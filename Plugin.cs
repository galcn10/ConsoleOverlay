using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace ConsoleOverlay
{
    // FASE 1:
    //  - Pulsación corta del atajo: salir al menú (juego pausado y silenciado, diálogo de Playnite con opciones)
    //    o, si ya estás en el menú, volver al juego.
    //  - Pulsación larga: menú de energía (suspender, reiniciar, apagar).
    //  - Segundo atajo: finalizar el juego directamente.
    public class ConsoleOverlayPlugin : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_NOREPEAT = 0x4000;
        private const int HOTKEY_ID = 0x5C01;
        private const int END_ID = 0x5C02;
        private const int WM_HOTKEY = 0x0312;

        private enum MenuPage
        {
            Main,
            Power
        }

        private enum MenuAction
        {
            ElevatedHelp,
            Back,
            Browse,
            EndGame,
            Power,
            Sleep,
            Restart,
            Shutdown,
            Cancel,
            Screenshot,
            Clip,
            Gallery,
            Achievements,
            Captures,
            RemoveAdmin
        }

        private static readonly string[] LauncherNames =
        {
            "steam", "steamwebhelper", "epicgameslauncher", "galaxyclient", "upc",
            "ubisoftconnect", "eadesktop", "origin", "battle.net",
            "playnite.desktopapp", "playnite.fullscreenapp", "explorer", "cmd",
            "conhost", "powershell"
        };

        private readonly ConsoleOverlaySettings settings;
        private HwndSource hwndSource;
        private readonly object gate = new object();
        private bool busy;
        private bool menuOpen;
        private bool dialogOpen;
        private Game currentGame;
        private int startedPid;
        private volatile bool playniteMinimizedByGuard;
        private CancellationTokenSource guardianCts;
        private DateTime gameStartUtc = DateTime.UtcNow;
        private List<int> pausedPids = new List<int>();
        private IntPtr gameHwnd = IntPtr.Zero;
        private IntPtr playniteHwnd = IntPtr.Zero;
        private int foregroundPid;
        private System.Threading.Timer watchdog;
        private volatile MenuWindow activeMenu;
        private Window curtain;
        private ImageSource coverCache;
        private int muteGen;
        private string coverFor;
        private ImageSource backdropCache;
        private List<AchievementItem> achCache;
        private string achFor;
        private List<CaptureItem> capCache;
        private string capFor;
        private string elevatedFlagExe;   // ejecutable (del juego o su lanzador) que tiene la marca de administrador del usuario
        private string elevatedWhy;       // por qué el juego actual se ejecuta como administrador (si se sabe)
        private bool playniteMuted;       // Playnite está silenciado por el menú
        private bool clipWasMuted;
        private long lastGameFgTick;   // Environment.TickCount de la última vez que el juego tuvo el primer plano
        private int lastGameFgPid;
        private readonly object learnLock = new object();
        private Dictionary<string, List<string>> learnedFolders;
        private string backdropFor;
        private IntPtr curtainHwnd = IntPtr.Zero;
        private readonly object guideLock = new object();
        private bool guideDown;
        private bool guideFired;
        private System.Threading.Timer guideTimer;

        public override Guid Id { get; } = new Guid("bc4b53fa-1c9f-46ee-bc39-4388ebd1c8e7");

        public ConsoleOverlayPlugin(IPlayniteAPI api) : base(api)
        {
            settings = new ConsoleOverlaySettings(this);
            LoadLocale();
            Properties = new GenericPluginProperties
            {
                HasSettings = true
            };
        }

        private void LoadLocale()
        {
            try
            {
                Loc.HostLanguage = PlayniteApi.ApplicationSettings.Language;
            }
            catch
            {
            }
            Loc.Init(GetPluginUserDataPath(), settings.Language);
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new ConsoleOverlaySettingsView();
        }

        private string RecoveryFile
        {
            get
            {
                string dir = GetPluginUserDataPath();
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "suspended.txt");
            }
        }

        // ------------------------------------------------------------------
        // Eventos de Playnite
        // ------------------------------------------------------------------

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            try
            {
                logger.Info("Console Overlay 1.0.0. Datos en: " + GetPluginUserDataPath());
                RecoverSuspended();
                watchdog = new System.Threading.Timer(_ => WatchdogTick(), null, 5000, 5000);

                if (PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Desktop &&
                    Process.GetProcessesByName("Playnite.FullscreenApp").Length > 0)
                {
                    logger.Info("Modo escritorio con Fullscreen abierto: no se registran los atajos aquí.");
                    return;
                }

                PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                {
                    hwndSource = new HwndSource(0, 0, 0, 0, 0, "ConsoleOverlayHotkey", new IntPtr(-3));
                    hwndSource.AddHook(WndProc);
                    RegisterHotkeys();
                });
                ShowFirstRunNotice();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error al iniciar el plugin");
            }
        }

        // Una sola vez: avisa de cómo se abre el menú (por defecto, botón Guía del mando o Select + Start).
        private void ShowFirstRunNotice()
        {
            try
            {
                if (settings.FirstRunDone)
                {
                    return;
                }
                List<string> ways = new List<string>();
                if (settings.UseGuideButton)
                {
                    ways.Add(Loc.T("el botón Guía del mando"));
                }
                if (settings.UseChordButton)
                {
                    ways.Add(Loc.T("Select + Start (1 s)"));
                }
                ways.Add(settings.Combo(settings.MenuKey));
                string text = Loc.T("Console Overlay: abre el menú durante el juego con {0}. Puedes cambiarlo en los ajustes del plugin.",
                    string.Join(Loc.T(" o "), ways));
                PlayniteApi.Notifications.Add(new NotificationMessage("ConsoleOverlay_firstrun", text, NotificationType.Info));
                logger.Info("Aviso: " + text);
                settings.FirstRunDone = true;
                SavePluginSettings(settings);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo mostrar el aviso inicial");
            }
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            try
            {
                SuppressPlayniteGuideFocus(false);
                if (watchdog != null)
                {
                    watchdog.Dispose();
                    watchdog = null;
                }
                lock (guideLock)
                {
                    if (guideTimer != null)
                    {
                        guideTimer.Dispose();
                        guideTimer = null;
                    }
                }
                List<int> pids;
                lock (gate)
                {
                    pids = new List<int>(pausedPids);
                }
                if (pids.Count > 0)
                {
                    Resume(pids);
                    if (settings.MuteGame)
                    {
                        AudioMuter.SetMute(pids, false);
                    }
                    DeleteRecovery();
                }
                PauseTracker.ResumeAll();
                CloseMenuWindow();
                HideCurtain();
                MutePlaynite(false);
                if (hwndSource != null)
                {
                    NativeMethods.UnregisterHotKey(hwndSource.Handle, HOTKEY_ID);
                    NativeMethods.UnregisterHotKey(hwndSource.Handle, END_ID);
                    hwndSource.Dispose();
                    hwndSource = null;
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error al cerrar el plugin");
            }
        }

        // Opcional: quita la marca "ejecutar como administrador" del usuario antes de lanzar el juego, así el juego
        // arranca con permisos normales y se puede pausar sin reiniciarlo después.
        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            try
            {
                if (!settings.AutoRemoveAdmin || args.Game == null)
                {
                    return;
                }
                string dir = null;
                if (!string.IsNullOrWhiteSpace(args.Game.InstallDirectory))
                {
                    dir = PlayniteApi.ExpandGameVariables(args.Game, args.Game.InstallDirectory);
                }
                List<string> changed = AdminFlag.RemoveUnder(dir);
                foreach (string c in changed)
                {
                    logger.Info("Antes de iniciar: quitada la marca de administrador de " + c);
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo revisar la marca de administrador");
            }
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            gameStartedAt = DateTime.UtcNow;
            browsing = false;
            SuppressPlayniteGuideFocus(true);
            List<int> leftover;
            lock (gate)
            {
                leftover = new List<int>(pausedPids);
                currentGame = args.Game;
                startedPid = args.StartedProcessId;
                gameStartUtc = DateTime.UtcNow;
                menuOpen = false;
                pausedPids.Clear();
            }
            foregroundPid = 0;
            noFocus = false;
            notPaused = false;
            mutedPids = new List<int>();
            logger.Info("Juego iniciado: " + args.Game.Name + " (PID inicial " + args.StartedProcessId + ")");
            StartGuardian();

            if (leftover.Count > 0)
            {
                logger.Warn("Había un juego pausado al iniciar otro; se reanuda: " + string.Join(",", leftover));
                Resume(leftover);
                AudioMuter.SetMute(leftover, false);
                DeleteRecovery();
            }

            // Un Playnite "siempre encima" taparía el juego recién iniciado.
            if (playniteHwnd != IntPtr.Zero)
            {
                NativeMethods.SetTopmost(playniteHwnd, false);
            }

            if (settings.MuteGame)
            {
                // Por si un cierre anterior dejó el juego silenciado en el mezclador de Windows.
                Task.Run(() =>
                {
                    try
                    {
                        Thread.Sleep(6000);
                        bool open;
                        lock (gate)
                        {
                            open = menuOpen;
                        }
                        if (!open)
                        {
                            AudioMuter.SetMute(FindGamePids(), false);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(ex, "No se pudo comprobar el silencio al iniciar");
                    }
                });
            }
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            try
            {
                SuppressPlayniteGuideFocus(false);
                List<int> pids;
                lock (gate)
                {
                    pids = new List<int>(pausedPids);
                }
                if (pids.Count > 0)
                {
                    Resume(pids);
                    if (settings.MuteGame)
                    {
                        AudioMuter.SetMute(pids, false);
                    }
                }
                if (playniteHwnd != IntPtr.Zero)
                {
                    NativeMethods.SetTopmost(playniteHwnd, false);
                }
                lock (gate)
                {
                    currentGame = null;
                    startedPid = 0;
                    menuOpen = false;
                    pausedPids.Clear();
                }
                foregroundPid = 0;
                browsing = false;
                PauseTracker.ResumeAll();
                DeleteRecovery();
                CloseMenuWindow();
                noFocus = false;
                notPaused = false;
                mutedPids = new List<int>();
                HideCurtain();
                MutePlaynite(false);
                StopGuardian();
                if (playniteMinimizedByGuard)
                {
                    // Playnite se minimizó para que no tapara al overlay de capturas: se restaura al cerrar el juego.
                    playniteMinimizedByGuard = false;
                    ActivatePlayniteWindow();
                }
                // Si Playnite sigue minimizado u oculto (p. ej. tras cerrar un juego de administrador), se trae de vuelta.
                EnsurePlayniteVisible(true);
                logger.Info("Juego detenido: " + args.Game.Name);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error en OnGameStopped");
            }
        }

        // Mando: navegación del menú y botón PS / Guide (pulsación corta y larga).
        public override void OnControllerButtonStateChanged(OnControllerButtonStateChangedArgs args)
        {
            try
            {
                string name = args.Button.ToString();
                bool pressed = args.State == ControllerInputState.Pressed;

                if (name == "Guide")
                {
                    if (pressed)
                    {
                        logger.Info("Mando: botón Guide pulsado");
                    }
                    if (settings.UseGuideButton && hwndSource != null)
                    {
                        OnGuide(pressed);
                    }
                    return;
                }

                if ((name == "Back" || name == "Start") && settings.UseChordButton && hwndSource != null)
                {
                    OnChord(name == "Back", pressed);
                }

                MenuWindow w = activeMenu;
                if (w != null && pressed)
                {
                    w.Dispatcher.BeginInvoke(new Action(() => w.HandleButton(name)));
                }
            }
            catch
            {
            }
        }

        // Select/Back + Start mantenidos: abre o cierra el menú (como una pulsación corta del botón Guía).
        private const int ChordHoldMs = 1000;
        private readonly object chordLock = new object();
        private bool backDown;
        private bool startDown;
        private bool chordFired;
        private System.Threading.Timer chordTimer;

        private void OnChord(bool isBack, bool pressed)
        {
            lock (chordLock)
            {
                if (isBack)
                {
                    backDown = pressed;
                }
                else
                {
                    startDown = pressed;
                }
                if (pressed && backDown && startDown && !chordFired)
                {
                    if (chordTimer != null)
                    {
                        chordTimer.Dispose();
                    }
                    chordTimer = new System.Threading.Timer(_ => ChordHeld(), null, ChordHoldMs, Timeout.Infinite);
                }
                else if (!pressed)
                {
                    if (chordTimer != null)
                    {
                        chordTimer.Dispose();
                        chordTimer = null;
                    }
                    if (!backDown && !startDown)
                    {
                        chordFired = false;
                    }
                }
            }
        }

        private void ChordHeld()
        {
            lock (chordLock)
            {
                if (!backDown || !startDown || chordFired)
                {
                    return;
                }
                chordFired = true;
            }
            logger.Info("Mando: Select + Start mantenidos");
            FireGuide(false);
        }

        private void OnGuide(bool pressed)
        {
            if (pressed)
            {
                lock (guideLock)
                {
                    if (guideDown)
                    {
                        return;
                    }
                    guideDown = true;
                    guideFired = false;
                    if (guideTimer != null)
                    {
                        guideTimer.Dispose();
                    }
                    guideTimer = new System.Threading.Timer(_ => GuideHeld(), null, settings.LongPressMs, Timeout.Infinite);
                }
                if (!guidePopSuppressed)
                {
                    Task.Run(() => PreCover());
                }
                return;
            }

            bool fire;
            lock (guideLock)
            {
                guideDown = false;
                fire = !guideFired;
                guideFired = true;
                if (guideTimer != null)
                {
                    guideTimer.Dispose();
                    guideTimer = null;
                }
            }
            if (fire)
            {
                FireGuide(false);
            }
        }

        // Playnite se pone delante por sí solo al pulsar PS. Para que no se vea, el telón oscuro se
        // coloca ya al pulsar (si hay un juego delante); si al final no se abre el menú, se quita.
        private void PreCover()
        {
            try
            {
                Game g;
                bool skip;
                lock (gate)
                {
                    g = currentGame;
                    skip = menuOpen || busy || dialogOpen;
                }
                // Playnite se adelanta a este código al pulsar PS: puede que el primer plano ya no sea el juego.
                // Por eso también vale que el juego lo tuviera hace un instante.
                bool recentlyInGame = lastGameFgPid != 0 && unchecked(Environment.TickCount - lastGameFgTick) < 1500;
                if (g == null || skip || activeMenu != null || !(IsForegroundGame(g) || recentlyInGame))
                {
                    return;
                }
                int fgp = ProcessGuard.GetForegroundPid();
                if (recentlyInGame && fgp != lastGameFgPid)
                {
                    fgp = lastGameFgPid;
                }
                if (fgp > 0 && NativeMethods.IsElevated(fgp) == 1 && NativeMethods.IsElevated(Process.GetCurrentProcess().Id) == 0)
                {
                    return; // juego elevado: no hay cambio de pantalla que tapar
                }
                ShowCurtain();
                Thread.Sleep(2500);
                lock (gate)
                {
                    skip = menuOpen || busy || dialogOpen;
                }
                if (!skip && activeMenu == null)
                {
                    HideCurtain();
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Error al preparar el telón");
            }
        }

        private bool IsForegroundGame(Game g)
        {
            int pid = ProcessGuard.GetForegroundPid();
            if (pid == 0 || pid == Process.GetCurrentProcess().Id)
            {
                return false;
            }
            if (pid == startedPid)
            {
                return true;
            }
            string path = NativeMethods.GetProcessPath(pid);
            if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(g.InstallDirectory))
            {
                return false;
            }
            try
            {
                string dir = PlayniteApi.ExpandGameVariables(g, g.InstallDirectory).TrimEnd('\\') + "\\";
                return path.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void GuideHeld()
        {
            lock (guideLock)
            {
                if (!guideDown || guideFired)
                {
                    return;
                }
                guideFired = true;
                guideDown = false;
            }
            FireGuide(true);
        }

        private void FireGuide(bool longPress)
        {
            logger.Info("Botón PS / Guide: " + (longPress ? "pulsación larga" : "pulsación corta"));
            MenuWindow w = activeMenu;
            if (w != null)
            {
                if (!longPress)
                {
                    w.Dispatcher.BeginInvoke(new Action(() => w.Cancel()));
                }
                return;
            }
            Task.Run(() => HandleGesture(longPress));
        }

        // Silencia el audio de Playnite (su música de fondo) mientras el menú del plugin está abierto.
        // Playnite crea su sesión de audio cuando le conviene, por eso se reaplica cada segundo.
        // Con delayMs > 0 el desilenciado se hace tarde, salvo que mientras tanto se vuelva a silenciar.
        private void MutePlaynite(bool mute, int delayMs = 0)
        {
            int gen = Interlocked.Increment(ref muteGen);
            playniteMuted = mute;
            if (mute)
            {
                ApplyPlayniteMute(true);
                Task.Run(() =>
                {
                    while (true)
                    {
                        Thread.Sleep(1000);
                        if (gen != Volatile.Read(ref muteGen))
                        {
                            return;
                        }
                        ApplyPlayniteMute(true);
                    }
                });
                return;
            }

            if (delayMs <= 0)
            {
                ApplyPlayniteMute(false);
                return;
            }
            Task.Run(() =>
            {
                Thread.Sleep(delayMs);
                if (gen == Volatile.Read(ref muteGen))
                {
                    ApplyPlayniteMute(false);
                }
            });
        }

        private static void ApplyPlayniteMute(bool mute)
        {
            try
            {
                AudioMuter.SetMute(new List<int> { Process.GetCurrentProcess().Id }, mute);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo cambiar el silencio de Playnite");
            }
        }

        // El botón Guide/PS lo gestiona este plugin. Si Playnite tiene activada "El botón Guide enfoca Playnite", Playnite se trae a sí
        // mismo al frente justo al pulsar PS (se ve un salto de su pantalla antes de que salga el menú). Mientras hay un juego en
        // marcha se activa en Playnite "solo cuando no hay juego en ejecución" (ajuste que ya existe) y se restaura al terminar.
        private bool guideRestoreNeeded;
        // true cuando Playnite ya no se adelanta al pulsar Guía mientras hay juego: no hace falta el telón previo.
        private bool guidePopSuppressed;

        private void SuppressPlayniteGuideFocus(bool on)
        {
            try
            {
                if (!on)
                {
                    guidePopSuppressed = false;
                }
                if (on == guideRestoreNeeded)
                {
                    return;
                }
                object api = PlayniteApi.ApplicationSettings;
                System.Reflection.PropertyInfo fp = api.GetType().GetProperty("Fullscreen");
                object fsApi = fp != null ? fp.GetValue(api, null) : null;
                System.Reflection.FieldInfo sf = fsApi != null
                    ? fsApi.GetType().GetField("settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    : null;
                object fs = sf != null ? sf.GetValue(fsApi) : null;
                if (fs == null)
                {
                    return;
                }
                System.Reflection.PropertyInfo focus = fs.GetType().GetProperty("GuideButtonFocus");
                System.Reflection.PropertyInfo only = fs.GetType().GetProperty("GuideButtonFocusOnlyWhenNoGameRunning");
                if (focus == null || only == null)
                {
                    return;
                }
                if (on)
                {
                    bool f = (bool)focus.GetValue(fs, null);
                    bool o = (bool)only.GetValue(fs, null);
                    guidePopSuppressed = true;
                    if (f && !o)
                    {
                        only.SetValue(fs, true, null);
                        guideRestoreNeeded = true;
                        logger.Info("Playnite: 'Guide enfoca Playnite' limitado a cuando no hay juego (se restaura al cerrar el juego).");
                    }
                }
                else
                {
                    only.SetValue(fs, false, null);
                    guideRestoreNeeded = false;
                    logger.Info("Playnite: ajuste del botón Guide restaurado.");
                }
            }
            catch (Exception ex)
            {
                logger.Info("No se pudo ajustar el botón Guide de Playnite: " + ex.Message);
            }
        }

        // Telón: ventana oscura a pantalla completa que tapa Playnite mientras dura el menú,
        // para que no se vea parpadear al cambiar de pantalla.
        private void ShowCurtain()
        {
            ShowCurtain(IntPtr.Zero);
        }

        private IntPtr curtainThumb = IntPtr.Zero;

        // freeze = ventana del juego cuya imagen (en directo o congelada si está pausado) se muestra a pantalla completa en el telón:
        // así, entre pantallas, se sigue viendo el juego y no un fondo oscuro ni Playnite. Sin imagen válida el telón es oscuro.
        private void ShowCurtain(IntPtr freeze)
        {
            try
            {
                PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                {
                    if (curtain != null)
                    {
                        return;
                    }
                    Window w = new Window
                    {
                        WindowStyle = WindowStyle.None,
                        ResizeMode = ResizeMode.NoResize,
                        ShowInTaskbar = false,
                        Topmost = true,
                        ShowActivated = false,
                        Focusable = false,
                        Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0F, 0x14)),
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = 0,
                        Top = 0,
                        Width = SystemParameters.PrimaryScreenWidth,
                        Height = SystemParameters.PrimaryScreenHeight
                    };
                    w.Show();
                    curtain = w;
                    curtainHwnd = new WindowInteropHelper(w).Handle;
                    NativeMethods.MakeClickThrough(curtainHwnd);
                    if (freeze != IntPtr.Zero && NativeMethods.IsWindow(freeze) && !NativeMethods.IsMinimized(freeze))
                    {
                        try
                        {
                            int sw, sh;
                            IntPtr th = DwmThumb.Register(curtainHwnd, freeze, out sw, out sh);
                            NativeMethods.RECT cr;
                            if (th != IntPtr.Zero && NativeMethods.GetWindowRect(curtainHwnd, out cr))
                            {
                                int cw = cr.Right - cr.Left, ch = cr.Bottom - cr.Top;
                                double ar = (double)sw / sh, car = (double)cw / ch;
                                // Solo si el juego ocupa la pantalla (si va en ventana, el telón oscuro es lo correcto).
                                if (cw > 0 && ch > 0 && Math.Abs(ar - car) < 0.05 && sw >= cw * 0.8)
                                {
                                    DwmThumb.Place(th, 0, 0, cw, ch, true);
                                    curtainThumb = th;
                                }
                                else
                                {
                                    DwmThumb.Unregister(th);
                                    logger.Info("Telón: el juego (" + sw + "x" + sh + ") no ocupa la pantalla (" + cw + "x" + ch + "); telón oscuro");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.Info("Telón: sin imagen del juego (" + ex.Message + ")");
                        }
                    }
                    // Esperar a que el telón se haya pintado de verdad antes de traer Playnite.
                    bool painted = false;
                    w.ContentRendered += (s2, e2) => painted = true;
                    DateTime limit = DateTime.UtcNow.AddMilliseconds(400);
                    while (!painted && DateTime.UtcNow < limit)
                    {
                        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
                        Thread.Sleep(10);
                    }
                    Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
                });
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo mostrar el telón");
            }
        }

        private void RaiseCurtain()
        {
            IntPtr h = curtainHwnd;
            if (h != IntPtr.Zero)
            {
                NativeMethods.RaiseTopmostNoActivate(h);
            }
        }

        private void HideCurtain()
        {
            HideCurtain(false);
        }

        // fade se conserva por compatibilidad: el telón ya no se desvanece (muestra la misma imagen que el juego, el corte no se nota).
        private void HideCurtain(bool fade)
        {
            try
            {
                if (curtain == null)
                {
                    return;
                }
                PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                {
                    Window w = curtain;
                    if (w == null)
                    {
                        return;
                    }
                    curtain = null;
                    curtainHwnd = IntPtr.Zero;
                    DwmThumb.Unregister(curtainThumb);
                    curtainThumb = IntPtr.Zero;
                    w.Close();
                });
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo quitar el telón");
            }
        }

        // Vigilante: si el juego pausado desaparece (cierre, fallo), limpia el estado del menú.
        private void WatchdogTick()
        {
            try
            {
                PauseTracker.Prune();
                List<int> pids;
                bool open;
                lock (gate)
                {
                    pids = new List<int>(pausedPids);
                    open = menuOpen;
                }
                if (!open || pids.Count == 0 || pids.Any(IsAlive))
                {
                    return;
                }
                logger.Warn("Vigilante: el juego pausado ya no existe; se limpia el estado del menú.");
                PauseTracker.Resume(pids);
                lock (gate)
                {
                    pausedPids.Clear();
                    menuOpen = false;
                }
                foregroundPid = 0;
                DeleteRecovery();
                CloseMenuWindow();
                HideCurtain();
                MutePlaynite(false);
                if (playniteHwnd != IntPtr.Zero)
                {
                    NativeMethods.SetTopmost(playniteHwnd, false);
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Error en el vigilante");
            }
        }

        // ------------------------------------------------------------------
        // Atajos
        // ------------------------------------------------------------------

        private void RegisterHotkeys()
        {
            if (hwndSource == null)
            {
                return;
            }
            NativeMethods.UnregisterHotKey(hwndSource.Handle, HOTKEY_ID);
            NativeMethods.UnregisterHotKey(hwndSource.Handle, END_ID);

            uint mods = MOD_NOREPEAT;
            if (settings.UseCtrl) mods |= MOD_CONTROL;
            if (settings.UseAlt) mods |= MOD_ALT;
            if (settings.UseShift) mods |= MOD_SHIFT;

            bool ok1 = NativeMethods.RegisterHotKey(hwndSource.Handle, HOTKEY_ID, mods, (uint)settings.MenuVk);
            bool ok2 = NativeMethods.RegisterHotKey(hwndSource.Handle, END_ID, mods, (uint)settings.EndVk);
            logger.Info("Atajo del menú " + settings.Combo(settings.MenuKey) + " registrado: " + ok1);
            logger.Info("Atajo de finalizar " + settings.Combo(settings.EndKey) + " registrado: " + ok2);
            if (!ok1)
            {
                Notify(Loc.T("No se pudo registrar {0}. Puede que otro programa lo esté usando.", settings.Combo(settings.MenuKey)));
            }
            if (!ok2)
            {
                Notify(Loc.T("No se pudo registrar {0}. Puede que otro programa lo esté usando.", settings.Combo(settings.EndKey)));
            }
        }

        // Se llama al guardar los ajustes.
        public void ApplySettings()
        {
            try
            {
                LoadLocale();
                PlayniteApi.MainView.UIDispatcher.Invoke(() => RegisterHotkeys());
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudieron aplicar los ajustes");
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HOTKEY_ID)
                {
                    handled = true;
                    Task.Run(() => OnMenuHotkey());
                }
                else if (id == END_ID)
                {
                    handled = true;
                    Task.Run(() => OnEndHotkey());
                }
            }
            return IntPtr.Zero;
        }

        // Mide cuánto tiempo se mantiene la tecla: corta = menú, larga = energía.
        private void OnMenuHotkey()
        {
            try
            {
                int vk = settings.MenuVk;
                int threshold = settings.LongPressMs;
                bool longPress = false;
                Stopwatch sw = Stopwatch.StartNew();
                while (true)
                {
                    if ((NativeMethods.GetAsyncKeyState(vk) & 0x8000) == 0)
                    {
                        break;
                    }
                    if (sw.ElapsedMilliseconds >= threshold)
                    {
                        longPress = true;
                        break;
                    }
                    Thread.Sleep(15);
                }
                logger.Info("Atajo del menú: " + (longPress ? "pulsación larga" : "pulsación corta") +
                            " (" + sw.ElapsedMilliseconds + " ms)");
                HandleGesture(longPress);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error en el atajo del menú");
            }
        }

        private void HandleGesture(bool longPress)
        {
            MenuWindow openMenu = activeMenu;
            if (openMenu != null)
            {
                if (!longPress)
                {
                    openMenu.Dispatcher.BeginInvoke(new Action(() => openMenu.Cancel()));
                }
                return;
            }

            bool dlg;
            lock (gate)
            {
                dlg = dialogOpen;
            }
            if (dlg)
            {
                // Con un diálogo abierto, la pulsación corta equivale a Esc (cancelar = volver al juego).
                if (!longPress)
                {
                    WaitKeysReleased(2000);
                    NativeMethods.TapKey(0x1B);
                }
                return;
            }

            lock (gate)
            {
                if (busy)
                {
                    return;
                }
                busy = true;
            }

            try
            {
                bool hasGame;
                bool open;
                lock (gate)
                {
                    hasGame = currentGame != null;
                    open = menuOpen;
                }

                if (!hasGame)
                {
                    // Sin juego en marcha el plugin no actúa (como una consola: solo dentro del juego),
                    // salvo que el usuario lo active en Ajustes.
                    if (settings.PowerMenuOutsideGame)
                    {
                        ShowMenuLoop(MenuPage.Power, false);
                    }
                    return;
                }

                if (!open)
                {
                    if (!EnterMenu())
                    {
                        return;
                    }
                    ShowMenuLoop(longPress ? MenuPage.Power : MenuPage.Main, true);
                }
                else if (longPress)
                {
                    ShowMenuLoop(MenuPage.Power, true);
                }
                else
                {
                    // El juego sigue pausado y el usuario está en Playnite: PS vuelve a abrir el menú
                    // (desde ahí, "Volver al juego" o PS otra vez reanudan).
                    ShowMenuLoop(MenuPage.Main, true);
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error en el menú");
                Notify(Loc.T("Error en el plugin. Revisa extensions.log."));
            }
            finally
            {
                lock (gate)
                {
                    busy = false;
                }
            }
        }

        private void OnEndHotkey()
        {
            lock (gate)
            {
                if (busy || dialogOpen)
                {
                    return;
                }
                busy = true;
            }
            try
            {
                EndGameCore();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error al finalizar el juego");
                Notify(Loc.T("Error al finalizar el juego. Revisa extensions.log."));
            }
            finally
            {
                lock (gate)
                {
                    busy = false;
                }
            }
        }

        // ------------------------------------------------------------------
        // Menú (diálogos de Playnite: se manejan con el mando en Fullscreen)
        // ------------------------------------------------------------------

        private void ShowMenuLoop(MenuPage page, bool hasGame)
        {
            try
            {
                ShowMenuLoopCore(page, hasGame);
            }
            finally
            {
                CloseMenuWindow();
            }
        }

        private void ShowMenuLoopCore(MenuPage page, bool hasGame)
        {
            MenuAction lastMain = MenuAction.Back; // la pantalla principal se reabre con el cursor donde estaba
            while (true)
            {
                MenuAction action = page == MenuPage.Main ? ShowMainPage(lastMain) : ShowPowerPage(hasGame);
                if (page == MenuPage.Main)
                {
                    lastMain = action;
                }
                switch (action)
                {
                    case MenuAction.Back:
                        CoverIfBrowsing();
                        CloseMenuWindow(true);
                        ReturnToGame();
                        return;

                    case MenuAction.Screenshot:
                        CoverIfBrowsing();
                        CloseMenuWindow(true);
                        ReturnToGame();
                        {
                            Game cg;
                            lock (gate)
                            {
                                cg = currentGame;
                            }
                            DateTime since = DateTime.Now.AddSeconds(-1);
                            SendCaptureKeys(settings.ScreenshotKeys, "captura");
                            LearnCaptureFolder(cg, since);
                        }
                        return;

                    case MenuAction.Clip:
                        CoverIfBrowsing();
                        CloseMenuWindow(true);
                        ReturnToGame();
                        SendCaptureKeys(settings.ClipKeys, "clip");
                        return;

                    case MenuAction.Gallery:
                        CoverIfBrowsing();
                        CloseMenuWindow(true);
                        ReturnToGame();
                        SendCaptureKeys(settings.GalleryKeys, "ver capturas");
                        return;

                    case MenuAction.Browse:
                        // Se queda en Playnite con el juego pausado. PS (o el atajo) vuelve a abrir el menú.
                        CloseMenuWindow();
                        MutePlaynite(false);
                        // Aquí sí hace falta Playnite "siempre encima", para verlo sobre el juego.
                        if (playniteHwnd != IntPtr.Zero)
                        {
                            NativeMethods.SetTopmost(playniteHwnd, true);
                        }
                        HideCurtain();
                        browsing = true;
                        ActivatePlayniteWindow();
                        return;

                    case MenuAction.ElevatedHelp:
                        Choose(Loc.T("Este juego se ejecuta como administrador. Para pausarlo y volver a Playnite desde aquí, Playnite también debe ejecutarse como administrador."),
                               Loc.T("Juego como administrador"),
                               new List<MenuEntry> { new MenuEntry("check", Loc.T("Entendido")) }, 0, 0, true);
                        page = MenuPage.Main;
                        break;

                    case MenuAction.RemoveAdmin:
                        if (Confirm(Loc.T("¿Quitar «ejecutar como administrador» a este juego? Se aplica la próxima vez que lo inicies. Si deja de funcionar, se puede volver a activar en Propiedades > Compatibilidad.")))
                        {
                            bool removed = AdminFlag.RemoveFromUser(notPausedExe);
                            logger.Info("Quitar RUNASADMIN de " + notPausedExe + ": " + removed);
                            Notify(removed ? Loc.T("Hecho. Cierra el juego y vuelve a abrirlo para que se pueda pausar.")
                                           : Loc.T("No se ha podido quitar la marca."));
                        }
                        page = MenuPage.Main;
                        break;

                    case MenuAction.Achievements:
                        ShowAchievementsPage();
                        page = MenuPage.Main;
                        break;

                    case MenuAction.Captures:
                        ShowCapturesPage();
                        page = MenuPage.Main;
                        break;

                    case MenuAction.Power:
                        page = MenuPage.Power;
                        break;

                    case MenuAction.Cancel:
                        if (hasGame)
                        {
                            page = MenuPage.Main;
                            break;
                        }
                        return;

                    case MenuAction.EndGame:
                        if (Confirm(noFocus
                            ? Loc.T("¿Finalizar el juego? Windows puede pedirte permiso de administrador. Se perderá el progreso que no esté guardado.")
                            : Loc.T("¿Finalizar el juego? Se perderá el progreso que no esté guardado.")))
                        {
                            CloseMenuWindow();
                            EndGameCore();
                            return;
                        }
                        page = MenuPage.Main;
                        break;

                    case MenuAction.Sleep:
                        if (Confirm(Loc.T("¿Suspender el equipo?")))
                        {
                            DoSleep();
                        }
                        page = hasGame ? MenuPage.Main : MenuPage.Power;
                        break;

                    case MenuAction.Restart:
                        if (Confirm(hasGame ? Loc.T("¿Reiniciar el equipo? Se cerrará el juego.") : Loc.T("¿Reiniciar el equipo?")))
                        {
                            CloseMenuWindow();
                            RunPowerCommand(settings.RestartCommand, hasGame);
                            return;
                        }
                        page = MenuPage.Power;
                        break;

                    case MenuAction.Shutdown:
                        if (Confirm(hasGame ? Loc.T("¿Apagar el equipo? Se cerrará el juego.") : Loc.T("¿Apagar el equipo?")))
                        {
                            CloseMenuWindow();
                            RunPowerCommand(settings.ShutdownCommand, hasGame);
                            return;
                        }
                        page = MenuPage.Power;
                        break;

                    default:
                        return;
                }
            }
        }

        private MenuAction ShowMainPage(MenuAction startOn)
        {
            List<MenuEntry> items = new List<MenuEntry>();
            List<MenuAction> map = new List<MenuAction>();
            items.Add(new MenuEntry("play", Loc.T("Volver al juego")));
            map.Add(MenuAction.Back);
            if (KeyChord.IsValid(settings.ScreenshotKeys))
            {
                items.Add(new MenuEntry("camera", Loc.T("Capturar pantalla")));
                map.Add(MenuAction.Screenshot);
            }
            if (KeyChord.IsValid(settings.ClipKeys))
            {
                items.Add(new MenuEntry("video", Loc.T("Guardar clip")));
                map.Add(MenuAction.Clip);
            }
            List<CaptureItem> caps = GetCaptures();
            if (caps != null && caps.Count > 0)
            {
                items.Add(new MenuEntry("images", Loc.T("Capturas y clips")) { Right = caps.Count.ToString() });
                map.Add(MenuAction.Captures);
            }
            else if (KeyChord.IsValid(settings.GalleryKeys))
            {
                // Sin capturas propias localizadas: se ofrece abrir el visor del programa de capturas.
                items.Add(new MenuEntry("images", Loc.T("Capturas y clips")) { Right = Loc.T("0 · abrir visor") });
                map.Add(MenuAction.Gallery);
            }
            List<AchievementItem> achs = GetAchievements();
            if (achs != null)
            {
                int got = achs.Count(x => x.Unlocked);
                items.Add(new MenuEntry("trophy", Loc.T("Logros")) { Right = got + " / " + achs.Count });
                map.Add(MenuAction.Achievements);
            }
            if (!noFocus)
            {
                items.Add(new MenuEntry("house", Loc.T("Ir a Playnite")));
                map.Add(MenuAction.Browse);
            }
            else
            {
                items.Add(new MenuEntry("lock", Loc.T("Ir a Playnite")) { Right = Loc.T("Playnite como administrador") });
                map.Add(MenuAction.ElevatedHelp);
            }
            if (notPaused && AdminFlag.HasInUser(notPausedExe))
            {
                items.Add(new MenuEntry("lock", Loc.T("Quitar modo administrador del juego")));
                map.Add(MenuAction.RemoveAdmin);
            }
            items.Add(noFocus
                ? new MenuEntry("circle-x", Loc.T("Finalizar juego")) { Danger = true, Right = Loc.T("pide permiso de Windows") }
                : new MenuEntry("circle-x", Loc.T("Finalizar juego")) { Danger = true });
            map.Add(MenuAction.EndGame);
            items.Add(new MenuEntry("power", Loc.T("Energía")));
            map.Add(MenuAction.Power);

            string sess = SessionText();
            string head = noFocus
                ? Loc.T("Juego como administrador · sin pausa · sesión {0}", sess)
                : (notPaused
                    ? (notPausedReason != null
                        ? Loc.T("Juego en marcha (no se pausa: {0}) · sesión {1}", Loc.T(notPausedReason), sess)
                        : Loc.T("Juego en marcha (no se puede pausar) · sesión {0}", sess))
                    : Loc.T("Juego pausado · sesión {0}", sess));
            int start = Math.Max(0, map.IndexOf(startOn));
            int idx = Choose(head, GameName(), items, start, 0, true);
            return idx >= 0 && idx < map.Count ? map[idx] : MenuAction.Back;
        }

        private MenuAction ShowPowerPage(bool hasGame)
        {
            List<MenuEntry> items = new List<MenuEntry>
            {
                new MenuEntry("moon", Loc.T("Suspender")),
                new MenuEntry("refresh-cw", Loc.T("Reiniciar")) { Danger = true },
                new MenuEntry("power", Loc.T("Apagar")) { Danger = true },
                new MenuEntry("arrow-left", Loc.T("Atrás"))
            };
            string text = hasGame
                ? Loc.T("Energía. Suspender deja el juego pausado. Reiniciar y apagar cierran antes el juego.")
                : Loc.T("Energía");
            int idx = Choose(text, Loc.T("Energía"), items, 0, 3, hasGame);
            switch (idx)
            {
                case 0: return MenuAction.Sleep;
                case 1: return MenuAction.Restart;
                case 2: return MenuAction.Shutdown;
                default: return MenuAction.Cancel;
            }
        }

        // Logros del juego actual (null si no hay plugin, datos o juego). Se lee una vez por menú.
        private List<AchievementItem> GetAchievements()
        {
            try
            {
                Game g;
                lock (gate)
                {
                    g = currentGame;
                }
                if (g == null)
                {
                    return null;
                }
                string id = g.Id.ToString();
                if (achFor == id)
                {
                    return achCache;
                }
                AchievementReader.Preload(PlayniteApi.Paths.ApplicationPath);
                List<AchievementItem> list = AchievementReader.Read(PlayniteApi.Paths.ExtensionsDataPath, g.Id,
                                                                    m => logger.Info(m));
                if (list != null)
                {
                    // Conseguidos (los más recientes primero), luego pendientes y al final los secretos.
                    list = list
                        .OrderBy(a => a.Unlocked ? 0 : (a.Hidden ? 2 : 1))
                        .ThenByDescending(a => a.UnlockTime ?? DateTime.MinValue)
                        .ThenByDescending(a => a.Percent ?? 0)
                        .ToList();
                }
                achCache = list;
                achFor = id;
                return list;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudieron cargar los logros");
                return null;
            }
        }

        // Nombre del juego y variantes con las que las herramientas de captura suelen nombrar carpetas y archivos.
        private List<string> CaptureNames(Game g)
        {
            List<string> names = new List<string>();
            names.Add(g.Name);
            foreach (string sep in new[] { ":", " - ", " – ", " — " })
            {
                int i = g.Name.IndexOf(sep, StringComparison.Ordinal);
                if (i >= 4)
                {
                    names.Add(g.Name.Substring(0, i));
                }
            }
            try
            {
                if (!string.IsNullOrWhiteSpace(g.InstallDirectory))
                {
                    string dir = PlayniteApi.ExpandGameVariables(g, g.InstallDirectory).TrimEnd('\\');
                    names.Add(Path.GetFileName(dir));
                }
                List<int> pids = FindGamePids();
                foreach (int pid in pids)
                {
                    string p = NativeMethods.GetProcessPath(pid);
                    if (!string.IsNullOrEmpty(p))
                    {
                        names.Add(Path.GetFileNameWithoutExtension(p));
                    }
                }
            }
            catch
            {
            }
            string[] generic = { "game", "launcher", "start", "play", "client", "main", "app", "shipping" };
            return names.Where(n => !string.IsNullOrWhiteSpace(n) && Array.IndexOf(generic, n.Trim().ToLowerInvariant()) < 0)
                        .Distinct().ToList();
        }

        private List<string> CaptureRoots()
        {
            List<string> roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(settings.CaptureFolders))
            {
                roots.AddRange(settings.CaptureFolders.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            }
            roots.AddRange(CaptureScanner.DefaultRoots());
            return roots;
        }

        // Carpetas donde se han visto aparecer capturas de este juego (se aprenden solas).
        private string LearnedFile()
        {
            return Path.Combine(GetPluginUserDataPath(), "capture_folders.txt");
        }

        private string lastColorInfo;
        private string lastFontInfo;

        // Imagen que sustituye a la miniatura si el juego no deja verse: su última captura o, si no hay, su fondo.
        private Tuple<ImageSource, bool> LoadPipFallback(ImageSource backdrop)
        {
            try
            {
                List<CaptureItem> caps = GetCaptures();
                CaptureItem best = null;
                if (caps != null)
                {
                    foreach (CaptureItem c in caps)
                    {
                        if (!c.IsVideo && (best == null || c.Time > best.Time))
                        {
                            best = c;
                        }
                    }
                }
                if (best != null && File.Exists(best.Path))
                {
                    BitmapImage bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.DecodePixelWidth = 840;
                    bi.UriSource = new Uri(best.Path);
                    bi.EndInit();
                    bi.Freeze();
                    return Tuple.Create<ImageSource, bool>(bi, true);
                }
            }
            catch (Exception ex)
            {
                logger.Info("No se pudo cargar la última captura: " + ex.Message);
            }
            return backdrop != null ? Tuple.Create(backdrop, false) : null;
        }

        // Lee fullscreenConfig.json de Playnite: "ButtonPrompts" es 0/"Xbox" o 1/"PlayStation". Devuelve null si no está.
        private string PlayniteButtonPrompts()
        {
            try
            {
                string file = Path.Combine(PlayniteApi.Paths.ConfigurationPath, "fullscreenConfig.json");
                if (!File.Exists(file))
                {
                    return null;
                }
                System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
                    File.ReadAllText(file), "\"ButtonPrompts\"\\s*:\\s*\"?(\\w+)\"?");
                if (!m.Success)
                {
                    return null;
                }
                string v = m.Groups[1].Value;
                if (v == "1" || string.Equals(v, "PlayStation", StringComparison.OrdinalIgnoreCase))
                {
                    return "PlayStation";
                }
                if (v == "0" || string.Equals(v, "Xbox", StringComparison.OrdinalIgnoreCase))
                {
                    return "Xbox";
                }
            }
            catch
            {
            }
            return null;
        }

        // "Auto": los iconos que el usuario tiene elegidos en Playnite; si no se pueden leer, según el mando conectado.
        private string ResolveButtonStyle()
        {
            string st = settings.ButtonStyle;
            if (string.Equals(st, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                // 1.º lo que el usuario tiene elegido en Playnite (Ajustes > Pantalla completa > Iconos de los botones);
                // 2.º si no se puede leer, el tipo de mando conectado.
                string fromPlaynite = PlayniteButtonPrompts();
                if (fromPlaynite != null)
                {
                    return fromPlaynite;
                }
                try
                {
                    ControllerBattery.Preload(PlayniteApi.Paths.ApplicationPath);
                    return ControllerBattery.GuessLayout();
                }
                catch
                {
                    return "Xbox";
                }
            }
            return st;
        }

        private List<string> LearnedFolders(string gameId)
        {
            lock (learnLock)
            {
                if (learnedFolders == null)
                {
                    learnedFolders = new Dictionary<string, List<string>>();
                    try
                    {
                        if (File.Exists(LearnedFile()))
                        {
                            foreach (string line in File.ReadAllLines(LearnedFile()))
                            {
                                int i = line.IndexOf('|');
                                if (i <= 0)
                                {
                                    continue;
                                }
                                string id = line.Substring(0, i);
                                if (!learnedFolders.ContainsKey(id))
                                {
                                    learnedFolders[id] = new List<string>();
                                }
                                learnedFolders[id].Add(line.Substring(i + 1));
                            }
                        }
                    }
                    catch
                    {
                    }
                }
                List<string> list;
                return learnedFolders.TryGetValue(gameId, out list) ? new List<string>(list) : new List<string>();
            }
        }

        private void RememberFolder(string gameId, string folder)
        {
            lock (learnLock)
            {
                LearnedFolders(gameId);
                List<string> list;
                if (!learnedFolders.TryGetValue(gameId, out list))
                {
                    list = new List<string>();
                    learnedFolders[gameId] = list;
                }
                if (list.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
                list.Add(folder);
                try
                {
                    List<string> lines = new List<string>();
                    foreach (KeyValuePair<string, List<string>> kv in learnedFolders)
                    {
                        foreach (string f in kv.Value)
                        {
                            lines.Add(kv.Key + "|" + f);
                        }
                    }
                    File.WriteAllLines(LearnedFile(), lines.ToArray());
                }
                catch
                {
                }
            }
        }

        // Tras pedir una captura, durante unos segundos vigila qué archivo nuevo aparece y en qué carpeta,
        // y se acuerda de ella para este juego. Solo corre esos segundos; luego no hace nada.
        private void LearnCaptureFolder(Game g, DateTime since)
        {
            if (g == null)
            {
                return;
            }
            string id = g.Id.ToString();
            string name = g.Name;
            Task.Run(() =>
            {
                try
                {
                    List<string> roots = CaptureRoots();
                    for (int i = 0; i < 10; i++)
                    {
                        Thread.Sleep(1000);
                        string file = CaptureScanner.FindNewCapture(roots, since);
                        if (file == null)
                        {
                            continue;
                        }
                        string dir = Path.GetDirectoryName(file);
                        bool isRoot = roots.Any(r =>
                        {
                            try
                            {
                                return string.Equals(Path.GetFullPath(r.Trim().Trim('"')).TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                            }
                            catch
                            {
                                return false;
                            }
                        });
                        logger.Info("Captura nueva de \"" + name + "\": " + file + (isRoot ? " (carpeta raíz: se reconoce por el nombre del archivo)" : " (carpeta recordada para este juego)"));
                        if (!isRoot)
                        {
                            RememberFolder(id, dir);
                        }
                        return;
                    }
                    logger.Info("Captura de \"" + name + "\": no ha aparecido ningún archivo nuevo en " + string.Join("; ", roots.Take(8)) +
                                ". Indica en Ajustes la carpeta donde tu programa guarda las capturas.");
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "No se pudo localizar la captura nueva");
                }
            });
        }

        // Capturas y clips del juego actual (null si no hay juego). Se buscan una vez por menú.
        private List<CaptureItem> GetCaptures()
        {
            try
            {
                Game g;
                lock (gate)
                {
                    g = currentGame;
                }
                if (g == null)
                {
                    return null;
                }
                string id = g.Id.ToString();
                if (capFor == id)
                {
                    return capCache;
                }
                List<string> roots = CaptureRoots();
                Stopwatch sw = Stopwatch.StartNew();
                List<string> direct = new List<string>();
                if (g.PluginId == new Guid("cb91dfc9-b977-43bf-8e70-55f46e410fab"))
                {
                    direct.AddRange(CaptureScanner.SteamFolders(g.GameId));
                }
                direct.AddRange(LearnedFolders(id));
                List<string> names = CaptureNames(g);
                List<CaptureItem> list = CaptureScanner.Scan(names, roots, 60, direct);
                logger.Info("Capturas de \"" + g.Name + "\": " + list.Count + " encontradas en " + sw.ElapsedMilliseconds + " ms");
                if (list.Count == 0)
                {
                    logger.Info("Capturas: nombres buscados [" + string.Join(" | ", names) + "]. Carpetas en las raíces: " + CaptureScanner.DescribeRoots(roots));
                }
                capCache = list;
                capFor = id;
                return list;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudieron buscar las capturas");
                return null;
            }
        }

        private void ShowCapturesPage()
        {
            List<CaptureItem> list = GetCaptures();
            if (list == null || list.Count == 0)
            {
                return;
            }
            try
            {
                ShowChoice(null, new List<MenuEntry> { new MenuEntry(null, null) }, 0, 0, true, null, list);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo mostrar la galería de capturas");
            }
        }

        private void ShowAchievementsPage()
        {
            List<AchievementItem> list = GetAchievements();
            if (list == null)
            {
                return;
            }
            try
            {
                ShowChoice(null, new List<MenuEntry> { new MenuEntry(null, null) }, 0, 0, true, list);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo mostrar la pantalla de logros");
            }
        }

        private bool Confirm(string text)
        {
            if (!settings.ConfirmActions)
            {
                return true;
            }
            List<MenuEntry> items = new List<MenuEntry>
            {
                new MenuEntry("check", Loc.T("Sí")),
                new MenuEntry("x", Loc.T("No"))
            };
            return Choose(text, Loc.T("Confirmar"), items, 1, 1, false) == 0;
        }

        // Guardián de foco: mientras hay un juego en marcha (y el menú no está abierto), Playnite no debe
        // quedar delante. Si aparece solo (p. ej. al cerrar el overlay del programa de capturas, porque el
        // juego se minimiza al perder el foco), se minimiza y se devuelve el foco a la ventana anterior
        // (si sigue visible) o al juego.
        private void StartGuardian()
        {
            StopGuardian();
            CancellationTokenSource cts = new CancellationTokenSource();
            guardianCts = cts;
            Task.Run(() => GuardianLoop(cts.Token));
        }

        private void StopGuardian()
        {
            CancellationTokenSource cts = guardianCts;
            guardianCts = null;
            if (cts != null)
            {
                cts.Cancel();
            }
        }

        private DateTime gameStartedAt = DateTime.MinValue;
        private string notPausedReason;
        private readonly Dictionary<string, string> antiCheatCache = new Dictionary<string, string>();
        private List<int> mutedPids = new List<int>(); // procesos del juego silenciados por el menú
        private bool notPaused;      // el menú está abierto con el juego sin pausar
        private bool noFocus;        // el juego es elevado: no se puede pasar el foco ni cerrar el juego
        private string notPausedExe;

        private void GuardianLoop(CancellationToken ct)
        {
            IntPtr lastOther = IntPtr.Zero;
            IntPtr lastSeen = IntPtr.Zero;
            bool seenGame = false;
            bool popped = false;
            DateTime poppedSince = DateTime.MinValue;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!settings.KeepGameInFront)
                    {
                        Thread.Sleep(1000);
                        continue;
                    }
                    Thread.Sleep(200);
                    Game g;
                    bool skip;
                    lock (gate)
                    {
                        g = currentGame;
                        skip = menuOpen || busy || dialogOpen;
                    }
                    if (g == null || skip || activeMenu != null)
                    {
                        continue;
                    }
                    IntPtr pn = playniteHwnd;
                    if (pn == IntPtr.Zero)
                    {
                        pn = GetPlayniteWindow();
                        if (pn == IntPtr.Zero)
                        {
                            continue;
                        }
                        playniteHwnd = pn;
                    }

                    IntPtr fg = NativeMethods.GetForegroundWindow();
                    if (fg != lastSeen)
                    {
                        logger.Info("Primer plano: " + NativeMethods.Describe(fg));
                        lastSeen = fg;
                    }

                    if (fg == pn)
                    {
                        if (!seenGame)
                        {
                            continue; // el juego aún se está abriendo
                        }
                        // Al abrirse el overlay del programa de capturas, Windows activa un momento Playnite
                        // hasta que el overlay termina de abrirse. No se interviene enseguida (cerraría el overlay):
                        // solo se manda Playnite al fondo y se espera. Si sigue ahí tras la espera, se devuelve el juego.
                        if (poppedSince == DateTime.MinValue)
                        {
                            poppedSince = DateTime.UtcNow;
                            popped = true;
                            NativeMethods.SendToBottom(pn);
                            continue;
                        }
                        // Justo al arrancar el juego no hay ningún overlay abierto: se actúa sin esperar (evita el parpadeo).
                        bool justStarted = (DateTime.UtcNow - gameStartedAt).TotalSeconds < 25;
                        if (!justStarted && (DateTime.UtcNow - poppedSince).TotalMilliseconds < 1200)
                        {
                            continue;
                        }
                        poppedSince = DateTime.MinValue;
                        IntPtr target = lastOther;
                        if (!IsUsableWindow(target, pn))
                        {
                            target = gameHwnd;
                            if (!IsUsableWindowAllowIconic(target))
                            {
                                target = FindMainWindow(FindGamePids());
                            }
                        }
                        if (target == IntPtr.Zero || !NativeMethods.IsWindow(target))
                        {
                            continue; // el juego ya no existe: no se toca nada
                        }
                        logger.Warn("Playnite ha aparecido solo durante el juego; se minimiza y se vuelve a " +
                                    NativeMethods.Describe(target) + ". Orden de ventanas:" + NativeMethods.DescribeZOrder(6));
                        NativeMethods.MinimizeNoActivate(pn);
                        playniteMinimizedByGuard = true;
                        NativeMethods.SendToBottom(pn);
                        NativeMethods.BringToFront(target, false);
                    }
                    else if (fg != IntPtr.Zero)
                    {
                        lastOther = fg;
                        poppedSince = DateTime.MinValue;
                        if (IsForegroundGame(g))
                        {
                            seenGame = true;
                            lastGameFgPid = ProcessGuard.GetForegroundPid();
                            lastGameFgTick = Environment.TickCount;
                        }
                        if ((popped || playniteMinimizedByGuard) && !NativeMethods.IsMinimized(pn) && NativeMethods.IsWindowVisible(pn))
                        {
                            // Playnite quedó visible detrás de la ventana con el foco: se minimiza sin activar nada.
                            logger.Info("Playnite seguía visible detrás de " + NativeMethods.Describe(fg) + "; se minimiza.");
                            NativeMethods.MinimizeNoActivate(pn);
                            playniteMinimizedByGuard = true;
                            popped = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "Error en el guardián de foco");
                    Thread.Sleep(1000);
                }
            }
        }

        private static bool IsUsableWindow(IntPtr h, IntPtr playnite)
        {
            if (h == IntPtr.Zero || h == playnite || !NativeMethods.IsWindow(h))
            {
                return false;
            }
            if (!NativeMethods.IsWindowVisible(h) || NativeMethods.IsMinimized(h))
            {
                return false;
            }
            string d = NativeMethods.Describe(h);
            return d.IndexOf("clase=Progman", StringComparison.Ordinal) < 0 &&
                   d.IndexOf("clase=WorkerW", StringComparison.Ordinal) < 0 &&
                   d.IndexOf("clase=Shell_TrayWnd", StringComparison.Ordinal) < 0;
        }

        private static bool IsUsableWindowAllowIconic(IntPtr h)
        {
            return h != IntPtr.Zero && NativeMethods.IsWindow(h);
        }

        // Vuelve al juego (ya hecho) y, cuando tiene el foco, envía el atajo del programa de capturas.
        private void SendCaptureKeys(string keys, string what)
        {
            try
            {
                Thread.Sleep(900);
                bool ok = KeyChord.Send(keys);
                logger.Info("Atajo de " + what + " (" + keys + ") enviado: " + ok +
                            ". Primer plano: " + NativeMethods.Describe(NativeMethods.GetForegroundWindow()));
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo enviar el atajo de " + what);
            }
        }

        // Muestra el menú visual; si no se puede crear la ventana, usa el diálogo de Playnite.
        private int Choose(string message, string caption, List<MenuEntry> items, int defaultIndex, int cancelIndex, bool showGame)
        {
            try
            {
                return ShowChoice(message, items, defaultIndex, cancelIndex, showGame);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Menú visual no disponible; se usa el diálogo de Playnite.");
            }

            List<MessageBoxOption> opts = new List<MessageBoxOption>();
            for (int i = 0; i < items.Count; i++)
            {
                opts.Add(new MessageBoxOption(items[i].Text, i == defaultIndex, i == cancelIndex));
            }
            return Pick(ShowOptions(message, caption, opts), opts);
        }

        private int ShowChoice(string message, List<MenuEntry> items, int defaultIndex, int cancelIndex, bool showGame,
                               List<AchievementItem> achList = null, List<CaptureItem> galList = null)
        {
            lock (gate)
            {
                dialogOpen = true;
            }
            MenuWindow w = null;
            try
            {
                int result = -1;
                using (ManualResetEventSlim evt = new ManualResetEventSlim(false))
                {
                    Action<int> handler = r =>
                    {
                        try
                        {
                            result = r;
                            evt.Set();
                        }
                        catch
                        {
                        }
                    };

                    PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                    {
                        Game g;
                        lock (gate)
                        {
                            g = currentGame;
                        }
                        string title = null;
                        string total = null;
                        ImageSource cover = null;
                        ImageSource backdrop = null;
                        string text = message;
                        if (showGame && g != null)
                        {
                            title = g.Name;
                            cover = GetCover(g);
                            backdrop = GetBackdrop(g);
                            total = Loc.T("Total {0}", FormatPlaytime(g.Playtime));
                            text = null;
                        }
                        Func<string> session = () => Loc.T("Sesión {0}", SessionText());
                        string achSummary = null;
                        string galSummary = null;
                        if (galList != null)
                        {
                            int nv = galList.Count(x => x.IsVideo);
                            galSummary = Loc.T("{0} capturas · {1} clips", galList.Count - nv, nv);
                        }
                        double achFraction = 0;
                        if (achList != null)
                        {
                            int got = achList.Count(x => x.Unlocked);
                            achFraction = achList.Count > 0 ? (double)got / achList.Count : 0;
                            achSummary = got + " de " + achList.Count + " · " + (int)Math.Round(achFraction * 100) + " %";
                        }

                        w = activeMenu;
                        if (w == null)
                        {
                            string colorInfo;
                            Color? accent = Palette.Choose(settings.MenuColors, cover, backdrop, out colorInfo);
                            if (colorInfo != lastColorInfo)
                            {
                                lastColorInfo = colorInfo;
                                logger.Info("Colores del menú (" + settings.MenuColors + "): " + colorInfo +
                                            (accent != null ? " -> #" + accent.Value.R.ToString("X2") + accent.Value.G.ToString("X2") + accent.Value.B.ToString("X2") : ""));
                            }
                            string fontInfo;
                            FontFamily themeFont = Palette.ThemeFont(out fontInfo);
                            if (fontInfo != lastFontInfo)
                            {
                                lastFontInfo = fontInfo;
                                logger.Info("Tipografía del menú: " + fontInfo);
                            }
                            MenuWindow created = new MenuWindow(title, text, items, defaultIndex, cancelIndex, cover, backdrop,
                                                                session, total, ResolveButtonStyle(), settings.UseGuideButton, accent, themeFont);
                            created.OnChosen = handler;
                            created.NoFocus = noFocus;
                            created.PipFallbackLoader = () => LoadPipFallback(backdrop);
                            if (settings.ShowLivePreview && gameHwnd != IntPtr.Zero && NativeMethods.IsWindow(gameHwnd))
                            {
                                created.PipSource = gameHwnd;
                                created.PipLive = notPaused;
                            }
                            created.ClipAudio = on =>
                            {
                                // Los clips suenan por Playnite: mientras se reproducen se le quita el silencio del menú.
                                if (on)
                                {
                                    clipWasMuted = clipWasMuted || playniteMuted;
                                    MutePlaynite(false);
                                    int n = 0;
                                    try
                                    {
                                        n = AudioMuter.SetMute(new List<int> { Process.GetCurrentProcess().Id }, false, true);
                                    }
                                    catch
                                    {
                                    }
                                    logger.Info("Audio del clip: sesiones de Playnite con sonido activado = " + n);
                                }
                                else if (clipWasMuted)
                                {
                                    clipWasMuted = false;
                                    MutePlaynite(true);
                                }
                            };
                            if (settings.ShowBattery)
                            {
                                created.BatteryProvider = () =>
                                {
                                    ControllerBattery.Preload(PlayniteApi.Paths.ApplicationPath);
                                    return ControllerBattery.Read();
                                };
                            }
                            if (achList != null)
                            {
                                created.SetAchievements(title, achSummary, achFraction, achList, cover, backdrop);
                            }
                            else if (galList != null)
                            {
                                created.SetGallery(title, galSummary, galList, backdrop);
                            }
                            activeMenu = created;
                            w = created;
                            PlayniteApi.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    created.ShowDialog();
                                }
                                catch (Exception ex)
                                {
                                    logger.Warn(ex, "Error en la ventana del menú");
                                    handler(-1);
                                }
                            }));
                        }
                        else
                        {
                            w.OnChosen = handler;
                            if (achList != null)
                            {
                                w.SetAchievements(title, achSummary, achFraction, achList, cover, backdrop);
                            }
                            else if (galList != null)
                            {
                                w.SetGallery(title, galSummary, galList, backdrop);
                            }
                            else
                            {
                                w.SetPage(title, text, items, defaultIndex, cancelIndex, cover, backdrop, session, total);
                            }
                        }
                    });

                    evt.Wait();
                }
                return result;
            }
            finally
            {
                if (w != null)
                {
                    w.OnChosen = null;
                }
                lock (gate)
                {
                    dialogOpen = false;
                }
            }
        }

        // Cierra la ventana del menú (si está abierta). El telón sigue tapando Playnite.
        private void CloseMenuWindow()
        {
            CloseMenuWindow(false);
        }

        private void CloseMenuWindow(bool animate)
        {
            try
            {
                if (activeMenu == null)
                {
                    return;
                }
                PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                {
                    MenuWindow w = activeMenu;
                    activeMenu = null;
                    coverCache = null;
                    coverFor = null;
                    backdropCache = null;
                    backdropFor = null;
                    achCache = null;
                    achFor = null;
                    capCache = null;
                    capFor = null;
                    if (w != null)
                    {
                        if (animate)
                        {
                            w.CloseAnimated();
                        }
                        else
                        {
                            w.Close();
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo cerrar la ventana del menú");
            }
        }

        private ImageSource GetCover(Game g)
        {
            string id = g.Id.ToString();
            if (coverCache != null && coverFor == id)
            {
                return coverCache;
            }
            ImageSource c = LoadCover(g);
            coverCache = c;
            coverFor = c != null ? id : null;
            return c;
        }

        private ImageSource LoadCover(Game g)
        {
            string[] candidates = { g.CoverImage, g.Icon, g.BackgroundImage };
            foreach (string c in candidates)
            {
                ImageSource img = LoadImage(c, 0, 720);
                if (img != null)
                {
                    return img;
                }
            }
            return null;
        }

        // Fondo del menú: la imagen de fondo del juego; si no tiene, la portada
        // decodificada en miniatura (al ampliarla queda difuminada y es muy barata).
        private ImageSource GetBackdrop(Game g)
        {
            string id = g.Id.ToString();
            if (backdropCache != null && backdropFor == id)
            {
                return backdropCache;
            }
            ImageSource b = LoadImage(g.BackgroundImage, 1280, 0);
            if (b == null)
            {
                string[] alt = { g.CoverImage, g.Icon };
                foreach (string c in alt)
                {
                    b = LoadImage(c, 0, 54);
                    if (b != null)
                    {
                        break;
                    }
                }
            }
            backdropCache = b;
            backdropFor = b != null ? id : null;
            return b;
        }

        private ImageSource LoadImage(string dbPath, int pixelWidth, int pixelHeight)
        {
            if (string.IsNullOrEmpty(dbPath))
            {
                return null;
            }
            try
            {
                string path = PlayniteApi.Database.GetFullFilePath(dbPath);
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return null;
                }
                BitmapImage bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                if (pixelWidth > 0)
                {
                    bi.DecodePixelWidth = pixelWidth;
                }
                if (pixelHeight > 0)
                {
                    bi.DecodePixelHeight = pixelHeight;
                }
                bi.UriSource = new Uri(path);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo cargar la imagen del juego");
                return null;
            }
        }

        private static string FormatPlaytime(ulong seconds)
        {
            ulong h = seconds / 3600;
            ulong m = (seconds % 3600) / 60;
            if (h > 0)
            {
                return Loc.T("{0} h {1} min", h, m);
            }
            return seconds == 0 ? Loc.T("sin registrar") : Loc.T("{0} min", m);
        }

        private MessageBoxOption ShowOptions(string text, string caption, List<MessageBoxOption> options)
        {
            lock (gate)
            {
                dialogOpen = true;
            }
            try
            {
                MessageBoxOption result = null;
                PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                {
                    result = PlayniteApi.Dialogs.ShowMessage(text, caption, MessageBoxImage.None, options);
                });
                return result;
            }
            finally
            {
                lock (gate)
                {
                    dialogOpen = false;
                }
            }
        }

        private static int Pick(MessageBoxOption result, List<MessageBoxOption> options)
        {
            if (result == null)
            {
                return -1;
            }
            int i = options.IndexOf(result);
            if (i >= 0)
            {
                return i;
            }
            for (int k = 0; k < options.Count; k++)
            {
                if (options[k].Title == result.Title)
                {
                    return k;
                }
            }
            return -1;
        }

        private string GameName()
        {
            Game g;
            lock (gate)
            {
                g = currentGame;
            }
            return g != null ? g.Name : Loc.T("Juego");
        }

        private string SessionText()
        {
            TimeSpan t = DateTime.UtcNow - gameStartUtc;
            if (t.TotalHours >= 1)
            {
                return Loc.T("{0} h {1} min", (int)t.TotalHours, t.Minutes);
            }
            return Loc.T("{0} min", Math.Max(0, (int)t.TotalMinutes));
        }

        // ------------------------------------------------------------------
        // Acciones de energía
        // ------------------------------------------------------------------

        private void DoSleep()
        {
            logger.Info("Suspender equipo.");
            Thread.Sleep(500);
            bool ok = NativeMethods.SetSuspendState(false, false, false);
            logger.Info("Equipo reanudado tras la suspensión: " + ok);
        }

        private void RunPowerCommand(string command, bool hasGame)
        {
            logger.Info("Acción de energía: " + command + (hasGame ? " (se cierra antes el juego)" : ""));
            if (hasGame)
            {
                // Un juego pausado impediría que Windows cierre la sesión.
                EndGameCore();
            }
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c " + command)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
        }

        // ------------------------------------------------------------------
        // Salir al menú: Playnite al frente, luego silenciar y pausar el juego.
        // El orden importa: si el juego se pausara antes, no podría liberar
        // la pantalla completa exclusiva y Playnite no se vería.
        // ------------------------------------------------------------------

        private bool EnterMenu()
        {
            Game game;
            lock (gate)
            {
                game = currentGame;
            }
            if (game == null)
            {
                Notify(Loc.T("No hay ningún juego en marcha."));
                return false;
            }

            // Antes de traer Playnite al frente: la ventana en primer plano es, casi seguro, la del juego.
            foregroundPid = CaptureForegroundPid();

            List<int> pids = FindGamePids();
            if (pids.Count == 0)
            {
                // Si lo que hay delante es Playnite (menú principal, el juego aún arrancando o ya cerrado) no hay nada que hacer:
                // el plugin solo actúa dentro de un juego. Solo se avisa si hay otra aplicación delante.
                bool inPlaynite = foregroundPid == 0 || foregroundPid == Process.GetCurrentProcess().Id;
                logger.Info("Salir al menú: no se encuentran procesos del juego" + (inPlaynite ? " (Playnite está delante; se ignora)" : ""));
                foregroundPid = 0;
                if (!inPlaynite)
                {
                    Notify(Loc.T("No encuentro los procesos del juego. No se ha pausado nada."));
                }
                return false;
            }

            gameHwnd = FindMainWindow(pids);
            logger.Info("Salir al menú. Juego: " + game.Name + ". PIDs: " + string.Join(",", pids) +
                        ". Ventana: " + NativeMethods.Describe(gameHwnd));

            WaitKeysReleased(2000);

            // Juego elevado (administrador) con un Playnite normal: Windows no deja traer Playnite al frente,
            // pausar ni cerrar el juego. Se resuelve según el ajuste, sin intentos que solo causarían parpadeos.
            notPaused = false;
            noFocus = false;
            notPausedExe = null;
            notPausedReason = null;
            elevatedWhy = null;
            uint mainPidE = 0;
            if (gameHwnd != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(gameHwnd, out mainPidE);
            }
            if (mainPidE == 0)
            {
                mainPidE = (uint)pids[0];
            }
            if (NativeMethods.IsElevated((int)mainPidE) == 1 && NativeMethods.IsElevated(Process.GetCurrentProcess().Id) == 0)
            {
                return EnterElevatedGame(pids, (int)mainPidE);
            }

            // Playnite NO se pone "siempre encima": así el telón y el menú quedan siempre por encima
            // de él (si Playnite fuera "siempre encima", al activarse taparía el telón y parpadearía).
            ShowCurtain(gameHwnd);
            MutePlaynite(true);
            playniteHwnd = GetPlayniteWindow();
            NativeMethods.SetTopmost(playniteHwnd, false);
            bool front = NativeMethods.BringToFront(playniteHwnd, false);
            playniteMinimizedByGuard = false;
            RaiseCurtain();
            logger.Info("Playnite al frente: " + front + " (" + NativeMethods.Describe(playniteHwnd) + ")");
            if (!front)
            {
                try
                {
                    uint pnPid;
                    NativeMethods.GetWindowThreadProcessId(playniteHwnd, out pnPid);
                    string ge = "";
                    foreach (int gp in pids)
                    {
                        ge += " " + gp + "=" + NativeMethods.IsElevated(gp);
                    }
                    logger.Warn("Fallo de foco. Elevación (1 sí, 0 no, -1 desconocido/protegido): Playnite=" +
                                NativeMethods.IsElevated((int)pnPid) + ", juego:" + ge +
                                ". Primer plano ahora: " + NativeMethods.Describe(NativeMethods.GetForegroundWindow()));
                }
                catch
                {
                }
                HideCurtain();
                MutePlaynite(false);
                Notify(Loc.T("No se pudo traer Playnite al frente. El juego NO se ha pausado."));
                return false;
            }

            // Activar de verdad la ventana de Playnite para que reciba teclado y mando.
            Thread.Sleep(40);
            ActivatePlayniteWindow();
            RaiseCurtain();
            // Se espera solo lo que haga falta: en cuanto Playnite tiene el foco (suele ser inmediato) se sigue.
            for (int spin = 0; spin < 14 && !NativeMethods.IsForeground(playniteHwnd); spin++)
            {
                Thread.Sleep(40);
            }
            Thread.Sleep(60);
            IntPtr fg = NativeMethods.GetForegroundWindow();
            logger.Info("Primer plano tras activar Playnite: " + NativeMethods.Describe(fg) +
                        (fg == playniteHwnd ? " (es la ventana principal de Playnite)" : " (NO es la ventana principal de Playnite)"));

            if (settings.MuteGame)
            {
                mutedPids = new List<int>(pids);
                AudioMuter.SetMute(pids, true);
            }

            try
            {
                File.WriteAllText(RecoveryFile, string.Join(",", pids));
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo escribir el archivo de recuperación");
            }

            uint mainPid = 0;
            if (gameHwnd != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(gameHwnd, out mainPid);
            }

            List<int> done = new List<int>();
            bool allOk = true;
            string noPause = NoPauseReason(game, pids);
            notPausedReason = noPause;
            if (noPause != null)
            {
                logger.Info("No se pausa el juego (" + noPause + "). El menú se abre con el juego en marcha.");
                notPaused = true;
            }
            try
            {
                foreach (int pid in noPause != null ? new List<int>() : pids)
                {
                    string reason;
                    if (PauseTracker.Suspend(pid, out reason))
                    {
                        done.Add(pid);
                    }
                    else
                    {
                        logger.Warn("No se pudo pausar el PID " + pid + " [" +
                                    (NativeMethods.GetProcessPath(pid) ?? "ruta desconocida") + "]: " + reason +
                                    (pid == (int)mainPid ? " (ES EL PROCESO DE LA VENTANA DEL JUEGO)" : ""));
                        if (mainPid == 0 || pid == (int)mainPid)
                        {
                            allOk = false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error inesperado al pausar; se deshace.");
                allOk = false;
            }

            if (!allOk && settings.ElevatedMode == 0)
            {
                // El juego no se puede pausar (p. ej. se ejecuta como administrador): se abre el menú igualmente,
                // con el juego en marcha detrás y silenciado, y se avisa en el propio menú.
                notPaused = true;
                notPausedExe = mainPid != 0 ? NativeMethods.GetProcessPath((int)mainPid) : null;
                lock (gate)
                {
                    pausedPids = done;
                    menuOpen = true;
                }
                logger.Warn("El juego no se puede pausar; se abre el menú sin pausar. Marca de administrador en HKCU: " +
                            AdminFlag.HasInUser(notPausedExe) + ", en HKLM: " + AdminFlag.HasInMachine(notPausedExe));
                return true;
            }

            if (!allOk)
            {
                foregroundPid = 0;
                Resume(done);
                if (settings.MuteGame)
                {
                    AudioMuter.SetMute(pids, false);
                }
                DeleteRecovery();

                // Dejarlo todo como estaba: quitar "siempre encima" a Playnite y devolver el juego al frente.
                if (playniteHwnd != IntPtr.Zero)
                {
                    NativeMethods.SetTopmost(playniteHwnd, false);
                    NativeMethods.SendToBottom(playniteHwnd);
                }
                IntPtr back = gameHwnd;
                if (back == IntPtr.Zero || !NativeMethods.IsWindow(back))
                {
                    back = FindMainWindow(pids);
                }
                bool restored = NativeMethods.BringToFront(back, false);
                HideCurtain();
                MutePlaynite(false, 1500);
                logger.Warn("No se pudo pausar el juego. Se devuelve al juego: " + restored);
                ShowNotice(Loc.T("Este juego no se puede pausar. Ejecuta Playnite como administrador o cambia la opción en los ajustes de Console Overlay."));
                return false;
            }

            lock (gate)
            {
                pausedPids = done;
                menuOpen = true;
            }
            logger.Info("Juego pausado y silenciado.");
            return true;
        }

        private static readonly string[] AntiCheatMarkers =
        {
            "easyanticheat", "battleye", "beservice", "beclient", "gameguard", "npggnt", "anticheatexpert",
            "ace-base", "ace-service", "xigncode", "punkbuster", "pbsvc", "faceitclient", "eaanticheat", "vanguard"
        };
        private static readonly string[] AntiCheatProcesses =
        {
            "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "BEService_x64", "vgc", "vgtray", "GameGuard", "AntiCheatExpert", "ACE-Service"
        };

        // Motivo por el que NO se debe pausar este juego (null = se puede pausar). Pausar un juego online o con anticheat
        // puede desconectar de la partida o activar sanciones.
        private string NoPauseReason(Game g, List<int> pids)
        {
            try
            {
                if (g.Tags != null && g.Tags.Any(t => t != null && t.Name != null &&
                    (t.Name.Equals("No pausar", StringComparison.OrdinalIgnoreCase) ||
                     t.Name.Equals("NoPause", StringComparison.OrdinalIgnoreCase))))
                {
                    return "etiqueta «No pausar»";
                }
                if (!settings.NoPauseAntiCheat)
                {
                    return null;
                }
                foreach (string pn in AntiCheatProcesses)
                {
                    if (Process.GetProcessesByName(pn).Length > 0)
                    {
                        return "anticheat activo";
                    }
                }
                string key = g.Id.ToString();
                string cached;
                if (antiCheatCache.TryGetValue(key, out cached))
                {
                    return cached;
                }
                string reason = null;
                if (!string.IsNullOrWhiteSpace(g.InstallDirectory))
                {
                    string dir = PlayniteApi.ExpandGameVariables(g, g.InstallDirectory);
                    if (Directory.Exists(dir) && FindAntiCheatFile(dir, 0))
                    {
                        reason = "anticheat";
                    }
                }
                antiCheatCache[key] = reason;
                return reason;
            }
            catch
            {
                return null;
            }
        }

        private static bool FindAntiCheatFile(string dir, int depth)
        {
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    string n = Path.GetFileName(entry).ToLowerInvariant();
                    foreach (string m in AntiCheatMarkers)
                    {
                        if (n.Contains(m))
                        {
                            return true;
                        }
                    }
                    if (depth < 2 && Directory.Exists(entry) && FindAntiCheatFile(entry, depth + 1))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        // Juego que se ejecuta como administrador y Playnite normal.
        private bool EnterElevatedGame(List<int> pids, int mainPid)
        {
            string exe = NativeMethods.GetProcessPath(mainPid);
            logger.Warn("Juego elevado (administrador): " + (exe ?? "ruta desconocida") + ". Marca RUNASADMIN en HKCU: " +
                        AdminFlag.HasInUser(exe) + ", en HKLM: " + AdminFlag.HasInMachine(exe) +
                        ". Modo: " + (settings.ElevatedMode == 0 ? "abrir sin pausar" : "solo avisar"));
            if (settings.ElevatedMode != 0)
            {
                foregroundPid = 0;
                ShowNotice(Loc.T("Juego como administrador: no se puede pausar. Ejecuta Playnite también como administrador."));
                return false;
            }
            notPaused = true;
            noFocus = true;
            notPausedExe = exe;
            notPausedReason = "juego como administrador";
            elevatedWhy = ExplainElevation(pids, exe);
            if (elevatedFlagExe != null)
            {
                notPausedExe = elevatedFlagExe;   // la marca que se puede quitar está en este ejecutable
            }
            if (settings.MuteGame)
            {
                mutedPids = new List<int>(pids);
                AudioMuter.SetMute(pids, true);
            }
            lock (gate)
            {
                pausedPids = new List<int>();
                menuOpen = true;
            }
            return true;
        }

        // Averigua (y deja en el log) por qué el juego se ejecuta como administrador: marca de compatibilidad
        // en alguno de sus ejecutables o un proceso padre (Steam, un lanzador...) que ya era administrador.
        private string ExplainElevation(List<int> pids, string mainExe)
        {
            string why = null;
            elevatedFlagExe = null;
            try
            {
                StringBuilder sb = new StringBuilder("Procesos del juego:");
                foreach (int pid in pids)
                {
                    string path = NativeMethods.GetProcessPath(pid);
                    int el = NativeMethods.IsElevated(pid);
                    int ppid = 0;
                    try
                    {
                        using (ManagementObjectSearcher q = new ManagementObjectSearcher(
                            "SELECT ParentProcessId FROM Win32_Process WHERE ProcessId=" + pid))
                        {
                            foreach (ManagementObject o in q.Get())
                            {
                                ppid = Convert.ToInt32(o["ParentProcessId"]);
                            }
                        }
                    }
                    catch
                    {
                    }
                    string ppath = ppid > 0 ? NativeMethods.GetProcessPath(ppid) : null;
                    int pel = ppid > 0 ? NativeMethods.IsElevated(ppid) : -1;
                    sb.Append(" [").Append(pid).Append(" ").Append(path ?? "?").Append(" elevado=").Append(el)
                      .Append(" | padre ").Append(ppid).Append(" ").Append(ppath ?? "?").Append(" elevado=").Append(pel).Append("]");

                    if (el == 1 && path != null && elevatedFlagExe == null && AdminFlag.HasInUser(path))
                    {
                        elevatedFlagExe = path;
                    }
                    if (why == null && el == 1)
                    {
                        if (path != null && (AdminFlag.HasInUser(path) || AdminFlag.HasInMachine(path)))
                        {
                            why = "el juego tiene activado «ejecutar como administrador» en Windows";
                        }
                        else if (ppid > 0 && pel == 1 && ppath != null &&
                                 !string.Equals(Path.GetFileNameWithoutExtension(ppath), "services", StringComparison.OrdinalIgnoreCase))
                        {
                            why = "lo abre «" + Path.GetFileName(ppath) + "», que se ejecuta como administrador";
                        }
                    }
                }
                logger.Info(sb.ToString());
                if (elevatedFlagExe != null)
                {
                    why = "el juego tiene activado «ejecutar como administrador» en Windows";
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo analizar por qué el juego es administrador");
            }
            if (why == null)
            {
                why = "el propio juego pide permisos de administrador al abrirse";
            }
            logger.Info("Juego como administrador: " + why);
            return why;
        }

        // Aviso breve en pantalla (ventana encima del juego, sin quitarle el foco).
        private void ShowNotice(string text)
        {
            logger.Info("Aviso: " + text);
            try
            {
                PlayniteApi.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        TextBlock tb = new TextBlock
                        {
                            Text = text,
                            Foreground = Brushes.White,
                            FontSize = 26,
                            TextWrapping = TextWrapping.Wrap,
                            MaxWidth = 1100,
                            Margin = new Thickness(36, 24, 36, 24)
                        };
                        Window w = new Window
                        {
                            WindowStyle = WindowStyle.None,
                            ResizeMode = ResizeMode.NoResize,
                            ShowInTaskbar = false,
                            Topmost = true,
                            ShowActivated = false,
                            Focusable = false,
                            AllowsTransparency = false,
                            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1A, 0x24)),
                            SizeToContent = SizeToContent.WidthAndHeight,
                            WindowStartupLocation = WindowStartupLocation.Manual,
                            Content = tb
                        };
                        w.Left = (SystemParameters.PrimaryScreenWidth - 1180) / 2;
                        w.Top = SystemParameters.PrimaryScreenHeight - 260;
                        w.Show();
                        DispatcherTimer t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                        t.Tick += (s, e) =>
                        {
                            t.Stop();
                            try
                            {
                                w.Close();
                            }
                            catch
                            {
                            }
                        };
                        t.Start();
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(ex, "No se pudo mostrar el aviso");
                    }
                }));
            }
            catch
            {
            }
        }

        // ------------------------------------------------------------------
        // Volver al juego: reanudar primero, esperar a que se suelten las
        // teclas del atajo y entonces traer la ventana del juego al frente.
        // ------------------------------------------------------------------

        // El usuario está viendo Playnite con el juego pausado (opción "Ir a Playnite").
        private volatile bool browsing;

        // Al volver al juego desde ahí, Playnite sigue visible hasta que el juego pasa al frente: se tapa con el telón
        // antes de cerrar el menú para que no se vea su pantalla ni un instante.
        private void CoverIfBrowsing()
        {
            if (browsing && !noFocus)
            {
                ShowCurtain(gameHwnd);
                // Playnite estaba "siempre encima": al cerrarse el menú Windows lo activaría y lo subiría sobre el telón (un fotograma
                // de su pantalla). Sin "siempre encima" queda por debajo del telón aunque se active.
                if (playniteHwnd != IntPtr.Zero)
                {
                    NativeMethods.SetTopmost(playniteHwnd, false);
                }
            }
        }

        private void ReturnToGame()
        {
            List<int> pids;
            lock (gate)
            {
                pids = new List<int>(pausedPids);
            }

            logger.Info("Volver al juego. PIDs: " + string.Join(",", pids));
            Resume(pids);
            Thread.Sleep(120);

            WaitKeysReleased(3000);
            Thread.Sleep(150);

            IntPtr target = gameHwnd;
            if (target == IntPtr.Zero || !NativeMethods.IsWindow(target))
            {
                target = FindMainWindow(pids);
                logger.Info("Ventana del juego buscada de nuevo: " + NativeMethods.Describe(target));
            }

            // Playnite estaba "siempre encima": hay que quitárselo ANTES de traer el juego
            // y mandarlo al fondo. Si no, sigue tapando al juego aunque este tenga el foco.
            // Telón (solo al volver desde Playnite): se queda SIEMPRE ENCIMA de todo, incluido el juego, hasta que el juego
            // esté pintado. Antes de traer el juego se minimiza Playnite (sin activarlo) detrás del telón, así que aunque el
            // juego tarde en repintar nunca puede asomar la pantalla de Playnite. Después el telón se desvanece.
            bool covered = curtainHwnd != IntPtr.Zero && !noFocus;
            if (covered)
            {
                RaiseCurtain();
            }
            if (playniteHwnd != IntPtr.Zero)
            {
                NativeMethods.SetTopmost(playniteHwnd, false);
                NativeMethods.SendToBottom(playniteHwnd);
                if (covered)
                {
                    NativeMethods.MinimizeNoActivate(playniteHwnd);
                }
            }

            bool ok = noFocus || NativeMethods.BringToFront(target, false);
            logger.Info("Juego al frente: " + ok + " (objetivo " + NativeMethods.Describe(target) + ", telón " + covered + ")");
            if (!ok && playniteHwnd != IntPtr.Zero)
            {
                NativeMethods.Minimize(playniteHwnd);
                Thread.Sleep(300);
                ok = NativeMethods.BringToFront(target, false);
                logger.Info("Segundo intento tras minimizar Playnite: " + ok);
            }
            logger.Info("Primer plano ahora: " + NativeMethods.Describe(NativeMethods.GetForegroundWindow()));
            if (covered && !ok)
            {
                // No se pudo traer el juego: se devuelve Playnite a la vista para no dejar al usuario en un escritorio vacío.
                NativeMethods.BringToFront(playniteHwnd, false);
                HideCurtain();
            }
            else if (covered)
            {
                // El juego pasa al frente por debajo del telón; se espera a que salga de segundo plano y se quita el telón.
                // Si el telón lleva la imagen del juego el corte no se nota y basta un instante; si es oscuro, se espera algo más.
                int waited = 0;
                while (waited < 1500 && (NativeMethods.IsMinimized(target) || !NativeMethods.IsForeground(target)))
                {
                    Thread.Sleep(40);
                    waited += 40;
                }
                int hold = curtainThumb != IntPtr.Zero ? 300 : 700;
                Thread.Sleep(hold);
                logger.Info("Telón: juego al frente tras " + waited + " ms, imagen del juego " + (curtainThumb != IntPtr.Zero) +
                            ", espera " + hold + " ms, Playnite minimizado " + NativeMethods.IsMinimized(playniteHwnd));
                HideCurtain();
            }
            else
            {
                HideCurtain();
            }
            browsing = false;
            MutePlaynite(false, 1500);
            if (!ok)
            {
                Notify(Loc.T("El juego está reanudado, pero no se pudo traer al frente. Prueba con Alt+Tab."));
            }

            Thread.Sleep(400);
            if (settings.MuteGame)
            {
                AudioMuter.SetMute(pids.Union(mutedPids).ToList(), false);
            }
            mutedPids = new List<int>();
            noFocus = false;
            notPaused = false;

            lock (gate)
            {
                pausedPids.Clear();
                menuOpen = false;
            }
            foregroundPid = 0;
            DeleteRecovery();
        }

        // Finalizar juego: reanuda (un proceso pausado no puede cerrarse solo), cierre limpio y, si no responde, forzado.
        private void EndGameCore()
        {
            List<int> pids;
            lock (gate)
            {
                pids = new List<int>(pausedPids);
            }
            pids = pids.Union(FindGamePids()).ToList();
            if (pids.Count == 0)
            {
                Notify(Loc.T("No hay ningún juego que cerrar."));
                return;
            }

            logger.Info("Finalizar juego. PIDs: " + string.Join(",", pids));
            endedByMenu = true;
            Resume(pids);
            Thread.Sleep(200);

            // Juego de administrador con un Playnite normal: Windows no deja cerrarlo desde aquí. Se pide el permiso a Windows
            // (si el usuario tiene el aviso de control de cuentas desactivado, se concede sin preguntar).
            bool selfElevated = NativeMethods.IsElevated(Process.GetCurrentProcess().Id) == 1;
            List<int> needAdmin = selfElevated ? new List<int>() : pids.Where(pid => IsAlive(pid) && NativeMethods.IsElevated(pid) == 1).ToList();
            if (needAdmin.Count > 0)
            {
                ElevatedClose(needAdmin);
                pids = pids.Except(needAdmin).ToList();
            }

            foreach (int pid in pids)
            {
                try
                {
                    using (Process p = Process.GetProcessById(pid))
                    {
                        p.CloseMainWindow();
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "CloseMainWindow falló para el PID " + pid);
                }
            }

            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000 && pids.Any(IsAlive))
            {
                Thread.Sleep(200);
            }

            List<int> alive = pids.Where(IsAlive).ToList();
            if (alive.Count > 0)
            {
                logger.Warn("No respondieron al cierre limpio; se fuerzan: " + string.Join(",", alive));
                foreach (int pid in alive)
                {
                    try
                    {
                        using (Process p = Process.GetProcessById(pid))
                        {
                            p.Kill();
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(ex, "No se pudo forzar el cierre del PID " + pid + " (¿administrador?)");
                    }
                }
                Thread.Sleep(500);
            }

            List<int> still = pids.Concat(needAdmin).Where(IsAlive).ToList();
            if (still.Count > 0)
            {
                if (settings.MuteGame)
                {
                    AudioMuter.SetMute(still, false);
                }
                Notify(Loc.T("No se pudo cerrar el juego del todo. Revisa extensions.log."));
            }

            lock (gate)
            {
                pausedPids.Clear();
                menuOpen = false;
            }
            foregroundPid = 0;
            DeleteRecovery();
            HideCurtain();
            MutePlaynite(false);
            if (playniteHwnd != IntPtr.Zero)
            {
                NativeMethods.SetTopmost(playniteHwnd, false);
            }
            logger.Info("Finalizar juego terminado. Procesos que siguen vivos: " + still.Count);
            EnsurePlayniteVisible(still.Count == 0);
        }

        // Cierra procesos de administrador pidiendo el permiso a Windows: primero un cierre normal y, si siguen vivos
        // pasados unos segundos, a la fuerza. Todo con una sola petición de permiso.
        private void ElevatedClose(List<int> pids)
        {
            try
            {
                string ids = string.Join(" ", pids.Select(p => "/PID " + p));
                string cmd = "/c taskkill /T " + ids + " >nul 2>&1 & ping -n 6 127.0.0.1 >nul & taskkill /F /T " + ids + " >nul 2>&1";
                logger.Info("Cierre con permiso de administrador: cmd.exe " + cmd);
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", cmd)
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        p.WaitForExit(90000);
                    }
                }
                Thread.Sleep(500);
            }
            catch (Exception ex)
            {
                // Lo más habitual: el usuario ha rechazado el aviso de Windows.
                logger.Warn(ex, "No se pudo cerrar el juego con permiso de administrador");
            }
        }

        private void WaitKeysReleased(int timeoutMs)
        {
            List<int> keys = new List<int> { settings.MenuVk, settings.EndVk };
            if (settings.UseCtrl) keys.Add(0x11);
            if (settings.UseAlt) keys.Add(0x12);
            if (settings.UseShift) keys.Add(0x10);

            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                bool down = false;
                foreach (int k in keys)
                {
                    if ((NativeMethods.GetAsyncKeyState(k) & 0x8000) != 0)
                    {
                        down = true;
                        break;
                    }
                }
                if (!down)
                {
                    return;
                }
                Thread.Sleep(30);
            }
            logger.Warn("Las teclas del atajo siguen pulsadas tras " + timeoutMs + " ms.");
        }

        // ------------------------------------------------------------------
        // Utilidades
        // ------------------------------------------------------------------

        private volatile bool endedByMenu;
        private int restoreGen;

        // Tras terminar un juego, Playnite debe quedar visible y con el foco. Si el juego se cerró desde el menú sin haber
        // traído antes a Playnite (juegos de administrador), Playnite se queda minimizado: se restaura aquí con reintentos.
        private void EnsurePlayniteVisible(bool force)
        {
            bool fullscreen = false;
            try
            {
                fullscreen = PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen;
            }
            catch
            {
            }
            if (!fullscreen && !endedByMenu)
            {
                return;
            }
            int gen = Interlocked.Increment(ref restoreGen);
            DateTime startAtCall = gameStartUtc;
            Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < 14 && gen == restoreGen; i++)
                    {
                        Thread.Sleep(i == 0 ? 500 : 700);
                        bool hasGame;
                        bool open;
                        lock (gate)
                        {
                            hasGame = currentGame != null;
                            open = menuOpen;
                        }
                        // Playnite puede tardar en dar el juego por terminado (o no enterarse si era de administrador):
                        // lo que cuenta es que sus procesos sigan vivos.
                        if (open || gameStartUtc != startAtCall || (!force && hasGame && FindGamePids().Any(IsAlive)))
                        {
                            return; // empezó otro juego, sigue en marcha o se abrió el menú: no se toca nada
                        }
                        if (activeMenu != null)
                        {
                            continue;
                        }
                        IntPtr pn = playniteHwnd != IntPtr.Zero && NativeMethods.IsWindow(playniteHwnd) ? playniteHwnd : GetPlayniteWindow();
                        if (pn == IntPtr.Zero)
                        {
                            continue;
                        }
                        playniteHwnd = pn;
                        if (NativeMethods.IsForeground(pn) && !NativeMethods.IsMinimized(pn) && NativeMethods.IsWindowVisible(pn))
                        {
                            if (i > 0)
                            {
                                logger.Info("Playnite restaurado tras cerrar el juego (intento " + i + ").");
                            }
                            endedByMenu = false;
                            return;
                        }
                        NativeMethods.SetTopmost(pn, false);
                        bool ok = NativeMethods.BringToFront(pn, false);
                        ActivatePlayniteWindow();
                        if (i == 0 || i == 5)
                        {
                            logger.Info("Playnite no estaba visible tras cerrar el juego; se restaura (" + ok + "). " + NativeMethods.Describe(pn));
                        }
                    }
                    endedByMenu = false;
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "No se pudo restaurar Playnite tras cerrar el juego");
                }
            });
        }

        private void ActivatePlayniteWindow()
        {
            try
            {
                PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                {
                    Window w = Application.Current != null ? Application.Current.MainWindow : null;
                    if (w == null)
                    {
                        return;
                    }
                    if (w.WindowState == WindowState.Minimized)
                    {
                        w.WindowState = WindowState.Normal;
                    }
                    w.Activate();
                    w.Focus();
                });
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo activar la ventana de Playnite");
            }
        }

        private static bool IsAlive(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    return !p.HasExited;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void Resume(List<int> pids)
        {
            PauseTracker.Resume(pids);
        }

        // PID de la ventana en primer plano si puede ser el juego (ni Playnite, ni Windows, ni lista de protegidos).
        private static int CaptureForegroundPid()
        {
            try
            {
                int pid = ProcessGuard.GetForegroundPid();
                if (pid <= 0 || pid == Process.GetCurrentProcess().Id)
                {
                    return 0;
                }
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
                string path = NativeMethods.GetProcessPath(pid);
                if (string.IsNullOrEmpty(path) || path.StartsWith(win, StringComparison.OrdinalIgnoreCase) || IsLauncher(pid))
                {
                    logger.Info("Primer plano antes del menú no válido como juego: " + (path ?? "ruta desconocida"));
                    return 0;
                }
                logger.Info("Primer plano antes del menú: PID " + pid + " [" + path + "]");
                return pid;
            }
            catch
            {
                return 0;
            }
        }

        private void RecoverSuspended()
        {
            try
            {
                string file = RecoveryFile;
                if (!File.Exists(file))
                {
                    return;
                }
                foreach (string s in File.ReadAllText(file).Split(','))
                {
                    int pid;
                    if (int.TryParse(s.Trim(), out pid))
                    {
                        NativeMethods.ResumeProcess(pid);
                    }
                }
                File.Delete(file);
                logger.Warn("Se han reanudado procesos que quedaron pausados en una sesión anterior.");
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo recuperar procesos pausados");
            }
        }

        private void DeleteRecovery()
        {
            try
            {
                string file = RecoveryFile;
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch
            {
            }
        }

        private void Notify(string text)
        {
            logger.Info("Aviso: " + text);
            try
            {
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "ConsoleOverlay_" + Guid.NewGuid().ToString("N"), text, NotificationType.Info));
            }
            catch
            {
            }
        }

        private IntPtr GetPlayniteWindow()
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                {
                    Window w = Application.Current != null ? Application.Current.MainWindow : null;
                    if (w != null)
                    {
                        handle = new WindowInteropHelper(w).Handle;
                    }
                });
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo obtener la ventana principal de Playnite");
            }
            if (handle == IntPtr.Zero)
            {
                handle = Process.GetCurrentProcess().MainWindowHandle;
            }
            return handle;
        }

        private List<int> FindGamePids()
        {
            Game game;
            int sp;
            lock (gate)
            {
                game = currentGame;
                sp = startedPid;
            }

            HashSet<int> result = new HashSet<int>();
            int self = Process.GetCurrentProcess().Id;
            string selfPath = NativeMethods.GetProcessPath(self);
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

            string dir = null;
            if (game != null && !string.IsNullOrWhiteSpace(game.InstallDirectory))
            {
                try
                {
                    dir = PlayniteApi.ExpandGameVariables(game, game.InstallDirectory);
                    dir = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
                }
                catch
                {
                    dir = null;
                }
            }
            if (dir != null && selfPath != null && selfPath.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
            {
                logger.Warn("La carpeta de instalación contiene a Playnite; se ignora: " + dir);
                dir = null;
            }

            if (dir != null)
            {
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id == self)
                        {
                            continue;
                        }
                        string path = NativeMethods.GetProcessPath(p.Id);
                        if (string.IsNullOrEmpty(path) || path.StartsWith(win, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (path.StartsWith(dir, StringComparison.OrdinalIgnoreCase) &&
                            !ProcessGuard.IsDeniedName(p.ProcessName))
                        {
                            result.Add(p.Id);
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }

            if (result.Count == 0 && sp > 0)
            {
                logger.Info("Sin procesos en la carpeta del juego; se usa el PID inicial y sus hijos.");
                List<int> candidates = new List<int> { sp };
                candidates.AddRange(GetDescendants(sp));
                foreach (int pid in candidates)
                {
                    if (pid == self || IsLauncher(pid))
                    {
                        continue;
                    }
                    string path = NativeMethods.GetProcessPath(pid);
                    if (string.IsNullOrEmpty(path) || path.StartsWith(win, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    result.Add(pid);
                }
            }

            // Proceso en primer plano justo antes de abrir el menú: es la mejor pista para juegos
            // lanzados como .exe cuya carpeta o proceso inicial no coinciden.
            int fgp = foregroundPid;
            if (fgp > 0 && !result.Contains(fgp) && IsAlive(fgp))
            {
                bool related = result.Count == 0;
                if (!related && sp > 0)
                {
                    related = GetDescendants(sp).Contains(fgp);
                }
                if (related)
                {
                    logger.Info("Se añade el proceso en primer plano (PID " + fgp + ") a los del juego.");
                    result.Add(fgp);
                    if (result.Count == 1)
                    {
                        foreach (int d in GetDescendants(fgp))
                        {
                            string dpath = NativeMethods.GetProcessPath(d);
                            if (d != self && !IsLauncher(d) && !string.IsNullOrEmpty(dpath) &&
                                !dpath.StartsWith(win, StringComparison.OrdinalIgnoreCase))
                            {
                                result.Add(d);
                            }
                        }
                    }
                }
            }

            if (result.Count > 25)
            {
                logger.Warn("Demasiados procesos (" + result.Count + "); se cancela por seguridad.");
                return new List<int>();
            }
            return result.ToList();
        }

        private static bool IsLauncher(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    return LauncherNames.Contains(p.ProcessName.ToLowerInvariant()) ||
                           ProcessGuard.IsDeniedName(p.ProcessName);
                }
            }
            catch
            {
                return true;
            }
        }

        private static List<int> GetDescendants(int rootPid)
        {
            List<int> result = new List<int>();
            try
            {
                Dictionary<int, int> parentOf = new Dictionary<int, int>();
                using (ManagementObjectSearcher searcher =
                    new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process"))
                using (ManagementObjectCollection coll = searcher.Get())
                {
                    foreach (ManagementBaseObject o in coll)
                    {
                        parentOf[Convert.ToInt32(o["ProcessId"])] = Convert.ToInt32(o["ParentProcessId"]);
                        o.Dispose();
                    }
                }

                Queue<int> queue = new Queue<int>();
                HashSet<int> seen = new HashSet<int> { rootPid };
                queue.Enqueue(rootPid);
                while (queue.Count > 0)
                {
                    int cur = queue.Dequeue();
                    foreach (KeyValuePair<int, int> kv in parentOf)
                    {
                        if (kv.Value == cur && seen.Add(kv.Key))
                        {
                            result.Add(kv.Key);
                            queue.Enqueue(kv.Key);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudieron obtener los procesos hijos");
            }
            return result;
        }

        private static IntPtr FindMainWindow(List<int> pids)
        {
            HashSet<uint> wanted = new HashSet<uint>(pids.Select(p => (uint)p));
            IntPtr best = IntPtr.Zero;
            long bestArea = 0;

            NativeMethods.EnumWindows((h, l) =>
            {
                uint pid;
                NativeMethods.GetWindowThreadProcessId(h, out pid);
                if (!wanted.Contains(pid))
                {
                    return true;
                }
                if (!NativeMethods.IsWindowVisible(h))
                {
                    return true;
                }
                if (NativeMethods.GetWindow(h, 4) != IntPtr.Zero)
                {
                    return true;
                }
                long area = NativeMethods.GetWindowArea(h);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = h;
                }
                return true;
            }, IntPtr.Zero);

            return best;
        }
    }
}
