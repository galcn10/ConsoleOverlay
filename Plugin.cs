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
            RemoveAdmin,
            OpenMusic,
            SwitchGame,
            Music
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
        private List<MenuRecent> pendingRecents;
        private string switchTargetId;
        private volatile bool switching;
        private readonly ManualResetEventSlim stoppedSignal = new ManualResetEventSlim(false);
        private readonly Dictionary<string, ImageSource> recentImages = new Dictionary<string, ImageSource>();
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
            UiFeedback.Log = s => logger.Info(s);
            UiFeedback.DataDir = GetPluginUserDataPath();
            ApplyFeedback();
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
                logger.Info("Console Overlay " + GetType().Assembly.GetName().Version.ToString(3) + ". Datos en: " + GetPluginUserDataPath());
                RecoverSuspended();
                if (settings.ShowMusic)
                {
                    // Se prepara ya, en segundo plano, para que la música esté lista cuando se abra el menú.
                    lastMusicPlayer = settings.MusicPlayer;
                    MusicWatcher.Instance.Start(settings.MusicPlayer);
                }
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
                UiFeedback.Shutdown();
                ClipPlayer.ShutdownAll();
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
            if (settings.ShowMusic && autoMusicApps == null)
            {
                Task.Run(() => { try { GetMusicApps(); WarmMusicIcons(); } catch { } }); // detección de reproductores e iconos en segundo plano
            }
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
            finally
            {
                stoppedSignal.Set();
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

                if (settings.UseCloseChord && (name == "Start" || name == "LeftShoulder"))
                {
                    if (name == "LeftShoulder" && pressed)
                    {
                        logger.Info("Mando: botón LB pulsado (atajo de cierre)");
                    }
                    OnCloseChord(name, pressed);
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

        // Start + LB mantenidos 1,5 s: cierra el juego. Solo con el menú cerrado.
        private const int CloseHoldMs = 1500;
        private readonly object closeLock = new object();
        private bool closeStartDown;
        private bool closeLbDown;
        private System.Threading.Timer closeTimer;

        private void OnCloseChord(string name, bool pressed)
        {
            lock (closeLock)
            {
                if (name == "Start")
                {
                    if (pressed)
                    {
                        logger.Info("Mando: Start pulsado (atajo de cierre)");
                    }
                    closeStartDown = pressed;
                }
                else
                {
                    closeLbDown = pressed;
                }
                bool both = closeStartDown && closeLbDown;
                if (both && closeTimer == null && activeMenu == null)
                {
                    closeTimer = new System.Threading.Timer(_ => CloseHeld(), null, CloseHoldMs, Timeout.Infinite);
                }
                else if (!both && closeTimer != null)
                {
                    closeTimer.Dispose();
                    closeTimer = null;
                }
            }
        }

        private void CloseHeld()
        {
            lock (closeLock)
            {
                if (!(closeStartDown && closeLbDown) || activeMenu != null)
                {
                    return;
                }
                if (closeTimer != null)
                {
                    closeTimer.Dispose();
                    closeTimer = null;
                }
            }
            logger.Info("Mando: Start + LB mantenidos, cerrando el juego");
            UiFeedback.Pulse(200, 0.8);
            Task.Run(() => OnEndHotkey());
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

        // Cada silenciado nuevo invalida los reintentos de desilenciado pendientes.
        private static volatile int muteEpoch;

        private static int ApplyPlayniteMute(bool mute, bool fullVolume = false)
        {
            try
            {
                int epoch = mute ? Interlocked.Increment(ref muteEpoch) : muteEpoch;
                int touched = AudioMuter.SetMute(new List<int> { Process.GetCurrentProcess().Id }, mute, fullVolume);
                if (!mute && touched == 0)
                {
                    // Windows solo ve las sesiones de audio activas: si Playnite no tiene sonido en ese momento,
                    // la orden no encuentra nada y la sesión se queda silenciada. Se reintenta hasta que aparezca.
                    RetryUnmute(epoch, fullVolume);
                }
                return touched;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo cambiar el silencio de Playnite");
                return 0;
            }
        }

        private static void RetryUnmute(int epoch, bool fullVolume)
        {
            Task.Run(() =>
            {
                for (int i = 0; i < 40; i++)
                {
                    Thread.Sleep(50);
                    if (epoch != muteEpoch)
                    {
                        return;
                    }
                    try
                    {
                        if (AudioMuter.SetMute(new List<int> { Process.GetCurrentProcess().Id }, false, fullVolume) > 0)
                        {
                            logger.Info("Audio de Playnite desilenciado tras " + ((i + 1) * 50) + " ms");
                            return;
                        }
                    }
                    catch
                    {
                        return;
                    }
                }
                logger.Warn("No se encontró la sesión de audio de Playnite para desilenciarla");
            });
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
                    DateTime limit = DateTime.UtcNow.AddMilliseconds(120); // en el log casi siempre agotaba los 400 ms sin recibir el aviso: se acorta
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
        private void ApplyFeedback()
        {
            UiFeedback.Apply(settings.AnimationsEnabled, settings.SoundsEnabled, settings.SoundVolume, settings.VibrationEnabled);
        }

        public void ApplySettings()
        {
            try
            {
                LoadLocale();
                ApplyFeedback();
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
            if (switching)
            {
                return;
            }
            MenuWindow openMenu = activeMenu;
            if (openMenu != null)
            {
                if (!longPress)
                {
                    // Un segundo Guide casi a la vez que se abre el menú suele ser un rebote: se ignora.
                    if ((DateTime.UtcNow - openMenu.ShownAtUtc).TotalMilliseconds < 1000)
                    {
                        logger.Info("Botón Guide ignorado: el menú acaba de abrirse.");
                        return;
                    }
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
                    Game cg;
                    lock (gate)
                    {
                        cg = currentGame;
                    }
                    // Mientras el juego carga (aún sin ventana visible) el menú no se abre: pausar entonces estropea el arranque.
                    if ((DateTime.UtcNow - gameStartedAt).TotalSeconds < 90 && FindMainWindow(FindGamePids()) == IntPtr.Zero)
                    {
                        logger.Info("Menú no abierto: el juego aún se está abriendo.");
                        return;
                    }
                    string skip = SkipOverlayReason(cg);
                    if (skip != null)
                    {
                        // El juego (p. ej. un emulador con su propio menú) se queda con el botón: el plugin no hace nada.
                        logger.Info("Menú no abierto: " + skip + ".");
                        return;
                    }
                    Stopwatch tMenu = Stopwatch.StartNew();
                    menuTimer = tMenu;
                    if (!EnterMenu())
                    {
                        return;
                    }
                    logger.Info("Tiempo: Playnite al frente y juego pausado en " + tMenu.ElapsedMilliseconds + " ms");
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

                    case MenuAction.SwitchGame:
                        {
                            Game target = null;
                            Guid tid;
                            if (Guid.TryParse(switchTargetId, out tid))
                            {
                                target = PlayniteApi.Database.Games.Get(tid);
                            }
                            if (target == null)
                            {
                                page = MenuPage.Main;
                                break;
                            }
                            if (Confirm(Loc.T("¿Cerrar el juego y abrir {0}? Se perderá el progreso que no esté guardado.", target.Name)))
                            {
                                SwitchGameCore(target);
                                return;
                            }
                            page = MenuPage.Main;
                            break;
                        }

                    case MenuAction.OpenMusic:
                        {
                            List<KeyValuePair<string, string>> apps = GetMusicApps();
                            int pick = 0;
                            if (apps.Count > 1)
                            {
                                List<MenuEntry> list = apps.Select(a =>
                                {
                                    MenuEntry me = new MenuEntry("music", a.Key) { OpenOnly = true };
                                    ApplyMusicIcon(me, a.Value);
                                    return me;
                                }).ToList();
                                list.Add(new MenuEntry("arrow-left", Loc.T("Atrás")));
                                pick = Choose(Loc.T("¿Qué reproductor quieres abrir?"), Loc.T("Música"), list, 0, list.Count - 1, hasGame);
                            }
                            if (pick < 0 || pick >= apps.Count)
                            {
                                page = MenuPage.Main;
                                break;
                            }
                            // El menú se queda abierto: vuelve de inmediato a la pantalla principal mientras el reproductor
                            // arranca minimizado; cuando hay música, la fila cambia en el sitio.
                            LaunchMusicApp(apps[pick].Value);
                            WatchMusicOpen(apps[pick].Value);
                            page = MenuPage.Main;
                            break;
                        }

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

        // Los 3 últimos juegos jugados (sin el actual) para el panel «Cambiar a».
        private List<MenuRecent> GetRecents()
        {
            try
            {
                Game cur;
                lock (gate)
                {
                    cur = currentGame;
                }
                if (cur == null)
                {
                    return null;
                }
                List<Game> games = PlayniteApi.Database.Games
                    .Where(g => g.Id != cur.Id && g.IsInstalled && !g.Hidden && g.LastActivity.HasValue)
                    .OrderByDescending(g => g.LastActivity.Value)
                    .Take(3)
                    .ToList();
                List<MenuRecent> res = new List<MenuRecent>();
                foreach (Game g in games)
                {
                    ImageSource img = null;
                    string key = g.CoverImage ?? g.Icon;
                    if (!string.IsNullOrEmpty(key) && !recentImages.TryGetValue(key, out img))
                    {
                        img = LoadImage(key, 240, 0);
                        if (recentImages.Count > 12)
                        {
                            recentImages.Clear();
                        }
                        recentImages[key] = img;
                    }
                    res.Add(new MenuRecent { Id = g.Id.ToString(), Name = g.Name, Image = img, When = WhenText(g.LastActivity.Value) });
                }
                return res.Count > 0 ? res : null;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudieron leer los juegos recientes");
                return null;
            }
        }

        private static string WhenText(DateTime dt)
        {
            int d = (int)(DateTime.Now.Date - dt.Date).TotalDays;
            if (d <= 0)
            {
                return Loc.T("Hoy");
            }
            return d == 1 ? Loc.T("Ayer") : Loc.T("Hace {0} días", d);
        }

        // Cierra el juego actual y abre otro detrás de una pantalla de transición, para que no se vea nada intermedio.
        private void SwitchGameCore(Game target)
        {
            switching = true;
            SwitchSplash splash = null;
            try
            {
                logger.Info("Cambiar de juego a: " + target.Name);
                ImageSource cov = LoadImage(target.CoverImage, 0, 720) ?? LoadImage(target.Icon, 0, 720);
                PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                {
                    FontFamily font = null;
                    try
                    {
                        string fi;
                        font = Palette.ThemeFont(out fi);
                    }
                    catch
                    {
                    }
                    splash = new SwitchSplash(target.Name, cov, font);
                    splash.Show();
                });
                Thread.Sleep(150);

                stoppedSignal.Reset();
                CloseMenuWindow(false);
                EndGameCore();
                // Se espera a que Playnite confirme que el juego anterior terminó, para no mezclar su estado con el del nuevo.
                if (!stoppedSignal.Wait(8000))
                {
                    logger.Warn("Cambiar de juego: no llegó el aviso de parada; se continúa igualmente.");
                }
                Thread.Sleep(300);

                PlayniteApi.MainView.UIDispatcher.Invoke(() => PlayniteApi.StartGame(target.Id));

                // Se mantiene la pantalla hasta que el juego nuevo tiene ventana visible (con un máximo de 30 s).
                Stopwatch sw = Stopwatch.StartNew();
                bool ready = false;
                while (sw.ElapsedMilliseconds < 30000)
                {
                    Game cur;
                    lock (gate)
                    {
                        cur = currentGame;
                    }
                    if (cur != null && cur.Id == target.Id)
                    {
                        IntPtr h = FindMainWindow(FindGamePids());
                        if (h != IntPtr.Zero && NativeMethods.IsWindow(h) && NativeMethods.IsWindowVisible(h) && !NativeMethods.IsMinimized(h))
                        {
                            ready = true;
                            break;
                        }
                    }
                    Thread.Sleep(250);
                }
                logger.Info("Cambiar de juego: ventana lista = " + ready + " tras " + sw.ElapsedMilliseconds + " ms");
                Thread.Sleep(ready ? 1200 : 0);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Error al cambiar de juego");
                try
                {
                    Notify(Loc.T("No se pudo cambiar de juego."));
                    EnsurePlayniteVisible(true);
                }
                catch
                {
                }
            }
            finally
            {
                try
                {
                    SwitchSplash sp = splash;
                    if (sp != null)
                    {
                        PlayniteApi.MainView.UIDispatcher.Invoke(() => sp.FadeAndClose());
                    }
                }
                catch
                {
                }
                switching = false;
            }
        }

        // Reproductores de la lista de ajustes (Nombre|ruta). Sin lista, si Spotify está instalado se ofrece Spotify.
        private List<KeyValuePair<string, string>> GetMusicApps()
        {
            List<KeyValuePair<string, string>> res = new List<KeyValuePair<string, string>>();
            foreach (string raw in (settings.MusicApps ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                int bar = line.IndexOf('|');
                string name = bar > 0 ? line.Substring(0, bar).Trim() : "";
                string target = bar > 0 ? line.Substring(bar + 1).Trim().Trim('"') : line.Trim('"');
                if (target.Length == 0)
                {
                    continue;
                }
                if (name.Length == 0)
                {
                    name = target.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase) ? "Spotify" : Path.GetFileNameWithoutExtension(target);
                }
                res.Add(new KeyValuePair<string, string>(name, target));
            }
            {
                if (autoMusicApps == null)
                {
                    autoMusicApps = DetectMusicApps();
                }
                // Los de ajustes van primero; los detectados se añaden sin repetir nombre ni destino.
                foreach (KeyValuePair<string, string> au in autoMusicApps)
                {
                    if (!res.Any(r => r.Key.Equals(au.Key, StringComparison.OrdinalIgnoreCase) || r.Value.Equals(au.Value, StringComparison.OrdinalIgnoreCase)))
                    {
                        res.Add(au);
                    }
                }
                foreach (string raw in (settings.LearnedMusicApps ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int bar = raw.IndexOf('|');
                    if (bar <= 0)
                    {
                        continue;
                    }
                    string nm = raw.Substring(0, bar).Trim();
                    string tg = raw.Substring(bar + 1).Trim();
                    if (nm.Length > 0 && tg.Length > 0 && !res.Any(r => r.Key.Equals(nm, StringComparison.OrdinalIgnoreCase) || r.Value.Equals(tg, StringComparison.OrdinalIgnoreCase)))
                    {
                        res.Add(new KeyValuePair<string, string>(nm, tg));
                    }
                }
            }
            return res;
        }

        private List<KeyValuePair<string, string>> autoMusicApps;

        // Sin lista en ajustes: se buscan los reproductores conocidos instalados (Spotify y los de la lista de programas de Windows).
        // Destino de un reproductor -> dónde sacar su icono (p. ej. Spotify de la Tienda, que se abre con «spotify:»).
        private static readonly Dictionary<string, string> MusicIconSource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static List<KeyValuePair<string, string>> DetectMusicApps()
        {
            List<KeyValuePair<string, string>> res = new List<KeyValuePair<string, string>>();
            try
            {
                string spotifyExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", "Spotify.exe");
                if (File.Exists(spotifyExe))
                {
                    res.Add(new KeyValuePair<string, string>("Spotify", spotifyExe));
                }
                else
                {
                    using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("spotify"))
                    {
                        if (k != null)
                        {
                            res.Add(new KeyValuePair<string, string>("Spotify", "spotify:"));
                        }
                    }
                }
            }
            catch
            {
            }
            string[] known = { "SimpMusic", "foobar2000", "AIMP", "MusicBee", "iTunes", "Apple Music", "Winamp", "Deezer", "TIDAL", "Amazon Music", "YouTube Music", "Pear Desktop", "Cider", "Qobuz", "Plexamp", "Pandora", "SoundCloud", "Audacious", "Strawberry", "MediaMonkey" };
            string[] hives = { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
            foreach (Microsoft.Win32.RegistryKey root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                foreach (string hive in hives)
                {
                    try
                    {
                        using (Microsoft.Win32.RegistryKey u = root.OpenSubKey(hive))
                        {
                            if (u == null)
                            {
                                continue;
                            }
                            foreach (string sub in u.GetSubKeyNames())
                            {
                                using (Microsoft.Win32.RegistryKey a = u.OpenSubKey(sub))
                                {
                                    string name = a == null ? null : a.GetValue("DisplayName") as string;
                                    if (string.IsNullOrEmpty(name))
                                    {
                                        continue;
                                    }
                                    string label = known.FirstOrDefault(kn => name.IndexOf(kn, StringComparison.OrdinalIgnoreCase) >= 0);
                                    if (label == null || res.Any(r => r.Key.Equals(label, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        continue;
                                    }
                                    string exe = ExeOfUninstallEntry(a);
                                    if (exe != null)
                                    {
                                        res.Add(new KeyValuePair<string, string>(label == "VLC media player" ? "VLC" : label, exe));
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
            // Apps de la Tienda / MSIX (WindowsApps no deja lanzar el .exe por su ruta): se abren por su identificador de aplicación.
            try
            {
                {
                    Dictionary<string, string> startApps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    ProcessStartInfo ps = new ProcessStartInfo("powershell.exe",
                        "-NoProfile -NonInteractive -Command \"Get-StartApps | ForEach-Object { $_.Name + '|' + $_.AppID }\"")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };
                    using (Process pr = Process.Start(ps))
                    {
                        string outp = pr.StandardOutput.ReadToEnd();
                        pr.WaitForExit(8000);
                        foreach (string line in outp.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            int bar = line.IndexOf('|');
                            if (bar <= 0)
                            {
                                continue;
                            }
                            string nm = line.Substring(0, bar).Trim();
                            string id = line.Substring(bar + 1).Trim();
                            startApps[nm] = id;
                            string flatNm = nm.Replace(" ", "");
                            string label = known.FirstOrDefault(kn => flatNm.StartsWith(kn.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) &&
                                                                     flatNm.IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) < 0 &&
                                                                     flatNm.IndexOf("desinstal", StringComparison.OrdinalIgnoreCase) < 0);
                            if (label != null || flatNm.IndexOf("simp", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                logger.Info("Música: app de Inicio '" + nm + "' (" + id + ")" + (label != null ? " -> " + label : " sin coincidir"));
                            }
                            if (label == null || id.IndexOf('!') < 0 || res.Any(r => r.Key.Equals(label == "VLC media player" ? "VLC" : label, StringComparison.OrdinalIgnoreCase)))
                            {
                                continue;
                            }
                            res.Add(new KeyValuePair<string, string>(label == "VLC media player" ? "VLC" : label, "shell:AppsFolder\\" + id));
                        }
                        logger.Info("Música: apps de Inicio leídas = " + startApps.Count);
                        // Reproductores que no se abren desde el menú Inicio (p. ej. Spotify con «spotify:»): el icono sale de su app de Inicio.
                        foreach (KeyValuePair<string, string> r in res.ToList())
                        {
                            string sid;
                            if (!File.Exists(r.Value) && !r.Value.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) && startApps.TryGetValue(r.Key, out sid))
                            {
                                MusicIconSource[r.Value] = "shell:AppsFolder\\" + sid;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Música: no se pudieron leer las apps de Inicio");
            }
            // Segunda vía para las apps de la Tienda, sin PowerShell: se consultan los paquetes instalados del usuario.
            try
            {
                Windows.Management.Deployment.PackageManager pm = new Windows.Management.Deployment.PackageManager();
                int pkgCount = 0;
                string spotifyAumid = null;
                foreach (Windows.ApplicationModel.Package pkg in pm.FindPackagesForUser(""))
                {
                    pkgCount++;
                    string pname = pkg.Id.Name ?? "";
                    if (spotifyAumid == null && pname.IndexOf("Spotify", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        try
                        {
                            foreach (Windows.ApplicationModel.Core.AppListEntry se in pkg.GetAppListEntries())
                            {
                                spotifyAumid = se.AppUserModelId;
                                break;
                            }
                        }
                        catch
                        {
                        }
                    }
                    string pkgLabel = null;
                    foreach (string kn in known)
                    {
                        string lab = kn == "VLC media player" ? "VLC" : kn;
                        if (res.Any(r => r.Key.Equals(lab, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }
                        if (pname.IndexOf(kn.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            pkgLabel = lab;
                            break;
                        }
                    }
                    if (pkgLabel == null)
                    {
                        continue;
                    }
                    foreach (Windows.ApplicationModel.Core.AppListEntry entry in pkg.GetAppListEntries())
                    {
                        string aumid = entry.AppUserModelId;
                        logger.Info("Música: paquete '" + pname + "' -> " + aumid);
                        if (!string.IsNullOrEmpty(aumid))
                        {
                            res.Add(new KeyValuePair<string, string>(pkgLabel, "shell:AppsFolder\\" + aumid));
                            break;
                        }
                    }
                }
                logger.Info("Música: paquetes del usuario revisados = " + pkgCount);
                // Spotify de la Tienda se abre con «spotify:»; su icono sale de su paquete.
                if (spotifyAumid != null)
                {
                    foreach (KeyValuePair<string, string> r in res)
                    {
                        if (r.Value.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase) && !MusicIconSource.ContainsKey(r.Value))
                        {
                            MusicIconSource[r.Value] = "shell:AppsFolder\\" + spotifyAumid;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Música: no se pudieron revisar los paquetes instalados");
            }
            // Respaldo: accesos directos del menú Inicio y carpetas habituales (instaladores que no registran el icono).
            try
            {
                List<string> lnks = new List<string>();
                foreach (Environment.SpecialFolder sf in new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Programs })
                {
                    string dir = Environment.GetFolderPath(sf);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        lnks.AddRange(Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories));
                    }
                }
                List<string> roots = new List<string>
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                };
                foreach (string kn in known)
                {
                    string label = kn == "VLC media player" ? "VLC" : kn;
                    if (res.Any(r => r.Key.Equals(label, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    string flat = kn.Replace(" ", "");
                    string exe = null;
                    foreach (string l in lnks)
                    {
                        string fn = Path.GetFileNameWithoutExtension(l).Replace(" ", "");
                        if (fn.IndexOf(flat, StringComparison.OrdinalIgnoreCase) >= 0 && fn.IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            exe = ShortcutTarget(l);
                            if (exe != null)
                            {
                                break;
                            }
                        }
                    }
                    if (exe == null)
                    {
                        // Alias de ejecución de las apps MSIX (se pueden lanzar por su nombre aunque WindowsApps no deje abrir el .exe).
                        try
                        {
                            string aliasDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
                            if (Directory.Exists(aliasDir))
                            {
                                exe = Directory.GetFiles(aliasDir, "*.exe")
                                    .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).StartsWith(flat, StringComparison.OrdinalIgnoreCase));
                            }
                        }
                        catch
                        {
                        }
                    }
                    if (exe == null)
                    {
                        foreach (string r in roots)
                        {
                            try
                            {
                                string d = Path.Combine(r, kn);
                                if (!Directory.Exists(d))
                                {
                                    d = Path.Combine(r, flat);
                                }
                                if (!Directory.Exists(d))
                                {
                                    continue;
                                }
                                string[] exes = Directory.GetFiles(d, "*.exe", SearchOption.AllDirectories)
                                    .Where(e => !Path.GetFileName(e).StartsWith("unins", StringComparison.OrdinalIgnoreCase) &&
                                                !Path.GetFileName(e).StartsWith("update", StringComparison.OrdinalIgnoreCase))
                                    .ToArray();
                                exe = exes.FirstOrDefault(e => Path.GetFileNameWithoutExtension(e).Replace(" ", "").Equals(flat, StringComparison.OrdinalIgnoreCase)) ?? exes.FirstOrDefault();
                                if (exe != null)
                                {
                                    break;
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                    if (exe != null)
                    {
                        res.Add(new KeyValuePair<string, string>(label, exe));
                    }
                }
            }
            catch
            {
            }
            return res;
        }

        // Destino de un acceso directo (.lnk) si es un .exe existente.
        private static string ShortcutTarget(string lnk)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object sh = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                string target = sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null) as string;
                return !string.IsNullOrEmpty(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target) ? target : null;
            }
            catch
            {
                return null;
            }
        }

        // Ejecutable de un programa instalado: el icono que registra (DisplayIcon) o, si no, el .exe con su nombre en la carpeta de instalación.
        private static string ExeOfUninstallEntry(Microsoft.Win32.RegistryKey a)
        {
            string icon = a.GetValue("DisplayIcon") as string;
            if (!string.IsNullOrEmpty(icon))
            {
                int comma = icon.LastIndexOf(',');
                if (comma > 0)
                {
                    icon = icon.Substring(0, comma);
                }
                icon = icon.Trim().Trim('"');
                if (icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(icon))
                {
                    return icon;
                }
            }
            string dir = a.GetValue("InstallLocation") as string;
            string name = a.GetValue("DisplayName") as string;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                string[] exes = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly);
                string best = exes.FirstOrDefault(e => Path.GetFileNameWithoutExtension(e).Equals(name, StringComparison.OrdinalIgnoreCase))
                              ?? exes.FirstOrDefault(e => !Path.GetFileName(e).StartsWith("unins", StringComparison.OrdinalIgnoreCase));
                return best;
            }
            return null;
        }

        // ¿Sigue abierta la aplicación que publicó la sesión de música? Se compara con el nombre de los procesos.
        private static void MusicOpenTexts(List<KeyValuePair<string, string>> apps, out string desc, out string right)
        {
            right = apps.Count == 1 ? apps[0].Key : null;
            desc = apps.Count == 1 ? Loc.T("Abre {0} y vuelve al juego", apps[0].Key) : Loc.T("Elige un reproductor y vuelve al juego");
        }

        private static string SourceProcessName(string appId)
        {
            string s = appId ?? "";
            int bang = s.LastIndexOf('!');
            if (bang >= 0 && bang < s.Length - 1)
            {
                s = s.Substring(bang + 1);
            }
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(0, s.Length - 4);
            }
            int us = s.IndexOf('_');
            if (us > 0)
            {
                s = s.Substring(0, us);
            }
            return s;
        }

        private volatile bool musicOpening;

        // Abre el único reproductor conocido sin cerrar ni redibujar el menú; cuando hay música cambia la fila en el sitio.
        // Devuelve false si hay que elegir entre varios reproductores (lo resuelve el bucle del menú).
        private bool OpenMusicInline()
        {
            List<KeyValuePair<string, string>> apps = GetMusicApps();
            if (apps.Count != 1)
            {
                return false;
            }
            if (musicOpening)
            {
                return true;
            }
            LaunchMusicApp(apps[0].Value);
            WatchMusicOpen(apps[0].Value);
            return true;
        }

        // Espera (hasta 8 s) a que el reproductor recién abierto publique su sesión y cambia la fila de música en el sitio.
        // Nombre del proceso que se espera al abrir un reproductor (para minimizar su ventana).
        private static string MusicProcessFor(string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                return "";
            }
            if (target.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
            {
                return "Spotify";
            }
            const string AppsPrefix = "shell:AppsFolder\\";
            if (target.StartsWith(AppsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return SourceProcessName(target.Substring(AppsPrefix.Length));
            }
            try { return Path.GetFileNameWithoutExtension(target); } catch { return ""; }
        }

        // Las ventanas del reproductor recién abierto se minimizan sin quitar el foco, para que no tapen el menú ni el juego.
        private void MinimizeMusicWindows(string procName)
        {
            if (procName.Length < 4 || BrowserNames.Any(b => procName.StartsWith(b, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
            HashSet<int> skip = new HashSet<int>();
            try { foreach (int gp in FindGamePids()) { skip.Add(gp); } } catch { }
            skip.Add(Process.GetCurrentProcess().Id);
            HashSet<uint> pids = new HashSet<uint>();
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.ProcessName.StartsWith(procName, StringComparison.OrdinalIgnoreCase) && !skip.Contains(p.Id))
                    {
                        pids.Add((uint)p.Id);
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
            if (pids.Count == 0)
            {
                return;
            }
            // Todas las ventanas visibles de esos procesos (las apps de Java o Chromium no siempre tienen «ventana principal»).
            NativeMethods.EnumWindows((h, l) =>
            {
                try
                {
                    uint wp;
                    NativeMethods.GetWindowThreadProcessId(h, out wp);
                    if (pids.Contains(wp) && NativeMethods.IsWindowVisible(h) && !NativeMethods.IsIconic(h))
                    {
                        NativeMethods.ShowWindow(h, 7); // SW_SHOWMINNOACTIVE
                    }
                }
                catch
                {
                }
                return true;
            }, IntPtr.Zero);
        }

        private static int SuspendedThreads(int pid)
        {
            int n = 0;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    foreach (ProcessThread t in p.Threads)
                    {
                        try
                        {
                            if (t.ThreadState == System.Diagnostics.ThreadState.Wait && t.WaitReason == ThreadWaitReason.Suspended)
                            {
                                n++;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
            return n;
        }

        private void WatchMusicOpen(string target = null)
        {
            string procName = MusicProcessFor(target);
            if (procName.Length >= 4)
            {
                Task.Run(() =>
                {
                    // Solo mientras el menú siga abierto y unos segundos: antes seguía 20 s tocando ventanas aunque
                    // ya se hubiera vuelto al juego, lo que podía interferir con la ventana del juego.
                    int idle = 0;
                    for (int i = 0; i < 30 && idle < 3; i++)
                    {
                        Thread.Sleep(400);
                        if (activeMenu == null)
                        {
                            idle++;
                            continue;
                        }
                        idle = 0;
                        try { MinimizeMusicWindows(procName); } catch { }
                    }
                });
            }
            if (musicOpening)
            {
                return;
            }
            musicOpening = true;
            Task.Run(() =>
            {
                try
                {
                    Stopwatch wait = Stopwatch.StartNew();
                    while (wait.ElapsedMilliseconds < 8000)
                    {
                        MusicInfo mi = MusicWatcher.Instance.Current;
                        if (mi.HasSession && SourceRunning(mi.SourceId))
                        {
                            break;
                        }
                        Thread.Sleep(250);
                    }
                    // Dos intentos: si la pantalla principal aún se estaba dibujando, el segundo la encuentra lista.
                    for (int i = 0; i < 2; i++)
                    {
                        PlayniteApi.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                        {
                            MenuWindow m = activeMenu;
                            MusicInfo mi = MusicWatcher.Instance.Current;
                            if (m != null && mi.HasSession && SourceRunning(mi.SourceId))
                            {
                                MenuEntry probe = new MenuEntry("music", "");
                                ApplyLiveIcon(probe, mi);
                                m.SetMusicRow(true, Loc.T("Música"), Loc.T("{A} pausa · {L1}/{R1} canción · {X} cerrar"), null, probe.Image, probe.AccentColor, true);
                                m.Activate();
                            }
                        }));
                        Thread.Sleep(700);
                    }
                }
                catch
                {
                }
                finally
                {
                    musicOpening = false;
                }
            });
        }

        // Icono de la app que está sonando (por nombre, por paquete de la Tienda o por su proceso en marcha).
        private void ApplyLiveIcon(MenuEntry live, MusicInfo mi)
        {
        KeyValuePair<string, string> src = GetMusicApps().FirstOrDefault(a => !string.IsNullOrEmpty(mi.Source) &&
            (a.Key.IndexOf(mi.Source, StringComparison.OrdinalIgnoreCase) >= 0 || mi.Source.IndexOf(a.Key, StringComparison.OrdinalIgnoreCase) >= 0));
        if (src.Value != null)
        {
            ApplyMusicIcon(live, src.Value);
        }
        if (live.Image == null)
        {
            // Sin coincidencia por nombre: se usa directamente la app que está sonando.
            string sid = mi.SourceId ?? "";
            if (sid.IndexOf('!') >= 0)
            {
                ApplyMusicIcon(live, "shell:AppsFolder\\" + sid);
            }
            else
            {
                string pn = SourceProcessName(sid);
                try
                {
                    foreach (System.Diagnostics.Process pr in System.Diagnostics.Process.GetProcessesByName(pn))
                    {
                        string path = null;
                        try { path = pr.MainModule.FileName; } catch { }
                        if (path != null)
                        {
                            ApplyMusicIcon(live, path);
                            if (live.Image != null)
                            {
                                break;
                            }
                        }
                    }
                    if (live.Image == null && pn.StartsWith("spotify", StringComparison.OrdinalIgnoreCase))
                    {
                        string sp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", "Spotify.exe");
                        ApplyMusicIcon(live, sp);
                        if (live.Image == null)
                        {
                            foreach (KeyValuePair<string, string> k in MusicIconSource)
                            {
                                if (k.Key.StartsWith("spotify", StringComparison.OrdinalIgnoreCase))
                                {
                                    ApplyMusicIcon(live, k.Key);
                                    break;
                                }
                            }
                        }
                    }
                }
                catch
                {
                }
            }
        }
        }

        // Icono y color del reproductor para su fila (se calculan una vez por reproductor).
        private readonly Dictionary<string, KeyValuePair<ImageSource, Color?>> musicIcons = new Dictionary<string, KeyValuePair<ImageSource, Color?>>();

        // Prepara los iconos de los reproductores en segundo plano para que el selector abra al instante.
        private void WarmMusicIcons()
        {
            foreach (KeyValuePair<string, string> a in GetMusicApps())
            {
                ApplyMusicIcon(new MenuEntry("music", ""), a.Value);
            }
        }

        private void ApplyMusicIcon(MenuEntry e, string target)
        {
            try
            {
                KeyValuePair<ImageSource, Color?> v;
                bool have;
                lock (musicIcons)
                {
                    have = musicIcons.TryGetValue(target, out v);
                }
                if (!have)
                {
                    Color? dom = null;
                    string iconSrc;
                    string eff = MusicIconSource.TryGetValue(target, out iconSrc) ? iconSrc : target;
                    ImageSource img = null;
                    const string AppsPrefix = "shell:AppsFolder\\";
                    if (File.Exists(eff))
                    {
                        img = AppIcon.Load(eff, out dom);
                    }
                    else if (eff.StartsWith(AppsPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        // App de la Tienda: el logo se pide a su paquete (el icono del Explorador salía como documento en blanco).
                        img = AppIcon.LoadBytes(PackagedLogo(eff.Substring(AppsPrefix.Length)), out dom);
                    }
                    v = new KeyValuePair<ImageSource, Color?>(img, dom);
                    if (img != null)
                    {
                        lock (musicIcons)
                        {
                            musicIcons[target] = v;
                        }
                    }
                }
                e.Image = v.Key;
                e.AccentColor = v.Value;
            }
            catch
            {
            }
        }

        // Logo (PNG) de una app de la Tienda a partir de su identificador de aplicación.
        private static byte[] PackagedLogo(string aumid)
        {
            try
            {
                Windows.Management.Deployment.PackageManager pm = new Windows.Management.Deployment.PackageManager();
                int bang = aumid.IndexOf('!');
                // Con el nombre de familia se pide solo ese paquete (recorrer los 120 instalados tardaba segundos).
                IEnumerable<Windows.ApplicationModel.Package> pkgs = bang > 0 ? pm.FindPackagesForUser("", aumid.Substring(0, bang)) : pm.FindPackagesForUser("");
                foreach (Windows.ApplicationModel.Package pkg in pkgs)
                {
                    foreach (Windows.ApplicationModel.Core.AppListEntry entry in pkg.GetAppListEntries())
                    {
                        if (!string.Equals(entry.AppUserModelId, aumid, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        Windows.Storage.Streams.RandomAccessStreamReference logo = entry.DisplayInfo.GetLogo(new Windows.Foundation.Size(128, 128));
                        using (Windows.Storage.Streams.IRandomAccessStreamWithContentType rs = logo.OpenReadAsync().AsTask().GetAwaiter().GetResult())
                        using (Stream st = rs.AsStreamForRead())
                        using (MemoryStream ms = new MemoryStream())
                        {
                            st.CopyTo(ms);
                            return ms.ToArray();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Info("Música: sin logo para " + aumid + " (" + ex.GetType().Name + ")");
            }
            return null;
        }

        // Si la app de la Tienda que suena no está en la lista, se recuerda para poder abrirla otro día.
        private void LearnMusicApp(MusicInfo mi)
        {
            try
            {
                string id = mi.SourceId ?? "";
                if (string.IsNullOrWhiteSpace(mi.Source))
                {
                    return;
                }
                string target = null;
                if (id.IndexOf('!') >= 0 && !id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    target = "shell:AppsFolder\\" + id; // app de la Tienda
                }
                else if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    // Programa de escritorio: se toma la ruta de su proceso en marcha (nunca navegadores ni Spotify, que ya tiene su vía).
                    string pn = SourceProcessName(id);
                    if (pn.Length < 4 || BrowserNames.Any(b => pn.StartsWith(b, StringComparison.OrdinalIgnoreCase)) ||
                        pn.StartsWith("spotify", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    foreach (Process p in Process.GetProcessesByName(pn))
                    {
                        try
                        {
                            string path = p.MainModule.FileName;
                            if (File.Exists(path))
                            {
                                target = path;
                                break;
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
                if (target == null)
                {
                    return;
                }
                if (GetMusicApps().Any(a => a.Value.Equals(target, StringComparison.OrdinalIgnoreCase) ||
                                            a.Key.Equals(mi.Source, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
                settings.LearnedMusicApps = (settings.LearnedMusicApps ?? "").TrimEnd() + "\n" + mi.Source.Trim() + "|" + target;
                SavePluginSettings(settings);
                logger.Info("Música: reproductor recordado " + mi.Source + " (" + target + ")");
            }
            catch
            {
            }
        }

        // Volumen del reproductor en el mezclador de Windows (el control de medios no tiene volumen).
        private void ChangeMusicVolume(MenuWindow w, float delta)
        {
            MusicInfo mi = MusicWatcher.Instance.Current;
            string name = SourceProcessName(mi.SourceId);
            if (name.Length < 4)
            {
                return;
            }
            Task.Run(() =>
            {
                try
                {
                    List<int> pids = new List<int>();
                    foreach (Process p in Process.GetProcesses())
                    {
                        try
                        {
                            if (p.ProcessName.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                            {
                                pids.Add(p.Id);
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
                    float v = AudioMuter.AdjustVolume(pids, delta);
                    if (v >= 0)
                    {
                        w.Dispatcher.BeginInvoke(new Action(() => w.ShowMusicVolume(v)));
                    }
                    else
                    {
                        logger.Info("Música: no hay sesión de audio de '" + name + "' para cambiar el volumen");
                    }
                }
                catch
                {
                }
            });
        }

        private static readonly string[] BrowserNames = { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "iexplore" };

        // Cierra el reproductor que está sonando y deja la fila lista para volver a abrirlo.
        private void CloseMusicInline()
        {
            MusicInfo mi = MusicWatcher.Instance.Current;
            string name = SourceProcessName(mi.SourceId);
            Task.Run(() =>
            {
                try
                {
                    bool browser = BrowserNames.Any(b => name.StartsWith(b, StringComparison.OrdinalIgnoreCase));
                    if (name.Length >= 4 && !browser)
                    {
                        HashSet<int> gamePids = new HashSet<int>();
                        try { foreach (int gp in FindGamePids()) { gamePids.Add(gp); } } catch { }
                        gamePids.Add(Process.GetCurrentProcess().Id);
                        foreach (Process p in Process.GetProcesses())
                        {
                            try
                            {
                                if (p.ProcessName.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (gamePids.Contains(p.Id))
                                    {
                                        logger.Info("Música: no se cierra " + p.ProcessName + " (PID " + p.Id + ") porque es del juego o de Playnite");
                                        continue;
                                    }
                                    logger.Info("Música: cerrando " + p.ProcessName + " (PID " + p.Id + ")");
                                    p.Kill();
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
                        logger.Info("Música: cerrado " + name);
                    }
                    else
                    {
                        logger.Info("Música: no se cierra '" + name + "' (navegador o nombre dudoso)");
                    }
                    Thread.Sleep(700);
                    PlayniteApi.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                    {
                        MenuWindow m = activeMenu;
                        if (m == null)
                        {
                            return;
                        }
                        MusicInfo cur = MusicWatcher.Instance.Current;
                        if (!(cur.HasSession && SourceRunning(cur.SourceId)))
                        {
                            string od, orr;
                            MusicOpenTexts(GetMusicApps(), out od, out orr);
                            m.SetMusicRow(false, Loc.T("Abrir música"), od, orr, null, null, true);
                        }
                        m.Activate();
                    }));
                    // Al cerrarse la app, Windows reparte el foco y a veces el menú se queda detrás: se vuelve a activar.
                    foreach (int wait in new[] { 600, 1500 })
                    {
                        Thread.Sleep(wait);
                        PlayniteApi.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                        {
                            MenuWindow m2 = activeMenu;
                            if (m2 != null)
                            {
                                m2.Activate();
                            }
                        }));
                    }
                }
                catch
                {
                }
            });
        }

        private static bool SourceRunning(string appId)
        {
            try
            {
                string s = appId ?? "";
                int bang = s.LastIndexOf('!');
                if (bang >= 0 && bang < s.Length - 1)
                {
                    s = s.Substring(bang + 1);
                }
                if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    s = s.Substring(0, s.Length - 4);
                }
                int us = s.IndexOf('_');
                if (us > 0)
                {
                    s = s.Substring(0, us);
                }
                if (s.Length < 4)
                {
                    return true;
                }
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.ProcessName.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true;
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
                return false;
            }
            catch
            {
                return true;
            }
        }

        // Abre el reproductor minimizado (el menú sigue abierto delante). Spotify admite --minimized.
        private void LaunchMusicApp(string target)
        {
            Task.Run(() =>
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(target) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized };
                    if (Path.GetFileName(target).Equals("Spotify.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        psi.Arguments = "--minimized";
                    }
                    Process.Start(psi);
                    logger.Info("Música: abierto " + target);
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "Música: no se pudo abrir " + target);
                }
            });
        }

        private MenuAction ShowMainPage(MenuAction startOn)
        {
            List<MenuEntry> items = new List<MenuEntry>();
            List<MenuAction> map = new List<MenuAction>();
            items.Add(new MenuEntry("play", Loc.T("Volver al juego")) { Desc = Loc.T("Cierra el menú y sigue jugando") });
            map.Add(MenuAction.Back);
            if (KeyChord.IsValid(settings.ScreenshotKeys))
            {
                items.Add(new MenuEntry("camera", Loc.T("Capturar pantalla")) { Desc = Loc.T("Guarda una imagen de lo que se ve ahora") });
                map.Add(MenuAction.Screenshot);
            }
            if (KeyChord.IsValid(settings.ClipKeys))
            {
                items.Add(new MenuEntry("video", Loc.T("Guardar clip")) { Desc = Loc.T("Guarda los últimos segundos de juego") });
                map.Add(MenuAction.Clip);
            }
            List<CaptureItem> caps = GetCaptures();
            if (caps != null && caps.Count > 0)
            {
                items.Add(new MenuEntry("images", Loc.T("Capturas y clips")) { Right = caps.Count.ToString(), Desc = Loc.T("Ver, reproducir y revisar lo guardado") });
                map.Add(MenuAction.Captures);
            }
            else if (KeyChord.IsValid(settings.GalleryKeys))
            {
                // Sin capturas propias localizadas: se ofrece abrir el visor del programa de capturas.
                items.Add(new MenuEntry("images", Loc.T("Capturas y clips")) { Right = Loc.T("0 · abrir visor"), Desc = Loc.T("Ver, reproducir y revisar lo guardado") });
                map.Add(MenuAction.Gallery);
            }
            List<AchievementItem> achs = GetAchievements();
            if (achs != null)
            {
                int got = achs.Count(x => x.Unlocked);
                items.Add(new MenuEntry("trophy", Loc.T("Logros")) { Right = got + " / " + achs.Count, Desc = Loc.T("Logros del juego actual"), Platinum = achs.Count > 0 && (got == achs.Count || achs.Any(x => x.Unlocked && MenuWindow.TrophyTier(x) >= 3)) });
                map.Add(MenuAction.Achievements);
            }
            if (settings.ShowMusic)
            {
                if (lastMusicPlayer != settings.MusicPlayer)
                {
                    lastMusicPlayer = settings.MusicPlayer;
                    MusicWatcher.Instance.SetPreferred(settings.MusicPlayer);
                }
                MusicWatcher.Instance.Start(settings.MusicPlayer);
                MusicInfo mi = MusicWatcher.Instance.Current;
                // Una sesión parada cuya aplicación ya se cerró no sirve para controlar nada: se ofrece abrir un reproductor.
                if (MusicWatcher.Instance.Available && mi.HasSession && (mi.Playing || SourceRunning(mi.SourceId)))
                {
                    // Esta fila no cierra el menú: aceptar pausa o reanuda, izquierda y derecha cambian de canción.
                    LearnMusicApp(mi);
                    MenuEntry live = new MenuEntry("music", Loc.T("Música")) { MainMusic = true, Desc = Loc.T("{A} pausa · {L1}/{R1} canción · {X} cerrar") };
                    ApplyLiveIcon(live, mi);
                    items.Add(live);
                    map.Add(MenuAction.Music);
                }
                else
                {
                    List<KeyValuePair<string, string>> apps = GetMusicApps();
                    if (apps.Count > 0)
                    {
                        // Nada sonando: la fila ofrece abrir el reproductor (o elegir entre varios).
                        string od, orr;
                        MusicOpenTexts(apps, out od, out orr);
                        MenuEntry openRow = new MenuEntry("music", Loc.T("Abrir música")) { OpenOnly = true, MainMusic = true, Right = orr, Desc = od };
                        if (apps.Count == 1)
                        {
                            ApplyMusicIcon(openRow, apps[0].Value);
                        }
                        items.Add(openRow);
                        map.Add(MenuAction.OpenMusic);
                    }
                }
            }
            if (!noFocus)
            {
                items.Add(new MenuEntry("house", Loc.T("Ir a Playnite")) { Desc = Loc.T("Vuelve a la biblioteca de Playnite") });
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
            List<MenuRecent> recents = settings.ShowRecents ? GetRecents() : null;
            string endDesc = recents != null ? Loc.T("Cierra el juego · → cambiar a otro") : null;
            items.Add(noFocus
                ? new MenuEntry("circle-x", Loc.T("Finalizar juego")) { Danger = true, Right = Loc.T("pide permiso de Windows"), Desc = endDesc }
                : new MenuEntry("circle-x", Loc.T("Finalizar juego")) { Danger = true, Desc = endDesc });
            map.Add(MenuAction.EndGame);
            items.Add(new MenuEntry("power", Loc.T("Energía")) { Desc = Loc.T("Suspender, reiniciar o apagar el equipo") });
            map.Add(MenuAction.Power);

            string sess = SessionText();
            string head = noFocus
                ? Loc.T("Juego como administrador · sin pausa · sesión {0}", sess)
                : (notPaused
                    ? (notPausedReason != null
                        ? Loc.T("Juego en marcha (no se pausa: {0}) · sesión {1}", Loc.T(notPausedReason), sess)
                        : Loc.T("Juego en marcha (no se puede pausar) · sesión {0}", sess))
                    : Loc.T("Juego pausado · sesión {0}", sess));
            // Tras abrir un reproductor la fila pasa de «Abrir música» a «Música»: el cursor se queda en ella.
            int start = map.IndexOf(startOn);
            if (start < 0 && startOn == MenuAction.OpenMusic)
            {
                start = map.IndexOf(MenuAction.Music);
            }
            start = Math.Max(0, start);
            int idx;
            pendingRecents = recents;
            try
            {
                idx = Choose(head, GameName(), items, start, 0, true);
            }
            finally
            {
                pendingRecents = null;
            }
            if (recents != null && idx >= map.Count && idx - map.Count < recents.Count)
            {
                switchTargetId = recents[idx - map.Count].Id;
                return MenuAction.SwitchGame;
            }
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
                            FontFamily themeFont = Palette.ChosenFont(settings.MenuFont, out fontInfo);
                            if (themeFont == null)
                            {
                                string themeInfo;
                                themeFont = Palette.ThemeFont(out themeInfo);
                                fontInfo = fontInfo != null ? fontInfo + " | " + themeInfo : themeInfo;
                            }
                            if (fontInfo != lastFontInfo)
                            {
                                lastFontInfo = fontInfo;
                                logger.Info("Tipografía del menú: " + fontInfo);
                            }
                            Stopwatch tm = menuTimer;
                            if (tm != null)
                            {
                                logger.Info("Tiempo: datos listos, creando ventana del menú a los " + tm.ElapsedMilliseconds + " ms");
                            }
                            MenuWindow.NextRecents = pendingRecents;
                            MenuWindow created = new MenuWindow(title, text, items, defaultIndex, cancelIndex, cover, backdrop,
                                                                session, total, ResolveButtonStyle(), settings.UseGuideButton, accent, themeFont);
                            created.OnChosen = handler;
                            created.OpenMusicInline = OpenMusicInline;
                            created.CloseMusicInline = CloseMusicInline;
                            created.MusicVolumeChange = delta => ChangeMusicVolume(created, delta);
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
                            if (settings.ShowMusic)
                            {
                                ChangeMusicVolume(created, 0f); // lee el volumen actual del reproductor para mostrarlo en la tarjeta
                            }
                            Task.Run(() => UiFeedback.Pulse(90, 0.35));
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
                            w.Recents = pendingRecents;
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

        private Stopwatch menuTimer;
        private string lastMusicPlayer;

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
            Stopwatch swOpen = Stopwatch.StartNew();
            ShowCurtain(gameHwnd);
            long tCurtain = swOpen.ElapsedMilliseconds;
            MutePlaynite(true);
            playniteHwnd = GetPlayniteWindow();
            NativeMethods.SetTopmost(playniteHwnd, false);
            long tPre = swOpen.ElapsedMilliseconds;
            bool front = NativeMethods.BringToFront(playniteHwnd, false);
            logger.Info("Tiempo: telón " + tCurtain + " ms, preparar " + (tPre - tCurtain) + " ms, traer Playnite " + (swOpen.ElapsedMilliseconds - tPre) + " ms");
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
                    // Diagnóstico y recuperación: ¿el juego sigue congelado (hilos suspendidos o ventana sin responder)?
                    IntPtr fgw = NativeMethods.GetForegroundWindow();
                    logger.Warn("Ventana en primer plano sin responder: " + NativeMethods.IsHung(fgw));
                    foreach (int gp in pids)
                    {
                        int sus = SuspendedThreads(gp);
                        logger.Warn("Juego PID " + gp + ": hilos suspendidos=" + sus);
                        if (sus > 0)
                        {
                            for (int k = 0; k < 4; k++) { NativeMethods.ResumeProcess(gp); }
                            logger.Warn("Juego PID " + gp + ": reanudado a la fuerza; hilos suspendidos ahora=" + SuspendedThreads(gp));
                        }
                    }
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
            ActivatePlayniteWindow();
            RaiseCurtain();
            // Se espera solo lo que haga falta: en cuanto Playnite tiene el foco (suele ser inmediato) se sigue.
            for (int spin = 0; spin < 60 && !NativeMethods.IsForeground(playniteHwnd); spin++)
            {
                Thread.Sleep(10);
            }
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
        // Motivo por el que NO se debe abrir el menú en este juego (null = se abre). Los emuladores con su propio menú
        // (RetroArch, PCSX2, DuckStation...) usan el botón Guía por su cuenta.
        private string SkipOverlayReason(Game g)
        {
            try
            {
                if (g == null)
                {
                    return null;
                }
                if (g.Tags != null && g.Tags.Any(t => t != null && t.Name != null &&
                    (t.Name.Equals("Sin overlay", StringComparison.OrdinalIgnoreCase) ||
                     t.Name.Equals("NoOverlay", StringComparison.OrdinalIgnoreCase))))
                {
                    return "etiqueta «Sin overlay»";
                }
                if (settings.SkipEmulatedGames && g.GameActions != null &&
                    g.GameActions.Any(a => a != null && a.IsPlayAction && a.Type == GameActionType.Emulator))
                {
                    return "juego de emulador";
                }
            }
            catch
            {
            }
            return null;
        }

        private string NoPauseReason(Game g, List<int> pids)
        {
            try
            {
                if (!settings.PauseGame)
                {
                    return "pausa desactivada en ajustes";
                }
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
            Stopwatch swBack = Stopwatch.StartNew();
            Resume(pids);
            Thread.Sleep(50);

            WaitKeysReleased(3000);
            Thread.Sleep(60);

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
                int hold = curtainThumb != IntPtr.Zero ? 150 : 450;
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

            Thread.Sleep(220);
            logger.Info("Tiempo: volver al juego en " + swBack.ElapsedMilliseconds + " ms");
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
