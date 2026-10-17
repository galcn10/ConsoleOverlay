using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Shapes = System.Windows.Shapes;
using System.Windows.Threading;
using Playnite.SDK;

namespace ConsoleOverlay
{
    internal sealed class MenuEntry
    {
        public string Glyph;
        public string Text;
        public bool Danger;
        public string Right;

        public MenuEntry(string glyph, string text)
        {
            Glyph = glyph;
            Text = text;
        }
    }

    // ----------------------------------------------------------------------
    // Ventana del menú (a pantalla completa, diseñada sobre un lienzo de
    // 1920x1080 que se escala). Se maneja con el mando, el teclado o el ratón.
    // ----------------------------------------------------------------------
    internal sealed class MenuWindow : Window
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static Brush Make(string hex)
        {
            SolidColorBrush b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        private static Brush BackBrush = Make("#0D0F14");
        private static Brush RowBrush = Make("#12151D");
        private static Brush SelBrush = Make("#F7F8FB");
        private static readonly Brush TextBrush = Make("#F2F4F8");
        private static readonly Brush SelTextBrush = Make("#0D0F14");
        private static readonly Brush MutedBrush = Make("#9AA3BD");
        private static Brush PillBrush = Make("#232838");
        private static readonly Brush DangerBrush = Make("#FF8A80");
        private static Brush BadgeBrush = Make("#262C3D");
        private static Brush BadgeSelBrush = Make("#E3E6F0");
        private static readonly Color BaseDark = (Color)ColorConverter.ConvertFromString("#0D0F14");
        private Color tintDark = BaseDark;
        private Color? accent;

        private static Brush Frozen(Color c)
        {
            SolidColorBrush b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // Pinta el menú con el color de acento (del tema de Playnite o de la carátula). Sin acento, los colores neutros de siempre.
        private void ApplyStyle(Color? acc)
        {
            accent = acc;
            Color a = acc ?? Color.FromRgb(0x8A, 0x93, 0xB0);
            double k = acc == null ? 0 : 1;
            tintDark = Palette.Mix(BaseDark, a, 0.11 * k);
            BackBrush = Frozen(tintDark);
            Color row = Palette.Mix(Color.FromRgb(0x12, 0x15, 0x1D), a, 0.12 * k);
            RowBrush = Frozen(Color.FromArgb(150, row.R, row.G, row.B));
            PillBrush = Frozen(Palette.Mix(Color.FromRgb(0x23, 0x28, 0x38), a, 0.22 * k));
            BadgeBrush = Frozen(Palette.Mix(Color.FromRgb(0x26, 0x2C, 0x3D), a, 0.34 * k));
            BadgeSelBrush = Frozen(Palette.Mix(Color.FromRgb(0xE3, 0xE6, 0xF0), a, 0.12 * k));
            SelBrush = Frozen(Palette.Mix(Color.FromRgb(0xF7, 0xF8, 0xFB), a, 0.03 * k));
        }

        private List<MenuEntry> entries;
        private int cancelIndex;
        private Func<string> sessionProvider;
        private readonly Viewbox box;
        private readonly string buttonStyle;
        private readonly bool guideEnabled;
        private readonly List<Border> rows = new List<Border>();
        private readonly List<Shapes.Path> icons = new List<Shapes.Path>();
        private readonly List<TextBlock> labels = new List<TextBlock>();
        private readonly List<Border> badges = new List<Border>();
        private readonly List<TextBlock> rights = new List<TextBlock>();
        private readonly Grid root;
        private readonly Image backdropImage;
        private readonly Grid pageHost;
        private DateTime shownAt = DateTime.UtcNow;
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private TextBlock clockText;
        private StackPanel batteryHost;
        private List<BatteryInfo> lastBattery;
        private int refreshTicks;
        // Lector de la batería del mando (null = no mostrar).
        public Func<List<BatteryInfo>> BatteryProvider;
        public IntPtr PipSource;            // ventana del juego de la que se muestra la miniatura (0 = sin miniatura)
        public bool PipLive;                // true: el juego sigue en marcha (miniatura en directo)
        private IntPtr pipThumb;
        private int pipSrcW, pipSrcH;
        private Border pipFrame;
        private TextBlock pipLabel;
        private Border pipFallbackHost;
        public ImageSource PipFallback;     // imagen alternativa si la miniatura sale negra (última captura o fondo del juego)
        public bool PipFallbackIsCapture;
        public Func<Tuple<ImageSource, bool>> PipFallbackLoader;   // se llama solo si hace falta (imagen, es una captura)
        private bool pipKnownBlack;
        private bool pipChecked;
        private bool pipUsingFallback;
        private DispatcherTimer pipCheckTimer;
        private int pipRectX, pipRectY, pipRectW, pipRectH;
        private bool canvasHasGame;
        private bool audioUnsupported;
        private string clipNote = "";
        public Action<bool> ClipAudio;      // true: se está reproduciendo un clip (hay que dejar sonar a Playnite)
        private bool clipAudioOn;
        private string viewCaptionBase = "";
        private bool batteryBusy;

        // El juego tiene el foco y Windows no deja quitárselo (juego elevado): el menú se muestra encima sin pedir el foco.
        public bool NoFocus;
        private TextBlock sessionLine;
        private int selected;
        private bool achMode;
        private List<AchievementItem> ach;
        private StackPanel achPanel;
        private TextBlock achPos;
        private static readonly Dictionary<string, ImageSource> iconCache = new Dictionary<string, ImageSource>();
        private const int AchVisible = 6;
        private bool galMode;
        private List<CaptureItem> gal;
        private Grid galGrid;
        private TextBlock galPos;
        private const int GalCols = 4;
        private const int GalRows = 3;
        private static readonly Dictionary<string, ImageSource> thumbCache = new Dictionary<string, ImageSource>();
        private static readonly HashSet<string> thumbPending = new HashSet<string>();
        private Grid viewer;
        private Image viewImg;
        private MediaElement viewMedia;
        private TextBlock viewCaption;
        private TextBlock viewHint;
        private bool viewing;
        private bool mediaFailed;
        private bool mediaPlaying;
        private bool done;
        private string lastAction;
        private DateTime lastAt = DateTime.MinValue;

        public int Result = -1;

        // Quien espera la elección de la pantalla actual. La ventana no se cierra al elegir:
        // el plugin cambia el contenido con SetPage, así no hay parpadeo entre pantallas.
        public Action<int> OnChosen;

        public MenuWindow(string title, string message, List<MenuEntry> entries, int defaultIndex, int cancelIndex,
                          ImageSource cover, ImageSource backdrop, Func<string> sessionProvider, string totalText,
                          string buttonStyle, bool guideEnabled, Color? accent = null, FontFamily themeFont = null)
        {
            ApplyStyle(accent);
            if (themeFont != null)
            {
                FontFamily = themeFont; // los textos heredan la tipografía del tema de Playnite
            }
            this.entries = entries;
            this.cancelIndex = cancelIndex;
            this.sessionProvider = sessionProvider;
            this.buttonStyle = buttonStyle;
            this.guideEnabled = guideEnabled;
            selected = Math.Max(0, Math.Min(defaultIndex, entries.Count - 1));

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = BackBrush;
            WindowStartupLocation = WindowStartupLocation.Manual;
            // Nace fuera de pantalla y entra cuando ya está pintada (ver Reveal): así no se ve un fondo liso mientras se prepara.
            Left = -20000;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;

            root = new Grid { Width = 1920, Height = 1080, Background = BackBrush, ClipToBounds = true };
            // Fondo: la imagen del juego muy difuminada (se guarda en caché: se dibuja una sola vez), un tinte oscuro del color
            // de acento, un resplandor suave del mismo color arriba a la derecha y una sombra abajo para leer las ayudas.
            backdropImage = new Image
            {
                Stretch = Stretch.UniformToFill,
                Opacity = 0.62,
                Margin = new Thickness(-90),
                Effect = new System.Windows.Media.Effects.BlurEffect
                {
                    Radius = 42,
                    KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                    RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
                },
                CacheMode = new BitmapCache()
            };
            RenderOptions.SetBitmapScalingMode(backdropImage, BitmapScalingMode.Linear);
            root.Children.Add(backdropImage);
            Color td = tintDark;
            LinearGradientBrush shade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            shade.GradientStops.Add(new GradientStop(Color.FromArgb(225, td.R, td.G, td.B), 0.0));
            shade.GradientStops.Add(new GradientStop(Color.FromArgb(150, td.R, td.G, td.B), 0.55));
            shade.GradientStops.Add(new GradientStop(Color.FromArgb(205, td.R, td.G, td.B), 1.0));
            shade.Freeze();
            root.Children.Add(new Border { Background = shade });
            if (accent != null)
            {
                Color ac = accent.Value;
                RadialGradientBrush glow = new RadialGradientBrush
                {
                    Center = new Point(0.86, 0.18),
                    GradientOrigin = new Point(0.86, 0.18),
                    RadiusX = 0.62,
                    RadiusY = 0.85
                };
                glow.GradientStops.Add(new GradientStop(Color.FromArgb(105, ac.R, ac.G, ac.B), 0.0));
                glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, ac.R, ac.G, ac.B), 1.0));
                glow.Freeze();
                root.Children.Add(new Border { Background = glow, IsHitTestVisible = false });
            }
            spotBrush = new RadialGradientBrush
            {
                Center = new Point(0.52, 0.5),
                GradientOrigin = new Point(0.52, 0.5),
                RadiusX = 0.30,
                RadiusY = 0.17
            };
            Color sc = accent ?? Color.FromRgb(0xB4, 0xBE, 0xE6);
            spotBrush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(accent != null ? 52 : 22), sc.R, sc.G, sc.B), 0.0));
            spotBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, sc.R, sc.G, sc.B), 1.0));
            spot = new Border { Background = spotBrush, IsHitTestVisible = false, Opacity = 0 };
            root.Children.Add(spot);
            LinearGradientBrush floor = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            floor.GradientStops.Add(new GradientStop(Color.FromArgb(0, td.R, td.G, td.B), 0.72));
            floor.GradientStops.Add(new GradientStop(Color.FromArgb(215, td.R, td.G, td.B), 1.0));
            floor.Freeze();
            root.Children.Add(new Border { Background = floor, IsHitTestVisible = false });
            pageHost = new Grid();
            root.Children.Add(pageHost);
            SetBackdrop(backdrop);
            pageHost.Children.Add(BuildCanvas(title, message, cover, totalText, buttonStyle, guideEnabled));
            BuildViewer();

            box = new Viewbox { Stretch = Stretch.Uniform };
            box.Child = root;
            Content = box;

            // Aparición suave solo al abrir el menú (no al cambiar de pantalla).
            root.Opacity = 0;
            Loaded += (s, e) =>
            {
                // Red de seguridad: si el primer fotograma no llegara, el menú entra igualmente.
                DispatcherTimer safety = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                safety.Tick += (s2, e2) =>
                {
                    safety.Stop();
                    Reveal();
                };
                safety.Start();
            };

            PreviewKeyDown += OnKeyDown;
            ContentRendered += OnRendered;
            Closed += (s, e) =>
            {
                timer.Stop();
                HidePip();
                CloseViewer();
                Action<int> h = OnChosen;
                if (h != null)
                {
                    try
                    {
                        h(-1);
                    }
                    catch
                    {
                    }
                }
            };

            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += (s, e) => RefreshTexts();
            timer.Start();
            RefreshTexts();
            UpdateSelection();
        }

        private bool revealed;

        // Mueve la ventana (ya pintada) a la pantalla y empieza el fundido de entrada.
        private void Reveal()
        {
            if (revealed)
            {
                return;
            }
            revealed = true;
            Left = 0;
            Top = 0;
            root.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        // Cierre con animación: el menú se desvanece y entonces se cierra la ventana (solo opacidad: es lo más barato).
        public void CloseAnimated()
        {
            try
            {
                if (closing)
                {
                    return;
                }
                closing = true;
                DoubleAnimation fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                fade.Completed += (s, e) =>
                {
                    try
                    {
                        Close();
                    }
                    catch
                    {
                    }
                };
                root.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            catch
            {
                Close();
            }
        }

        private bool closing;

        // Cambia el contenido de la ventana (otra pantalla del menú) sin cerrarla.
        public void SetPage(string title, string message, List<MenuEntry> newEntries, int defaultIndex, int newCancelIndex,
                            ImageSource cover, ImageSource backdrop, Func<string> newSessionProvider, string totalText)
        {
            achMode = false;
            ach = null;
            galMode = false;
            CloseViewer();
            entries = newEntries;
            cancelIndex = newCancelIndex;
            sessionProvider = newSessionProvider;
            rows.Clear();
            icons.Clear();
            labels.Clear();
            badges.Clear();
            rights.Clear();
            shimmerSel = -1;
            selected = Math.Max(0, Math.Min(defaultIndex, entries.Count - 1));
            done = false;
            lastAction = null;
            shownAt = DateTime.UtcNow;
            if (backdrop != null)
            {
                SetBackdrop(backdrop);
            }
            pageHost.Children.Clear();
            pageHost.Children.Add(BuildCanvas(title, message, cover, totalText, buttonStyle, guideEnabled));
            RefreshTexts();
            UpdateSelection();
            HidePip();
            Dispatcher.BeginInvoke(new Action(UpdatePip), DispatcherPriority.Loaded);
        }

        // Parte derecha de la cabecera: batería del mando (si hay) y hora.
        private UIElement BuildHeaderRight()
        {
            StackPanel p = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            batteryHost = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 40, 0),
                Visibility = Visibility.Collapsed
            };
            clockText = new TextBlock
            {
                FontSize = 40,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            p.Children.Add(batteryHost);
            p.Children.Add(clockText);
            ShowBattery(lastBattery);
            return p;
        }

        private UIElement SmallIcon(string key, Brush stroke, double size)
        {
            Shapes.Path path = MakeIcon(key);
            path.Stroke = stroke;
            return new Viewbox
            {
                Width = size,
                Height = size,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Canvas { Width = 24, Height = 24, Children = { path } }
            };
        }

        private static readonly Brush OkBrush = Make("#7DDC9A");
        private static readonly Brush MidBrush = Make("#FFD479");

        private void ShowBattery(List<BatteryInfo> list)
        {
            lastBattery = list;
            if (batteryHost == null)
            {
                return;
            }
            batteryHost.Children.Clear();
            if (list == null || list.Count == 0)
            {
                batteryHost.Visibility = Visibility.Collapsed;
                return;
            }
            for (int i = 0; i < list.Count; i++)
            {
                BatteryInfo b = list[i];
                StackPanel one = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(i == 0 ? 0 : 30, 0, 0, 0)
                };
                string key = b.Wired ? "battery-charging" : (b.Level >= 3 ? "battery-full" : (b.Level == 2 ? "battery-medium" : (b.Level == 1 ? "battery-low" : "battery")));
                Brush color = b.Wired ? OkBrush : (b.Level <= 1 ? DangerBrush : (b.Level == 2 ? MidBrush : OkBrush));
                one.Children.Add(SmallIcon("gamepad-2", MutedBrush, 34));
                if (list.Count > 1)
                {
                    one.Children.Add(new TextBlock
                    {
                        Text = b.Index.ToString(),
                        FontSize = 24,
                        FontWeight = FontWeights.Bold,
                        Foreground = TextBrush,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(4, 0, 0, 0)
                    });
                }
                UIElement bat = SmallIcon(key, color, 40);
                ((FrameworkElement)bat).Margin = new Thickness(10, 0, 0, 0);
                one.Children.Add(bat);
                string txt;
                if (b.Percent >= 0)
                {
                    txt = b.Percent + " %";
                }
                else
                {
                    txt = b.Level >= 3 ? ">70 %" : (b.Level == 2 ? "~45 %" : (b.Level == 1 ? "~15 %" : "<5 %"));
                }
                if (b.Wired)
                {
                    txt = ""; // enchufado: solo el rayo (el porcentaje que informan muchos mandos al cargar no es fiable)
                }
                one.Children.Add(new TextBlock
                {
                    Text = txt,
                    FontSize = 26,
                    Foreground = color,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                });
                batteryHost.Children.Add(one);
            }
            batteryHost.Visibility = Visibility.Visible;
        }

        private void SetBackdrop(ImageSource backdrop)
        {
            if (backdrop != null && !ReferenceEquals(backdropImage.Source, backdrop))
            {
                backdropImage.Source = backdrop;
            }
        }

        // Tarjeta de la miniatura: a la derecha de la lista, con un margen amplio respecto a los botones.
        private const double PipX = 1424, PipY = 400, PipW = 376, PipH = 212;

        // Miniatura del juego: la compone Windows (DWM) sobre este hueco. Casi no usa CPU ni GPU.
        private void UpdatePip()
        {
            try
            {
                bool want = PipSource != IntPtr.Zero && pipFrame != null && canvasHasGame && !galMode && !achMode && !viewing && !done;
                if (!want)
                {
                    HidePip();
                    return;
                }
                if (pipKnownBlack || pipUsingFallback)
                {
                    ShowPipFallback();
                    return;
                }
                IntPtr h = new WindowInteropHelper(this).Handle;
                if (h == IntPtr.Zero)
                {
                    return;
                }
                if (pipThumb == IntPtr.Zero)
                {
                    pipThumb = DwmThumb.Register(h, PipSource, out pipSrcW, out pipSrcH);
                    if (pipThumb == IntPtr.Zero)
                    {
                        logger.Info("Miniatura del juego: no disponible para esta ventana.");
                        PipSource = IntPtr.Zero;
                        pipKnownBlack = true;
                        ShowPipFallback();
                        return;
                    }
                    logger.Info("Miniatura del juego: " + pipSrcW + "x" + pipSrcH + (PipLive ? " (en directo)" : " (juego pausado)"));
                }
                double scale = Math.Min(PipW / pipSrcW, PipH / pipSrcH);
                double w = pipSrcW * scale, hh = pipSrcH * scale;
                double x = PipX + (PipW - w) / 2, y = PipY + (PipH - hh) / 2;
                Point tl = root.TransformToVisual(this).Transform(new Point(x, y));
                Point br = root.TransformToVisual(this).Transform(new Point(x + w, y + hh));
                PresentationSource ps = PresentationSource.FromVisual(this);
                double sx = ps != null ? ps.CompositionTarget.TransformToDevice.M11 : 1.0;
                double sy = ps != null ? ps.CompositionTarget.TransformToDevice.M22 : 1.0;
                pipRectX = (int)Math.Round(tl.X * sx);
                pipRectY = (int)Math.Round(tl.Y * sy);
                pipRectW = (int)Math.Round(br.X * sx) - pipRectX;
                pipRectH = (int)Math.Round(br.Y * sy) - pipRectY;
                DwmThumb.Place(pipThumb, pipRectX, pipRectY, pipRectX + pipRectW, pipRectY + pipRectH, true);
                pipFrame.Visibility = Visibility.Visible;
                pipFallbackHost.Visibility = Visibility.Collapsed;
                SetPipLabel(PipLive ? Loc.T("● En directo") : Loc.T("Último fotograma"), PipLive ? OkBrush : MutedBrush);
                if (!pipChecked)
                {
                    // Algunos juegos (pantalla completa exclusiva, protección de contenido) no dejan ver su imagen y la
                    // miniatura sale negra: pasado un momento se comprueba lo que se ve y, si es negro, se usa otra imagen.
                    pipChecked = true;
                    if (pipCheckTimer == null)
                    {
                        pipCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                        pipCheckTimer.Tick += (s2, e2) =>
                        {
                            pipCheckTimer.Stop();
                            CheckPipBlack();
                        };
                    }
                    pipCheckTimer.Stop();
                    pipCheckTimer.Start();
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo mostrar la miniatura del juego");
            }
        }

        private void SetPipLabel(string text, Brush color)
        {
            if (pipLabel == null)
            {
                return;
            }
            pipLabel.Text = text;
            pipLabel.Foreground = color;
            pipLabel.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        // Mira, en la pantalla, la zona donde Windows dibuja la miniatura (es lo que ve el usuario).
        private void CheckPipBlack()
        {
            try
            {
                if (pipThumb == IntPtr.Zero || pipKnownBlack || done || pipRectW < 16 || pipRectH < 16)
                {
                    return;
                }
                Point o = PointToScreen(new Point(0, 0));
                bool black = RegionIsBlack((int)Math.Round(o.X) + pipRectX, (int)Math.Round(o.Y) + pipRectY, pipRectW, pipRectH);
                logger.Info("Miniatura del juego: " + (black ? "se ve negra; se usa otra imagen" : "con imagen"));
                if (black)
                {
                    pipKnownBlack = true;
                    HidePipThumb();
                    ShowPipFallback();
                }
            }
            catch (Exception ex)
            {
                logger.Info("No se pudo comprobar la miniatura: " + ex.Message);
            }
        }

        private static bool RegionIsBlack(int x, int y, int w, int h)
        {
            using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
                }
                System.Drawing.Imaging.BitmapData d = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    byte[] buf = new byte[d.Stride * h];
                    System.Runtime.InteropServices.Marshal.Copy(d.Scan0, buf, 0, buf.Length);
                    int total = 0, bright = 0;
                    for (int row = 0; row < h; row += 2)
                    {
                        int line = row * d.Stride;
                        for (int col = 0; col < w; col += 2)
                        {
                            int i = line + col * 4;
                            int max = Math.Max(buf[i], Math.Max(buf[i + 1], buf[i + 2]));
                            total++;
                            if (max > 28)
                            {
                                bright++;
                            }
                        }
                    }
                    return total > 0 && bright < total * 0.004;
                }
                finally
                {
                    bmp.UnlockBits(d);
                }
            }
        }

        // Imagen alternativa en el hueco de la miniatura: última captura del juego o, si no hay, su fondo.
        private void ShowPipFallback()
        {
            if (pipFrame == null || pipFallbackHost == null)
            {
                return;
            }
            if (PipFallback == null && PipFallbackLoader != null)
            {
                try
                {
                    Tuple<ImageSource, bool> r = PipFallbackLoader();
                    PipFallbackLoader = null;
                    if (r != null)
                    {
                        PipFallback = r.Item1;
                        PipFallbackIsCapture = r.Item2;
                    }
                }
                catch (Exception ex)
                {
                    logger.Info("No se pudo cargar la imagen alternativa de la miniatura: " + ex.Message);
                }
            }
            if (PipFallback == null)
            {
                pipFrame.Visibility = Visibility.Collapsed;
                pipFallbackHost.Visibility = Visibility.Collapsed;
                SetPipLabel("", MutedBrush);
                return;
            }
            pipUsingFallback = true;
            ImageBrush ib = new ImageBrush(PipFallback) { Stretch = Stretch.UniformToFill };
            ib.Freeze();
            pipFallbackHost.Background = ib;
            pipFrame.Visibility = Visibility.Visible;
            pipFallbackHost.Visibility = Visibility.Visible;
            SetPipLabel(PipFallbackIsCapture ? Loc.T("Última captura") : "", MutedBrush);
        }

        private void HidePipThumb()
        {
            if (pipThumb != IntPtr.Zero)
            {
                DwmThumb.Unregister(pipThumb);
                pipThumb = IntPtr.Zero;
            }
        }

        private void HidePip()
        {
            HidePipThumb();
            if (pipCheckTimer != null)
            {
                pipCheckTimer.Stop();
            }
            pipChecked = false;
            if (pipFrame != null)
            {
                pipFrame.Visibility = Visibility.Collapsed;
            }
            if (pipFallbackHost != null)
            {
                pipFallbackHost.Visibility = Visibility.Collapsed;
            }
            if (pipLabel != null)
            {
                pipLabel.Visibility = Visibility.Collapsed;
            }
        }

        private Grid BuildCanvas(string title, string message, ImageSource cover, string totalText,
                                 string buttonStyle, bool guideEnabled)
        {
            bool hasGame = title != null;

            Grid canvas = new Grid { Width = 1920, Height = 1080 };
            canvas.RowDefinitions.Add(new RowDefinition { Height = new GridLength(150) });
            canvas.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            canvas.RowDefinitions.Add(new RowDefinition { Height = new GridLength(120) });

            // Cabecera: nombre y hora.
            Grid header = new Grid { Margin = new Thickness(120, 56, 120, 0) };
            TextBlock brand = new TextBlock
            {
                Text = "", // sin marca: la cabecera solo muestra la hora y el estado del mando
                FontSize = 24,
                Foreground = MutedBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            header.Children.Add(brand);
            header.Children.Add(BuildHeaderRight());
            Grid.SetRow(header, 0);
            canvas.Children.Add(header);

            // Cuerpo.
            Grid body = new Grid { Margin = new Thickness(120, 0, 120, 0) };
            Grid.SetRow(body, 1);
            if (hasGame)
            {
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(480) });
            }
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (hasGame)
            {
                StackPanel left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                if (cover != null)
                {
                    Image img = new Image
                    {
                        Source = cover,
                        Stretch = Stretch.Uniform,
                        MaxWidth = 440,
                        MaxHeight = 560,
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    Border frame = new Border
                    {
                        CornerRadius = new CornerRadius(14),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Child = img,
                        Effect = new System.Windows.Media.Effects.DropShadowEffect
                        {
                            BlurRadius = 40,
                            ShadowDepth = 8,
                            Opacity = 0.6,
                            Color = Colors.Black
                        }
                    };
                    left.Children.Add(frame);
                }
                left.Children.Add(new TextBlock
                {
                    Text = title,
                    FontSize = 34,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 440,
                    Margin = new Thickness(0, 26, 0, 14)
                });
                WrapPanel chips = new WrapPanel { MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left };
                sessionLine = new TextBlock { FontSize = 20, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center };
                chips.Children.Add(Chip(sessionLine));
                if (!string.IsNullOrEmpty(totalText))
                {
                    chips.Children.Add(Chip(new TextBlock { Text = totalText, FontSize = 20, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center }));
                }
                left.Children.Add(chips);
                Grid.SetColumn(left, 0);
                body.Children.Add(left);
            }

            StackPanel right = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Width = 640,
                HorizontalAlignment = hasGame ? HorizontalAlignment.Left : HorizontalAlignment.Center
            };
            if (hasGame)
            {
                right.Margin = new Thickness(70, 0, 0, 0);
            }
            if (!string.IsNullOrEmpty(message))
            {
                right.Children.Add(new TextBlock
                {
                    Text = message,
                    FontSize = 26,
                    Foreground = TextBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 28)
                });
            }
            for (int i = 0; i < entries.Count; i++)
            {
                right.Children.Add(BuildRow(i));
            }
            spotInit = false;
            AnimateRowsIn();
            Grid.SetColumn(right, hasGame ? 1 : 0);
            body.Children.Add(right);
            canvas.Children.Add(body);

            canvasHasGame = hasGame;
            pipFrame = null;
            pipLabel = null;
            pipFallbackHost = null;
            if (hasGame)
            {
                // Tarjeta de la miniatura del juego (a la derecha de la lista). La imagen la dibuja Windows encima.
                pipFrame = new Border
                {
                    Width = PipW + 16,
                    Height = PipH + 16,
                    Margin = new Thickness(PipX - 8, PipY - 8, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    CornerRadius = new CornerRadius(18),
                    Background = new SolidColorBrush(Color.FromArgb(215, 6, 7, 10)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                    BorderThickness = new Thickness(1.5),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 36, ShadowDepth = 8, Opacity = 0.55, Color = Colors.Black },
                    Visibility = Visibility.Collapsed
                };
                Grid.SetRowSpan(pipFrame, 3);
                canvas.Children.Add(pipFrame);
                pipFallbackHost = new Border
                {
                    Width = PipW,
                    Height = PipH,
                    Margin = new Thickness(PipX, PipY, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    CornerRadius = new CornerRadius(12),
                    Visibility = Visibility.Collapsed
                };
                Grid.SetRowSpan(pipFallbackHost, 3);
                canvas.Children.Add(pipFallbackHost);
                pipLabel = new TextBlock
                {
                    FontSize = 20,
                    Foreground = MutedBrush,
                    Margin = new Thickness(PipX, PipY + PipH + 24, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Visibility = Visibility.Collapsed
                };
                Grid.SetRowSpan(pipLabel, 3);
                canvas.Children.Add(pipLabel);
            }

            StackPanel hints = BuildHints(buttonStyle, guideEnabled, true);
            Grid.SetRow(hints, 2);
            canvas.Children.Add(hints);

            return canvas;
        }

        // Etiqueta redondeada para datos cortos (sesión, tiempo total...).
        private UIElement Chip(UIElement content)
        {
            return new Border
            {
                Background = PillBrush,
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(16, 6, 16, 7),
                Margin = new Thickness(0, 0, 10, 10),
                Child = content
            };
        }

        // Pie con las ayudas de botones.
        private StackPanel BuildHints(string buttonStyle, bool guideEnabled, bool showAccept)
        {
            StackPanel hints = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(120, 0, 120, 44),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            string accept = "A";
            string back = "B";
            string guide = "Xbox";
            if (string.Equals(buttonStyle, "PlayStation", StringComparison.OrdinalIgnoreCase))
            {
                accept = "✕";
                back = "○";
                guide = "PS";
            }
            else if (string.Equals(buttonStyle, "Nintendo", StringComparison.OrdinalIgnoreCase))
            {
                accept = "B";
                back = "A";
                guide = "Home";
            }
            if (showAccept)
            {
                hints.Children.Add(Hint(accept, Loc.T("Seleccionar")));
            }
            hints.Children.Add(Hint(back, Loc.T("Volver")));
            if (guideEnabled)
            {
                hints.Children.Add(Hint(guide, Loc.T("Cerrar menú")));
            }
            return hints;
        }

        // Pantalla de logros (lista tipo PS5, con ventana de 6 filas alrededor de la selección).
        public void SetAchievements(string title, string summary, double fraction, List<AchievementItem> list,
                                    ImageSource cover, ImageSource backdrop)
        {
            HidePip();
            achMode = true;
            galMode = false;
            CloseViewer();
            ach = list;
            entries = new List<MenuEntry>();
            for (int i = 0; i < list.Count; i++)
            {
                entries.Add(new MenuEntry(null, null));
            }
            cancelIndex = 0;
            rows.Clear();
            icons.Clear();
            labels.Clear();
            badges.Clear();
            rights.Clear();
            selected = 0;
            done = false;
            lastAction = null;
            shownAt = DateTime.UtcNow;
            if (backdrop != null)
            {
                SetBackdrop(backdrop);
            }
            pageHost.Children.Clear();
            pageHost.Children.Add(BuildAchCanvas(title, summary, fraction, cover));
            RefreshTexts();
            UpdateSelection();
        }

        private Grid BuildAchCanvas(string title, string summary, double fraction, ImageSource cover)
        {
            Grid canvas = new Grid { Width = 1920, Height = 1080 };
            canvas.RowDefinitions.Add(new RowDefinition { Height = new GridLength(150) });
            canvas.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            canvas.RowDefinitions.Add(new RowDefinition { Height = new GridLength(120) });

            Grid header = new Grid { Margin = new Thickness(120, 56, 120, 0) };
            header.Children.Add(new TextBlock
            {
                Text = Loc.T("Logros"),
                FontSize = 24,
                Foreground = MutedBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(BuildHeaderRight());
            Grid.SetRow(header, 0);
            canvas.Children.Add(header);
            sessionLine = null;

            Grid body = new Grid { Margin = new Thickness(120, 0, 120, 0) };
            Grid.SetRow(body, 1);
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            StackPanel left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            if (cover != null)
            {
                Image img = new Image { Source = cover, Stretch = Stretch.Uniform, MaxWidth = 360, MaxHeight = 440, HorizontalAlignment = HorizontalAlignment.Left };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                left.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(14),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = img,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 40, ShadowDepth = 8, Opacity = 0.6, Color = Colors.Black }
                });
            }
            left.Children.Add(new TextBlock
            {
                Text = title ?? "",
                FontSize = 30,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
                Margin = new Thickness(0, 22, 0, 8)
            });
            left.Children.Add(new TextBlock { Text = summary, FontSize = 22, Foreground = TextBrush, Margin = new Thickness(0, 0, 0, 12) });
            Grid track = new Grid { Width = 360, Height = 10, HorizontalAlignment = HorizontalAlignment.Left };
            track.Children.Add(new Border { Background = PillBrush, CornerRadius = new CornerRadius(5) });
            double f = Math.Max(0, Math.Min(1, fraction));
            track.Children.Add(new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(5),
                Width = Math.Max(10, 360 * f),
                HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = f > 0 ? Visibility.Visible : Visibility.Collapsed
            });
            left.Children.Add(track);
            Grid.SetColumn(left, 0);
            body.Children.Add(left);

            Grid right = new Grid { VerticalAlignment = VerticalAlignment.Center, Width = 1020, HorizontalAlignment = HorizontalAlignment.Left };
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            achPos = new TextBlock
            {
                FontSize = 18,
                Foreground = MutedBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 4, 10)
            };
            achPanel = new StackPanel();
            Grid.SetRow(achPos, 0);
            Grid.SetRow(achPanel, 1);
            right.Children.Add(achPos);
            right.Children.Add(achPanel);
            Grid.SetColumn(right, 1);
            body.Children.Add(right);
            canvas.Children.Add(body);

            StackPanel hints = BuildHints(buttonStyle, guideEnabled, false);
            Grid.SetRow(hints, 2);
            canvas.Children.Add(hints);
            return canvas;
        }

        // ---------------- Galería de capturas y clips ----------------

        public void SetGallery(string title, string summary, List<CaptureItem> list, ImageSource backdrop)
        {
            HidePip();
            galMode = true;
            achMode = false;
            gal = list;
            CloseViewer();
            entries = new List<MenuEntry>();
            for (int i = 0; i < list.Count; i++)
            {
                entries.Add(new MenuEntry(null, null));
            }
            cancelIndex = 0;
            rows.Clear();
            icons.Clear();
            labels.Clear();
            badges.Clear();
            rights.Clear();
            selected = 0;
            done = false;
            lastAction = null;
            shownAt = DateTime.UtcNow;
            if (backdrop != null)
            {
                SetBackdrop(backdrop);
            }
            pageHost.Children.Clear();
            pageHost.Children.Add(BuildGalCanvas(title, summary));
            RefreshTexts();
            UpdateSelection();
        }

        private Grid BuildGalCanvas(string title, string summary)
        {
            Grid canvas = new Grid { Width = 1920, Height = 1080 };
            canvas.RowDefinitions.Add(new RowDefinition { Height = new GridLength(150) });
            canvas.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            canvas.RowDefinitions.Add(new RowDefinition { Height = new GridLength(120) });

            Grid header = new Grid { Margin = new Thickness(120, 56, 120, 0) };
            header.Children.Add(new TextBlock
            {
                Text = Loc.T("Capturas"),
                FontSize = 24,
                Foreground = MutedBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(BuildHeaderRight());
            Grid.SetRow(header, 0);
            canvas.Children.Add(header);

            StackPanel body = new StackPanel { Margin = new Thickness(120, 0, 120, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid top = new Grid { Margin = new Thickness(8, 0, 8, 12) };
            top.Children.Add(new TextBlock
            {
                Text = (title ?? "") + (string.IsNullOrEmpty(summary) ? "" : "  ·  " + summary),
                FontSize = 26,
                Foreground = TextBrush,
                FontWeight = FontWeights.SemiBold
            });
            galPos = new TextBlock { FontSize = 18, Foreground = MutedBrush, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            top.Children.Add(galPos);
            body.Children.Add(top);

            galGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            for (int c = 0; c < GalCols; c++)
            {
                galGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(408) });
            }
            for (int r = 0; r < GalRows; r++)
            {
                galGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(240) });
            }
            body.Children.Add(galGrid);
            Grid.SetRow(body, 1);
            canvas.Children.Add(body);

            StackPanel hints = BuildHints(buttonStyle, guideEnabled, true);
            Grid.SetRow(hints, 2);
            canvas.Children.Add(hints);
            return canvas;
        }

        private static ImageSource LoadThumb(string path)
        {
            ImageSource img;
            if (thumbCache.TryGetValue(path, out img))
            {
                return img;
            }
            try
            {
                BitmapImage bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 440;
                bi.UriSource = new Uri(path);
                bi.EndInit();
                bi.Freeze();
                img = bi;
            }
            catch
            {
                img = null;
            }
            if (thumbCache.Count > 120)
            {
                thumbCache.Clear();
            }
            thumbCache[path] = img;
            return img;
        }

        // Miniatura de un clip: la da el propio Windows. Se pide en segundo plano y, al llegar, se repinta la cuadrícula.
        private ImageSource VideoThumb(string path)
        {
            ImageSource img;
            if (thumbCache.TryGetValue(path, out img))
            {
                return img;
            }
            if (!thumbPending.Add(path))
            {
                return null;
            }
            System.Threading.Thread t = new System.Threading.Thread(() =>
            {
                ImageSource r = null;
                try
                {
                    r = ShellThumb.Get(path, 440, 248);
                }
                catch
                {
                }
                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        thumbPending.Remove(path);
                        thumbCache[path] = r;
                        if (r != null && galMode && !viewing && !done)
                        {
                            RenderGallery();
                        }
                    }));
                }
                catch
                {
                }
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            return null;
        }

        private void RenderGallery()
        {
            if (galGrid == null || gal == null)
            {
                return;
            }
            galGrid.Children.Clear();
            int n = gal.Count;
            int totalRows = (n + GalCols - 1) / GalCols;
            int selRow = selected / GalCols;
            int startRow = Math.Max(0, Math.Min(selRow - 1, totalRows - GalRows));
            for (int r = 0; r < GalRows; r++)
            {
                for (int c = 0; c < GalCols; c++)
                {
                    int idx = (startRow + r) * GalCols + c;
                    if (idx >= n)
                    {
                        continue;
                    }
                    UIElement tile = BuildTile(gal[idx], idx == selected);
                    Grid.SetRow(tile, r);
                    Grid.SetColumn(tile, c);
                    galGrid.Children.Add(tile);
                }
            }
            if (galPos != null)
            {
                galPos.Text = (selected + 1) + " / " + n;
            }
        }

        private UIElement BuildTile(CaptureItem item, bool sel)
        {
            Border outer = new Border
            {
                Margin = new Thickness(8),
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(4),
                BorderBrush = sel ? Brushes.White : Brushes.Transparent,
                Background = RowBrush
            };
            Grid g = new Grid();
            Border pic = new Border { CornerRadius = new CornerRadius(10) };
            ImageSource thumb = item.IsVideo ? VideoThumb(item.Path) : LoadThumb(item.Path);
            if (thumb != null)
            {
                ImageBrush ib = new ImageBrush(thumb) { Stretch = Stretch.UniformToFill };
                pic.Background = ib;
            }
            else
            {
                pic.Background = BadgeBrush;
                pic.Child = SmallIcon(item.IsVideo ? "video" : "images", MutedBrush, 64);
            }
            g.Children.Add(pic);
            if (item.IsVideo)
            {
                // Símbolo de play para distinguir los clips de las capturas.
                Border badge = new Border
                {
                    Width = 76,
                    Height = 76,
                    CornerRadius = new CornerRadius(38),
                    Background = new SolidColorBrush(Color.FromArgb(185, 0, 0, 0)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
                    BorderThickness = new Thickness(3),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(6, 0, 0, 0),
                    Child = SmallIcon("play", Brushes.White, 40)
                };
                g.Children.Add(badge);
            }
            Border cap = new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(0, 0, 10, 10),
                Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
                Padding = new Thickness(12, 6, 12, 6)
            };
            cap.Child = new TextBlock
            {
                Text = (item.IsVideo ? Loc.T("Clip · ") : "") + item.Time.ToString("g"),
                FontSize = 17,
                Foreground = Brushes.White
            };
            g.Children.Add(cap);
            outer.Child = g;
            return outer;
        }

        private void MoveGrid(int delta)
        {
            if (gal == null || gal.Count == 0)
            {
                return;
            }
            int ni = selected + delta;
            if (ni < 0 || ni >= gal.Count)
            {
                return;
            }
            selected = ni;
            UpdateSelection();
        }

        // ---------------- Visor (imagen o clip a pantalla completa) ----------------

        private void ButtonLabels(out string accept, out string back)
        {
            accept = "A";
            back = "B";
            if (string.Equals(buttonStyle, "PlayStation", StringComparison.OrdinalIgnoreCase))
            {
                accept = "✕";
                back = "○";
            }
            else if (string.Equals(buttonStyle, "Nintendo", StringComparison.OrdinalIgnoreCase))
            {
                accept = "B";
                back = "A";
            }
        }

        private void BuildViewer()
        {
            viewer = new Grid { Background = new SolidColorBrush(Color.FromArgb(245, 5, 6, 9)), Visibility = Visibility.Collapsed };
            viewImg = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(60, 60, 60, 140) };
            RenderOptions.SetBitmapScalingMode(viewImg, BitmapScalingMode.HighQuality);
            viewMedia = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(60, 60, 60, 140),
                Visibility = Visibility.Collapsed
            };
            viewMedia.Volume = 1.0;
            viewMedia.ScrubbingEnabled = true;
            RenderOptions.SetBitmapScalingMode(viewMedia, BitmapScalingMode.LowQuality);
            viewMedia.MediaOpened += (s, e) =>
            {
                // La sesión de audio del clip se crea al empezar a sonar: se vuelve a quitar el silencio ya con el clip en marcha.
                KickClipAudio();
                AnalyzeClipAudio();
                try
                {
                    logger.Info("Clip abierto: " + viewMedia.NaturalVideoWidth + "x" + viewMedia.NaturalVideoHeight +
                                ", duración " + (viewMedia.NaturalDuration.HasTimeSpan ? viewMedia.NaturalDuration.TimeSpan.ToString() : "?") +
                                ", audio: " + viewMedia.HasAudio);
                }
                catch
                {
                }
            };
            viewMedia.MediaFailed += (s, e) =>
            {
                mediaFailed = true;
                mediaPlaying = false;
                viewHint.Text = Loc.T("No se puede reproducir este clip aquí. Pulsa aceptar para abrirlo con el reproductor de Windows.");
            };
            viewMedia.MediaEnded += (s, e) =>
            {
                viewMedia.Pause();
                mediaPlaying = false;
            };
            viewCaption = new TextBlock
            {
                FontSize = 22,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(80, 0, 80, 84)
            };
            viewHint = new TextBlock
            {
                FontSize = 18,
                Foreground = MutedBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(80, 0, 80, 40)
            };
            viewer.Children.Add(viewImg);
            viewer.Children.Add(viewMedia);
            viewer.Children.Add(viewCaption);
            viewer.Children.Add(viewHint);
            root.Children.Add(viewer);
        }

        private void OpenViewer()
        {
            if (gal == null || gal.Count == 0)
            {
                return;
            }
            viewing = true;
            viewer.Visibility = Visibility.Visible;
            ShowViewerItem();
        }

        private void ShowViewerItem()
        {
            CaptureItem item = gal[selected];
            string acc, back;
            ButtonLabels(out acc, out back);
            viewCaptionBase = item.Name + "   ·   " + item.Time.ToString("g") + "   ·   " + (selected + 1) + " / " + gal.Count;
            viewCaption.Text = viewCaptionBase;
            mediaFailed = false;
            audioUnsupported = false;
            clipNote = "";
            try
            {
                viewMedia.Stop();
            }
            catch
            {
            }
            if (item.IsVideo)
            {
                viewImg.Source = null;
                viewMedia.Visibility = Visibility.Visible;
                viewHint.Text = Loc.T("◀ ▶ Retroceder / adelantar 10 s     ▲ ▼ Clip anterior / siguiente     {0} Pausa / Reanudar     {1} Cerrar", acc, back);
                try
                {
                    SetClipAudio(true);
                    viewMedia.Source = new Uri(item.Path);
                    viewMedia.Play();
                    mediaPlaying = true;
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "No se pudo reproducir el clip");
                    mediaFailed = true;
                }
            }
            else
            {
                viewMedia.Visibility = Visibility.Collapsed;
                viewMedia.Source = null;
                SetClipAudio(false);
                viewHint.Text = Loc.T("◀ ▶ Cambiar     {0} Cerrar", back);
                try
                {
                    BitmapImage bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.DecodePixelWidth = 1920;
                    bi.UriSource = new Uri(item.Path);
                    bi.EndInit();
                    bi.Freeze();
                    viewImg.Source = bi;
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "No se pudo abrir la captura");
                    viewImg.Source = null;
                }
            }
        }

        private void CloseViewer()
        {
            if (viewer == null)
            {
                return;
            }
            viewing = false;
            try
            {
                viewMedia.Stop();
                viewMedia.Source = null;
            }
            catch
            {
            }
            SetClipAudio(false);
            viewImg.Source = null;
            viewer.Visibility = Visibility.Collapsed;
        }

        // Comprueba qué pistas trae el clip y qué entiende de ellas el reproductor de WPF, para avisar si falta el sonido.
        private void AnalyzeClipAudio()
        {
            if (gal == null || selected < 0 || selected >= gal.Count)
            {
                return;
            }
            string path = gal[selected].Path;
            bool wpfAudio = false;
            try
            {
                wpfAudio = viewMedia.HasAudio;
            }
            catch
            {
            }
            System.Threading.Tasks.Task.Run(() =>
            {
                string desc;
                bool hasTrack = Mp4Info.HasAudioTrack(path, out desc);
                logger.Info("Clip pistas: " + desc + " | el visor detecta audio: " + wpfAudio);
                if (wpfAudio)
                {
                    return;
                }
                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!viewing || gal == null || selected >= gal.Count || gal[selected].Path != path)
                        {
                            return;
                        }
                        string acc, back;
                        ButtonLabels(out acc, out back);
                        if (hasTrack)
                        {
                            audioUnsupported = true;
                            viewHint.Text = Loc.T("◀ ▶ Retroceder / adelantar 10 s     ▲ ▼ Clip anterior / siguiente     {0} Abrir con el reproductor de Windows (con sonido)     {1} Cerrar", acc, back);
                        }
                        else if (desc.IndexOf("no mp4", StringComparison.Ordinal) < 0)
                        {
                            clipNote = Loc.T("   ·   sin audio");
                            UpdateClipClock();
                        }
                    }));
                }
                catch
                {
                }
            });
        }

        private void KickClipAudio()
        {
            try
            {
                Action<bool> h = ClipAudio;
                if (h != null && clipAudioOn)
                {
                    h(true);
                    DispatcherTimer once = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                    once.Tick += (s2, e2) =>
                    {
                        once.Stop();
                        if (clipAudioOn)
                        {
                            h(true);
                        }
                    };
                    once.Start();
                }
            }
            catch
            {
            }
        }

        private void SetClipAudio(bool on)
        {
            if (clipAudioOn == on)
            {
                return;
            }
            clipAudioOn = on;
            try
            {
                Action<bool> h = ClipAudio;
                if (h != null)
                {
                    h(on);
                }
            }
            catch
            {
            }
        }

        // Izquierda/derecha: en un clip salta 10 s; en una captura pasa a la anterior/siguiente.
        private void ViewSeek(int dir)
        {
            if (gal == null || selected < 0 || selected >= gal.Count)
            {
                return;
            }
            if (!gal[selected].IsVideo)
            {
                ViewStep(dir);
                return;
            }
            if (mediaFailed)
            {
                return;
            }
            try
            {
                TimeSpan pos = viewMedia.Position + TimeSpan.FromSeconds(10 * dir);
                if (pos < TimeSpan.Zero)
                {
                    pos = TimeSpan.Zero;
                }
                if (viewMedia.NaturalDuration.HasTimeSpan && pos > viewMedia.NaturalDuration.TimeSpan)
                {
                    pos = viewMedia.NaturalDuration.TimeSpan - TimeSpan.FromMilliseconds(200);
                    if (pos < TimeSpan.Zero)
                    {
                        pos = TimeSpan.Zero;
                    }
                }
                viewMedia.Position = pos;
                if (!mediaPlaying && dir < 0)
                {
                    // Si el clip había terminado, retroceder lo deja listo para verse de nuevo.
                    viewMedia.Play();
                    mediaPlaying = true;
                }
                UpdateClipClock();
            }
            catch
            {
            }
        }

        private void UpdateClipClock()
        {
            if (!viewing || gal == null || selected < 0 || selected >= gal.Count || !gal[selected].IsVideo || mediaFailed)
            {
                return;
            }
            try
            {
                string cur = viewMedia.Position.ToString(@"mm\:ss");
                string tot = viewMedia.NaturalDuration.HasTimeSpan ? viewMedia.NaturalDuration.TimeSpan.ToString(@"mm\:ss") : "--:--";
                viewCaption.Text = viewCaptionBase + "   ·   " + cur + " / " + tot + clipNote;
            }
            catch
            {
            }
        }

        private void ViewStep(int d)
        {
            if (gal == null)
            {
                return;
            }
            int ni = selected + d;
            if (ni < 0 || ni >= gal.Count)
            {
                return;
            }
            selected = ni;
            ShowViewerItem();
        }

        private void ViewAccept()
        {
            CaptureItem item = gal[selected];
            if (!item.IsVideo)
            {
                return;
            }
            if (mediaFailed || audioUnsupported)
            {
                try
                {
                    if (audioUnsupported)
                    {
                        viewMedia.Pause();
                        mediaPlaying = false;
                    }
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.Path) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "No se pudo abrir el clip con el reproductor");
                }
                return;
            }
            if (mediaPlaying)
            {
                viewMedia.Pause();
                mediaPlaying = false;
            }
            else
            {
                viewMedia.Play();
                mediaPlaying = true;
            }
        }

        private void RenderAchWindow()
        {
            if (achPanel == null || ach == null)
            {
                return;
            }
            achPanel.Children.Clear();
            int n = ach.Count;
            int start = Math.Max(0, Math.Min(selected - AchVisible / 2, n - AchVisible));
            int end = Math.Min(n, start + AchVisible);
            for (int i = start; i < end; i++)
            {
                achPanel.Children.Add(BuildAchRow(ach[i], i == selected));
            }
            if (achPos != null)
            {
                achPos.Text = (selected + 1) + " / " + n;
            }
        }

        private static ImageSource LoadIcon(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            ImageSource img;
            if (iconCache.TryGetValue(path, out img))
            {
                return img;
            }
            try
            {
                BitmapImage bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 144;
                bi.UriSource = new Uri(path);
                bi.EndInit();
                bi.Freeze();
                img = bi;
            }
            catch
            {
                img = null;
            }
            iconCache[path] = img;
            return img;
        }

        private UIElement BuildAchRow(AchievementItem a, bool sel)
        {
            bool secret = a.Hidden && !a.Unlocked;
            Brush main = sel ? SelTextBrush : TextBrush;
            Brush muted = sel ? Make("#4A5168") : MutedBrush;

            Border b = new Border
            {
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 10),
                Background = sel ? SelBrush : RowBrush,
                Height = 114
            };
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            ImageSource icon = secret ? null : LoadIcon(a.IconPath);
            UIElement iconEl;
            if (icon != null)
            {
                Image im = new Image { Source = icon, Width = 86, Height = 86, Stretch = Stretch.Uniform, Opacity = a.Unlocked ? 1.0 : 0.38 };
                RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.HighQuality);
                iconEl = im;
            }
            else
            {
                Shapes.Path p = MakeIcon(secret ? "lock" : "trophy");
                p.Stroke = sel ? SelTextBrush : MutedBrush;
                iconEl = new Border
                {
                    Width = 86,
                    Height = 86,
                    CornerRadius = new CornerRadius(14),
                    Background = sel ? BadgeSelBrush : BadgeBrush,
                    Child = new Viewbox
                    {
                        Width = 40,
                        Height = 40,
                        Child = new Canvas { Width = 24, Height = 24, Children = { p } }
                    }
                };
            }
            Grid.SetColumn(iconEl, 0);
            g.Children.Add(iconEl);

            StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 16, 0) };
            text.Children.Add(new TextBlock
            {
                Text = secret ? Loc.T("Logro secreto") : a.Name,
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = main,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            string desc = secret ? Loc.T("Sigue jugando para descubrirlo.") : a.Description;
            if (!string.IsNullOrWhiteSpace(desc))
            {
                text.Children.Add(new TextBlock
                {
                    Text = desc,
                    FontSize = 18,
                    Foreground = muted,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 50,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }
            string extra = null;
            if (a.Unlocked)
            {
                extra = a.UnlockTime.HasValue ? Loc.T("Conseguido el {0}", a.UnlockTime.Value.ToShortDateString()) : Loc.T("Conseguido");
            }
            else if (!secret && a.ProgressDenom.HasValue && a.ProgressDenom.Value > 0)
            {
                extra = Loc.T("Progreso {0} / {1}", a.ProgressNum ?? 0, a.ProgressDenom.Value);
            }
            if (extra != null)
            {
                text.Children.Add(new TextBlock { Text = extra, FontSize = 16, Foreground = muted, Margin = new Thickness(0, 2, 0, 0) });
            }
            Grid.SetColumn(text, 1);
            g.Children.Add(text);

            if (!secret && a.Percent.HasValue)
            {
                StackPanel rarity = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                bool rare = a.Percent.Value < 10;
                rarity.Children.Add(new TextBlock
                {
                    Text = a.Percent.Value.ToString("0.#") + " %",
                    FontSize = 26,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = rare ? (sel ? Make("#B26A00") : Make("#FFC857")) : main,
                    HorizontalAlignment = HorizontalAlignment.Right
                });
                rarity.Children.Add(new TextBlock
                {
                    Text = rare ? Loc.T("Raro") : Loc.T("de jugadores"),
                    FontSize = 15,
                    Foreground = muted,
                    HorizontalAlignment = HorizontalAlignment.Right
                });
                Grid.SetColumn(rarity, 2);
                g.Children.Add(rarity);
            }
            b.Child = g;
            return b;
        }

        private UIElement BuildRow(int index)
        {
            MenuEntry e = entries[index];
            Border b = new Border
            {
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(14, 10, 22, 10),
                Margin = new Thickness(0, 0, 0, 12),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)),
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = new TransformGroup { Children = { new ScaleTransform(1, 1), new TranslateTransform(0, 0) } }
            };
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Shapes.Path icon = MakeIcon(e.Glyph);
            Viewbox iconBox = new Viewbox
            {
                Width = 32,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Canvas { Width = 24, Height = 24, Children = { icon } }
            };
            Border badge = new Border
            {
                Width = 56,
                Height = 56,
                CornerRadius = new CornerRadius(16),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = iconBox
            };
            TextBlock label = new TextBlock
            {
                Text = e.Text,
                FontSize = 28,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(badge, 0);
            Grid.SetColumn(label, 1);
            g.Children.Add(badge);
            g.Children.Add(label);
            if (!string.IsNullOrEmpty(e.Right))
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                TextBlock right = new TextBlock
                {
                    Text = e.Right,
                    FontSize = 22,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(16, 0, 6, 0)
                };
                Grid.SetColumn(right, 2);
                g.Children.Add(right);
                rights.Add(right);
            }
            else
            {
                rights.Add(null);
            }
            // Aro blanco alrededor de la fila (por fuera de la tarjeta); se enciende con un destello que le da la vuelta entera.
            Border halo = new Border
            {
                CornerRadius = new CornerRadius(25),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Margin = new Thickness(-20, -16, -28, -16),
                IsHitTestVisible = false,
                Opacity = 0
            };
            Grid.SetColumnSpan(halo, 3);
            g.Children.Add(halo);
            g.Tag = halo;
            b.Child = g;

            int captured = index;
            b.MouseLeftButtonUp += (s, ev) =>
            {
                selected = captured;
                UpdateSelection();
                Accept();
            };
            b.MouseEnter += (s, ev) =>
            {
                selected = captured;
                UpdateSelection();
            };

            rows.Add(b);
            icons.Add(icon);
            labels.Add(label);
            badges.Add(badge);
            return b;
        }

        private static Shapes.Path MakeIcon(string key)
        {
            Shapes.Path p = new Shapes.Path
            {
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = Brushes.Transparent
            };
            string data;
            if (key != null && IconData.Paths.TryGetValue(key, out data))
            {
                try
                {
                    p.Data = Geometry.Parse(data);
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "Icono no válido: " + key);
                }
            }
            return p;
        }

        private UIElement Hint(string key, string text)
        {
            StackPanel p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 36, 0) };
            Border pill = new Border
            {
                Background = PillBrush,
                CornerRadius = new CornerRadius(18),
                MinWidth = 36,
                Height = 36,
                Padding = new Thickness(12, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            pill.Child = new TextBlock
            {
                Text = key,
                FontFamily = new FontFamily("Segoe UI Symbol"),
                FontSize = 18,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            p.Children.Add(pill);
            p.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 20,
                Foreground = MutedBrush,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            return p;
        }

        // Foco de la fila seleccionada, al estilo de los launchers de Android TV: la tarjeta se "eleva" con una sombra oscura y suave
        // debajo, un aro fino casi transparente la perfila y el resto de filas quedan algo atenuadas. Sin brillos ni destellos.
        // El efecto solo existe en la fila seleccionada y no hay animaciones continuas: en reposo no cuesta nada.
        private void SetRowGlow(Border row, bool on)
        {
            bool lit = row.Tag is bool && (bool)row.Tag;
            if (row.Tag == null && !on)
            {
                // Primera vez y sin foco: se deja atenuada de entrada, sin animar.
                row.Tag = false;
                if (row.Child != null)
                {
                    row.Child.Opacity = 0.76;
                }
                return;
            }
            if (on == lit)
            {
                return;
            }
            row.Tag = on;
            CubicEase ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            // Elevación: sombra oscura hacia abajo.
            System.Windows.Media.Effects.DropShadowEffect fx = row.Effect as System.Windows.Media.Effects.DropShadowEffect;
            if (on)
            {
                if (fx == null)
                {
                    fx = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = Colors.Black,
                        BlurRadius = 34,
                        ShadowDepth = 12,
                        Direction = 270,
                        Opacity = 0,
                        RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
                    };
                    row.Effect = fx;
                }
                fx.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
                    new DoubleAnimation(0.55, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            }
            else if (fx != null)
            {
                DoubleAnimation off = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
                off.Completed += (s2, e2) =>
                {
                    if (!(row.Tag is bool && (bool)row.Tag))
                    {
                        row.Effect = null;
                    }
                };
                fx.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, off);
            }

            // Aro blanco: al ganar el foco un destello le da la vuelta entera y se queda un aro fino; al perderlo se apaga.
            Grid gr = row.Child as Grid;
            Border halo = gr != null ? gr.Tag as Border : null;
            if (halo != null)
            {
                if (on)
                {
                    RotateTransform rot = new RotateTransform(0, 0.5, 0.5);
                    LinearGradientBrush sweep = new LinearGradientBrush
                    {
                        StartPoint = new Point(0, 0.5),
                        EndPoint = new Point(1, 0.5),
                        RelativeTransform = rot
                    };
                    Color faint = Color.FromArgb(55, 255, 255, 255);
                    sweep.GradientStops.Add(new GradientStop(faint, 0.0));
                    sweep.GradientStops.Add(new GradientStop(faint, 0.36));
                    sweep.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), 0.5));
                    sweep.GradientStops.Add(new GradientStop(faint, 0.64));
                    sweep.GradientStops.Add(new GradientStop(faint, 1.0));
                    halo.BorderBrush = sweep;
                    halo.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
                    DoubleAnimation turn = new DoubleAnimation(0, 180, TimeSpan.FromMilliseconds(1000))
                    {
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };
                    turn.Completed += (s2, e2) =>
                    {
                        if (ReferenceEquals(halo.BorderBrush, sweep))
                        {
                            halo.BorderBrush = new SolidColorBrush(Color.FromArgb(95, 255, 255, 255));
                        }
                    };
                    rot.BeginAnimation(RotateTransform.AngleProperty, turn);
                }
                else
                {
                    halo.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
                }
            }

            // Las filas sin foco se atenúan un poco (solo el contenido, para no interferir con la entrada escalonada).
            UIElement inner = row.Child;
            if (inner != null)
            {
                inner.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(on ? 1.0 : 0.76, TimeSpan.FromMilliseconds(on ? 200 : 160)) { EasingFunction = ease });
            }
        }

        // Foco de luz suave detrás de la lista que sigue a la selección.
        private Border spot;
        private RadialGradientBrush spotBrush;
        private bool spotInit;

        private void MoveSpot(bool animate)
        {
            try
            {
                if (spotBrush == null || selected < 0 || selected >= rows.Count)
                {
                    return;
                }
                Border r = rows[selected];
                if (r.ActualHeight <= 0 || !r.IsLoaded)
                {
                    return;
                }
                Point c = r.TransformToVisual(root).Transform(new Point(r.ActualWidth / 2, r.ActualHeight / 2));
                Point target = new Point(0.52, Math.Max(0.05, Math.Min(0.95, c.Y / 1080.0)));
                if (!animate)
                {
                    spotBrush.BeginAnimation(RadialGradientBrush.CenterProperty, null);
                    spotBrush.BeginAnimation(RadialGradientBrush.GradientOriginProperty, null);
                    spotBrush.Center = target;
                    spotBrush.GradientOrigin = target;
                    spotInit = true;
                    return;
                }
                PointAnimation pa = new PointAnimation(target, TimeSpan.FromMilliseconds(340))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                spotBrush.BeginAnimation(RadialGradientBrush.CenterProperty, pa);
                spotBrush.BeginAnimation(RadialGradientBrush.GradientOriginProperty, pa);
            }
            catch (Exception ex)
            {
                logger.Info("No se pudo mover el foco de luz: " + ex.Message);
            }
        }

        // Entrada escalonada de las filas al abrir el menú o cambiar de pantalla: aparecen y se deslizan una tras otra.
        private void AnimateRowsIn()
        {
            CubicEase ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            for (int i = 0; i < rows.Count; i++)
            {
                Border row = rows[i];
                TransformGroup tg = row.RenderTransform as TransformGroup;
                TranslateTransform tt = tg != null ? tg.Children[1] as TranslateTransform : null;
                TimeSpan delay = TimeSpan.FromMilliseconds(30 + 45 * i);
                row.Opacity = 0;
                row.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)) { BeginTime = delay, EasingFunction = ease });
                if (tt != null)
                {
                    tt.BeginAnimation(TranslateTransform.XProperty,
                        new DoubleAnimation(-36, 0, TimeSpan.FromMilliseconds(340)) { BeginTime = delay, EasingFunction = ease });
                }
            }
        }

        // Brillo que recorre las letras de la fila seleccionada (como el de Claude mientras trabaja): una pasada suave de izquierda a
        // derecha cada vez que cambia la selección. Solo anima un texto durante menos de un segundo; después no cuesta nada.
        private int shimmerSel = -1;

        private void StartShimmer(TextBlock label)
        {
            try
            {
                Color baseC = Color.FromRgb(0x0D, 0x0F, 0x14);
                Color hl = accent != null ? Palette.Mix(accent.Value, Colors.Black, 0.30) : Color.FromRgb(0x6A, 0x78, 0xB0);
                TranslateTransform tt = new TranslateTransform(-1, 0);
                LinearGradientBrush br = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 0),
                    RelativeTransform = tt
                };
                br.GradientStops.Add(new GradientStop(baseC, 0.0));
                br.GradientStops.Add(new GradientStop(baseC, 0.30));
                br.GradientStops.Add(new GradientStop(hl, 0.50));
                br.GradientStops.Add(new GradientStop(baseC, 0.70));
                br.GradientStops.Add(new GradientStop(baseC, 1.0));
                label.Foreground = br;
                DoubleAnimation sweep = new DoubleAnimation(-1, 1, TimeSpan.FromMilliseconds(950))
                {
                    BeginTime = TimeSpan.FromMilliseconds(60),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                sweep.Completed += (s2, e2) =>
                {
                    if (ReferenceEquals(label.Foreground, br))
                    {
                        label.Foreground = SelTextBrush;
                    }
                };
                tt.BeginAnimation(TranslateTransform.XProperty, sweep);
            }
            catch
            {
                label.Foreground = SelTextBrush;
            }
        }

        private void UpdateSelection()
        {
            if (achMode)
            {
                if (spot != null)
                {
                    spot.Opacity = 0;
                }
                RenderAchWindow();
                return;
            }
            if (galMode)
            {
                if (spot != null)
                {
                    spot.Opacity = 0;
                }
                RenderGallery();
                return;
            }
            if (spot != null)
            {
                spot.Opacity = 1;
                Dispatcher.BeginInvoke(new Action(() => MoveSpot(spotInit)), DispatcherPriority.Loaded);
            }
            for (int i = 0; i < rows.Count; i++)
            {
                bool sel = i == selected;
                SetRowGlow(rows[i], sel);
                rows[i].Background = sel ? SelBrush : RowBrush;
                badges[i].Background = sel ? BadgeSelBrush : BadgeBrush;
                icons[i].Stroke = sel ? SelTextBrush : (entries[i].Danger ? DangerBrush : TextBrush);
                if (sel)
                {
                    if (shimmerSel != i)
                    {
                        shimmerSel = i;
                        StartShimmer(labels[i]);
                    }
                }
                else
                {
                    labels[i].Foreground = TextBrush;
                }
                if (rights[i] != null)
                {
                    rights[i].Foreground = sel ? SelTextBrush : MutedBrush;
                }

                TransformGroup tg = rows[i].RenderTransform as TransformGroup;
                ScaleTransform st = tg != null ? tg.Children[0] as ScaleTransform : null;
                if (st != null)
                {
                    double target = sel ? 1.045 : 1.0;
                    DoubleAnimation anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(200))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
                }
            }
        }

        private void RefreshTexts()
        {
            if (clockText != null)
            {
                clockText.Text = DateTime.Now.ToShortTimeString();
            }
            UpdateClipClock();
            Func<List<BatteryInfo>> bp = BatteryProvider;
            if (bp != null && !batteryBusy && refreshTicks++ % 3 == 0)
            {
                // La lectura (HID/SDL) se hace fuera del hilo de la interfaz para no bloquear el menú.
                batteryBusy = true;
                System.Threading.Tasks.Task.Run(() =>
                {
                    List<BatteryInfo> r = null;
                    try
                    {
                        r = bp();
                    }
                    catch
                    {
                    }
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            batteryBusy = false;
                            ShowBattery(r);
                        }));
                    }
                    catch
                    {
                        batteryBusy = false;
                    }
                });
            }
            if (sessionLine != null && sessionProvider != null)
            {
                try
                {
                    sessionLine.Text = sessionProvider();
                }
                catch
                {
                }
            }
        }

        // Llamado desde el plugin con el nombre del botón del mando (DPadUp, A, B...).
        public void HandleButton(string name)
        {
            switch (name)
            {
                case "DPadUp":
                case "LeftStickUp":
                    Act("mando", "up");
                    break;
                case "DPadDown":
                case "LeftStickDown":
                    Act("mando", "down");
                    break;
                case "DPadLeft":
                case "LeftStickLeft":
                    Act("mando", "left");
                    break;
                case "DPadRight":
                case "LeftStickRight":
                    Act("mando", "right");
                    break;
                case "A":
                    Act("mando", "accept");
                    break;
                case "B":
                    Act("mando", "cancel");
                    break;
            }
        }

        // Todas las entradas (mando, teclado) pasan por aquí. Se ignoran las que llegan
        // justo al abrir la ventana y las repetidas casi a la vez (la misma pulsación
        // puede llegar por dos caminos).
        private void Act(string source, string action)
        {
            if (done)
            {
                return;
            }
            DateTime now = DateTime.UtcNow;
            if ((now - shownAt).TotalMilliseconds < 350 ||
                (action == lastAction && (now - lastAt).TotalMilliseconds < 150))
            {
                logger.Info("Menú: " + action + " (" + source + ") ignorado");
                return;
            }
            lastAction = action;
            lastAt = now;
            logger.Info("Menú: " + action + " (" + source + ")");
            switch (action)
            {
                case "up":
                    if (galMode) { if (viewing) ViewStep(-1); else MoveGrid(-GalCols); } else { Move(-1); }
                    break;
                case "down":
                    if (galMode) { if (viewing) ViewStep(1); else MoveGrid(GalCols); } else { Move(1); }
                    break;
                case "left":
                    if (galMode) { if (viewing) ViewSeek(-1); else MoveGrid(-1); }
                    break;
                case "right":
                    if (galMode) { if (viewing) ViewSeek(1); else MoveGrid(1); }
                    break;
                case "accept":
                    if (galMode)
                    {
                        if (viewing) ViewAccept(); else OpenViewer();
                    }
                    else
                    {
                        Accept();
                    }
                    break;
                case "cancel":
                    if (galMode && viewing)
                    {
                        CloseViewer();
                        UpdateSelection();
                    }
                    else
                    {
                        Cancel();
                    }
                    break;
            }
        }

        private void Move(int delta)
        {
            selected = (selected + delta + entries.Count) % entries.Count;
            UpdateSelection();
        }

        public void Accept()
        {
            if (achMode)
            {
                return;
            }
            Choose(selected);
        }

        public void Cancel()
        {
            Choose(cancelIndex);
        }

        private void Choose(int index)
        {
            Action<int> h = OnChosen;
            if (done || h == null)
            {
                return;
            }
            done = true;
            Result = index;
            h(index);
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Up:
                    Act("teclado", "up");
                    e.Handled = true;
                    break;
                case Key.Down:
                    Act("teclado", "down");
                    e.Handled = true;
                    break;
                case Key.Left:
                    Act("teclado", "left");
                    e.Handled = true;
                    break;
                case Key.Right:
                    Act("teclado", "right");
                    e.Handled = true;
                    break;
                case Key.Enter:
                case Key.Space:
                    Act("teclado", "accept");
                    e.Handled = true;
                    break;
                case Key.Escape:
                case Key.Back:
                    Act("teclado", "cancel");
                    e.Handled = true;
                    break;
            }
        }

        private void OnRendered(object sender, EventArgs e)
        {
            Reveal();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    UpdatePip();
                    IntPtr h = new WindowInteropHelper(this).Handle;
                    if (NoFocus)
                    {
                        // Sin pedir el foco (Windows lo rechazaría): solo se asegura de que quede encima.
                        if (h != IntPtr.Zero)
                        {
                            NativeMethods.RaiseTopmostNoActivate(h);
                        }
                        return;
                    }
                    Activate();
                    Focus();
                    if (h != IntPtr.Zero)
                    {
                        NativeMethods.BringToFront(h, true);
                    }
                }
                catch
                {
                }
            }), DispatcherPriority.Background);
        }
    }
}
