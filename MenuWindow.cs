using System;
using System.Collections.Generic;
using System.Linq;
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
        public string Desc;
        // Fila de música que solo abre un reproductor (no hay nada sonando): sin tarjeta ni control de pista.
        public bool OpenOnly;
        // Fila de música de la pantalla principal (la que cambia en el sitio entre «Abrir música» y «Música»).
        public bool MainMusic;
        // Icono propio de una aplicación y su color (filas de reproductores).
        public ImageSource Image;
        public Color? AccentColor;
        // «Logros» con el platino (o todos los trofeos): trofeo de platino grande en la insignia.
        public bool Platinum;
        public Brush PlateBrush;

        public MenuEntry(string glyph, string text)
        {
            Glyph = glyph;
            Text = text;
        }
    }

    // Un juego reciente del panel «Cambiar a».
    internal sealed class MenuRecent
    {
        public string Id;
        public string Name;
        public string When;
        public ImageSource Image;
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

        private static readonly Brush AppBadgeBrush = Make("#F01A1F2B");
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
            // Cristal: degradado vertical (más claro arriba) teñido con el acento. Es un pincel, no un efecto: no cuesta nada al dibujar.
            Color gTop = Palette.Mix(Colors.White, a, 0.55 * k + 0.0);
            Color gMid = Palette.Mix(Color.FromRgb(0x12, 0x15, 0x1D), a, 0.30 * k);
            Color gBot = Palette.Mix(Color.FromRgb(0x12, 0x15, 0x1D), a, 0.45 * k);
            LinearGradientBrush glass = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            glass.GradientStops.Add(new GradientStop(Color.FromArgb(70, gTop.R, gTop.G, gTop.B), 0.0));
            glass.GradientStops.Add(new GradientStop(Color.FromArgb(120, gMid.R, gMid.G, gMid.B), 0.55));
            glass.GradientStops.Add(new GradientStop(Color.FromArgb(140, gBot.R, gBot.G, gBot.B), 1.0));
            glass.Freeze();
            RowBrush = glass;
            PillBrush = Frozen(Palette.Mix(Color.FromRgb(0x23, 0x28, 0x38), a, 0.22 * k));
            BadgeBrush = Frozen(Palette.Mix(Color.FromRgb(0x26, 0x2C, 0x3D), a, 0.34 * k));
            BadgeSelBrush = Frozen(Palette.Mix(Color.FromRgb(0xE3, 0xE6, 0xF0), a, 0.12 * k));
            SelBrush = Frozen(Palette.Mix(Color.FromRgb(0xF7, 0xF8, 0xFB), a, 0.03 * k));
        }

        // Borde de cristal: brillante arriba y casi invisible abajo.
        private static readonly Brush GlassEdge = MakeEdge();
        private static Brush MakeEdge()
        {
            LinearGradientBrush e = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            e.GradientStops.Add(new GradientStop(Color.FromArgb(120, 255, 255, 255), 0.0));
            e.GradientStops.Add(new GradientStop(Color.FromArgb(40, 255, 255, 255), 0.5));
            e.GradientStops.Add(new GradientStop(Color.FromArgb(18, 255, 255, 255), 1.0));
            e.Freeze();
            return e;
        }

        // Degradados de una opción con color (se calculan una vez por color).
        private static readonly Dictionary<Color, Brush> badgeGrad = new Dictionary<Color, Brush>();
        private static readonly Dictionary<Color, Brush> selGrad = new Dictionary<Color, Brush>();

        private static Color SolidColorOf(Brush b)
        {
            SolidColorBrush sb = b as SolidColorBrush;
            return sb != null ? sb.Color : Colors.Gray;
        }

        // Recuadro del icono: claro arriba, color en medio y más oscuro abajo.
        private static Brush BadgeGradient(Brush solid)
        {
            Color c = SolidColorOf(solid);
            Brush r;
            if (!badgeGrad.TryGetValue(c, out r))
            {
                LinearGradientBrush g = new LinearGradientBrush { StartPoint = new Point(0.2, 0), EndPoint = new Point(0.8, 1) };
                g.GradientStops.Add(new GradientStop(Palette.Mix(c, Colors.White, 0.32), 0.0));
                g.GradientStops.Add(new GradientStop(c, 0.55));
                g.GradientStops.Add(new GradientStop(Palette.Mix(c, Colors.Black, 0.22), 1.0));
                g.Freeze();
                r = g;
                badgeGrad[c] = r;
            }
            return r;
        }

        // Fila seleccionada con color: degradado del color de la acción, lo bastante oscuro para leer texto blanco.
        private static Brush SelGradient(Brush solid)
        {
            Color c = SolidColorOf(solid);
            Brush r;
            if (!selGrad.TryGetValue(c, out r))
            {
                LinearGradientBrush g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                g.GradientStops.Add(new GradientStop(Palette.Mix(c, Colors.Black, 0.28), 0.0));
                g.GradientStops.Add(new GradientStop(Palette.Mix(c, Color.FromRgb(0x14, 0x17, 0x22), 0.62), 1.0));
                g.Freeze();
                r = g;
                selGrad[c] = r;
            }
            return r;
        }

        private static readonly Brush SelDescBrush = Make("#E9EEFB");
        private static readonly Brush BarWhite = Make("#F2FFFFFF");

        private List<MenuEntry> entries;
        private int cancelIndex;
        private Func<string> sessionProvider;
        private readonly Viewbox box;
        private readonly string buttonStyle;
        private readonly bool guideEnabled;
        private readonly List<Border> rows = new List<Border>();
        private readonly List<Shapes.Path> icons = new List<Shapes.Path>();
        private readonly List<TextBlock> labels = new List<TextBlock>();
        private readonly List<TextBlock> descs = new List<TextBlock>();
        private readonly List<Border> accents = new List<Border>();
        private List<AchievementItem> achAll;
        private int achFilter;
        private readonly List<UIElement> achCache = new List<UIElement>();
        private int achCacheStart = -1;
        private int achCacheEnd = -1;
        private int achCacheSel = -1;
        private TextBlock achTabs;
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
        public static List<MenuRecent> NextRecents;   // lo lee el constructor antes de construir la primera pantalla
        public List<MenuRecent> Recents;     // juegos recientes para «Cambiar a» (null = sin panel)
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
        private ClipPlayer viewMedia;
        private TextBlock viewCaption;
        private TextBlock viewHint;
        private Border viewProgressFill;
        private StackPanel viewNoAudio;
        private static readonly Dictionary<string, string> durCache = new Dictionary<string, string>();
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
            Recents = NextRecents;
            NextRecents = null;
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
                try
                {
                    MusicWatcher.Instance.Changed -= OnMusicChanged;
                }
                catch
                {
                }
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
            UiFeedback.Play(UiFeedback.Kind.Open);
            if (!UiFeedback.Animations)
            {
                root.Opacity = 1;
                return;
            }
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
                if (!UiFeedback.Animations)
                {
                    Close();
                    return;
                }
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
            descs.Clear();
            accents.Clear();
            icons.Clear();
            labels.Clear();
            badges.Clear();
            rights.Clear();
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

        // Color de cada mando según su posición (como los LED de jugador de una consola): 1 azul, 2 rojo, 3 verde, 4 amarillo.
        private static readonly string[] PadColors = { "5AA2FF", "FF6B6B", "4FDC95", "FFC857" };

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
                };
                string key = b.Wired ? "battery-charging" : (b.Level >= 3 ? "battery-full" : (b.Level == 2 ? "battery-medium" : (b.Level == 1 ? "battery-low" : "battery")));
                Brush color = b.Wired ? OkBrush : (b.Level <= 1 ? DangerBrush : (b.Level == 2 ? MidBrush : OkBrush));
                // Con varios mandos, cada uno lleva su color (mando y número) y una pastilla suave de ese color;
                // la batería y el porcentaje mantienen el color del nivel.
                string padHex = PadColors[Math.Max(0, b.Index - 1) % PadColors.Length];
                Brush padBrush = list.Count > 1 ? Make("#" + padHex) : color;
                one.Children.Add(SmallIcon("gamepad-2", padBrush, 34));
                if (list.Count > 1)
                {
                    one.Children.Add(new TextBlock
                    {
                        Text = b.Index.ToString(),
                        FontSize = 24,
                        FontWeight = FontWeights.Bold,
                        Foreground = padBrush,
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
                if (list.Count > 1)
                {
                    Border pill = new Border
                    {
                        Background = Make("#22" + padHex),
                        BorderBrush = Make("#66" + padHex),
                        BorderThickness = new Thickness(2),
                        CornerRadius = new CornerRadius(30),
                        Padding = new Thickness(12, 4, 18, 4),
                        Margin = new Thickness(i == 0 ? 0 : 20, 0, 0, 0),
                        Child = one
                    };
                    batteryHost.Children.Add(pill);
                }
                else
                {
                    batteryHost.Children.Add(one);
                }
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
            // Con muchas filas (música, administrador...) la lista no cabe en 810 px: se reduce lo justo para que se vean todas enteras.
            double need = entries.Count * 92 + 40;
            if (need > 800)
            {
                double k = Math.Max(0.7, 800 / need);
                right.LayoutTransform = new ScaleTransform(k, k);
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
                    BorderBrush = GlassEdge,
                    BorderThickness = new Thickness(1.5),
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

            // Tarjeta de música (debajo de la miniatura): solo si el menú trae la fila «Música».
            musicCard = null;
            if (hasGame && entries != null && entries.Any(x => x.Glyph == "music"))
            {
                UIElement mc = BuildMusicCard();
                Grid.SetRowSpan(mc, 3);
                canvas.Children.Add(mc);
                // Chuleta de botones bajo la tarjeta (en la fila no cabe todo el texto).
                TextBlock hint = new TextBlock
                {
                    Text = PadText(Loc.T("←/→ ±10 s · {L2}/{R2} volumen · {Y} aleatorio · {R3} repetir")),
                    FontSize = 15,
                    Foreground = MutedBrush,
                    Width = PipW,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(PipX, MusicY + MusicH + 10, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    IsHitTestVisible = false,
                    Visibility = musicCard.Visibility
                };
                Grid.SetRowSpan(hint, 3);
                canvas.Children.Add(hint);
                musicCard.IsVisibleChanged += (s3, e3) => hint.Visibility = musicCard.Visibility;
                UpdateMusic();
            }

            // Panel «Cambiar a»: ocupa el hueco de la tarjeta de música cuando la selección está en «Finalizar juego».
            recPanel = null;
            recFocus = -1;
            if (hasGame && Recents != null && Recents.Count > 0 && entries != null && entries.Any(x => x.Glyph == "circle-x"))
            {
                Canvas rp = BuildRecentsPanel(cover);
                Grid.SetRowSpan(rp, 3);
                canvas.Children.Add(rp);
            }

            StackPanel hints = BuildHints(buttonStyle, guideEnabled, true);
            Grid.SetRow(hints, 2);
            canvas.Children.Add(hints);

            return canvas;
        }

        // ---------- Panel «Cambiar a» ----------

        private Canvas recPanel;
        private int recFocus = -1;
        private List<Border> recTiles;
        private List<ScaleTransform> recScales;
        private List<bool> recOn;
        private TextBlock recName, recWhen;

        private Canvas BuildRecentsPanel(ImageSource cover)
        {
            bool portrait = cover != null && cover.Height > cover.Width * 1.25;
            double tw = 100, th = portrait ? 150 : 100, gap = 28, top = 56;
            recTiles = new List<Border>();
            recScales = new List<ScaleTransform>();
            recOn = new List<bool>();
            Canvas c = new Canvas
            {
                Width = PipW,
                Height = 330,
                Margin = new Thickness(PipX, MusicY - 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed
            };
            TextBlock head = new TextBlock { Text = Loc.T("Cambiar a"), FontSize = 22, Foreground = MutedBrush };
            Canvas.SetLeft(head, 6);
            Canvas.SetTop(head, 0);
            c.Children.Add(head);
            for (int i = 0; i < Recents.Count && i < 3; i++)
            {
                MenuRecent r = Recents[i];
                ScaleTransform sc = new ScaleTransform(1, 1);
                Border tile = new Border
                {
                    Width = tw,
                    Height = th,
                    CornerRadius = new CornerRadius(14),
                    BorderThickness = new Thickness(3),
                    BorderBrush = Brushes.Transparent,
                    Opacity = 0.78,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = sc
                };
                if (r.Image != null)
                {
                    ImageBrush ib = new ImageBrush(r.Image) { Stretch = Stretch.UniformToFill };
                    ib.Freeze();
                    tile.Background = ib;
                }
                else
                {
                    tile.Background = BadgeBrush;
                    tile.Child = new TextBlock
                    {
                        Text = r.Name,
                        Foreground = TextBrush,
                        FontSize = 16,
                        FontWeight = FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(6)
                    };
                }
                Canvas.SetLeft(tile, 12 + i * (tw + gap));
                Canvas.SetTop(tile, top);
                c.Children.Add(tile);
                recTiles.Add(tile);
                recScales.Add(sc);
                recOn.Add(false);
            }
            recName = new TextBlock
            {
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Width = PipW - 12
            };
            Canvas.SetLeft(recName, 6);
            Canvas.SetTop(recName, top + th + 34);
            c.Children.Add(recName);
            recWhen = new TextBlock { FontSize = 18, Foreground = MutedBrush, Width = PipW - 12 };
            Canvas.SetLeft(recWhen, 6);
            Canvas.SetTop(recWhen, top + th + 68);
            c.Children.Add(recWhen);
            recPanel = c;
            return c;
        }

        private bool IsEndRow()
        {
            return !achMode && !galMode && entries != null && selected >= 0 && selected < entries.Count && entries[selected].Glyph == "circle-x";
        }

        // Muestra u oculta el panel y mueve el foco entre pósters (solo escala y opacidad: barato y fluido).
        private void UpdateRecents()
        {
            if (recPanel == null)
            {
                return;
            }
            bool show = IsEndRow();
            if (!show)
            {
                recFocus = -1;
            }
            recPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (musicCard != null)
            {
                musicCard.Visibility = show ? Visibility.Collapsed : (MusicWatcher.Instance.Current.HasSession ? Visibility.Visible : Visibility.Collapsed);
            }
            double big = recTiles.Count > 0 && recTiles[0].Height > 120 ? 1.12 : 1.16;
            for (int i = 0; i < recTiles.Count; i++)
            {
                bool f = i == recFocus;
                if (recOn[i] == f)
                {
                    continue;
                }
                recOn[i] = f;
                recTiles[i].BorderBrush = f ? Brushes.White : Brushes.Transparent;
                double sTo = f ? big : 1.0;
                double oTo = f ? 1.0 : 0.78;
                if (UiFeedback.Animations)
                {
                    TimeSpan t = TimeSpan.FromMilliseconds(150);
                    CubicEase ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                    recScales[i].BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(sTo, t) { EasingFunction = ease });
                    recScales[i].BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(sTo, t) { EasingFunction = ease });
                    recTiles[i].BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(oTo, t) { EasingFunction = ease });
                }
                else
                {
                    recScales[i].BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    recScales[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    recTiles[i].BeginAnimation(UIElement.OpacityProperty, null);
                    recScales[i].ScaleX = sTo;
                    recScales[i].ScaleY = sTo;
                    recTiles[i].Opacity = oTo;
                }
            }
            if (recFocus >= 0 && recFocus < Recents.Count)
            {
                recName.Text = Recents[recFocus].Name;
                recName.Foreground = TextBrush;
                recWhen.Text = Recents[recFocus].When ?? "";
            }
            else
            {
                recName.Text = Loc.T("→ elegir otro juego");
                recName.Foreground = MutedBrush;
                recWhen.Text = "";
            }
        }

        // ---------- Tarjeta de música ----------

        private Border musicCard;
        private Border musicArt;
        private UIElement musicArtIcon;
        private StackPanel musicVol;
        private Shapes.Path musicArtPath, musicShufPath, musicRepPath;
        private UIElement musicShufBox, musicRepBox;
        private static readonly Geometry GeoRepeat = FrozenGeo("M7 7h10v3l4-4-4-4v3H5v6h2V7zm10 10H7v-3l-4 4 4 4v-3h12v-6h-2v4z");
        private static readonly Geometry GeoRepeatOne = FrozenGeo("M7 7h10v3l4-4-4-4v3H5v6h2V7zm10 10H7v-3l-4 4 4 4v-3h12v-6h-2v4zM13 15V9h-1l-2 1v1h1.5v4H13z");
        private Border musicVolFill;
        private Image musicMark;
        private TextBlock musicSrc, musicTitle, musicArtist, musicCur, musicDur;
        private Border musicFill;
        private Shapes.Path musicPlayIcon;
        private BitmapSource musicArtShown;
        private bool musicHooked;
        private const double MusicY = 690, MusicH = 172;
        private static readonly Brush MusicGreen = Make("#22C55E");
        private Brush musicAccent;
        private Color? musicAccentFor;

        // Color de la app de música para la tarjeta; si es gris, blanco o muy oscuro se usa el verde de siempre.
        private Brush MusicAccentBrush()
        {
            Color? c = null;
            if (entries != null)
            {
                foreach (MenuEntry me in entries)
                {
                    if (me.MainMusic && me.Image != null)
                    {
                        c = me.AccentColor;
                        break;
                    }
                }
            }
            if (!c.HasValue)
            {
                return MusicGreen;
            }
            if (musicAccent != null && musicAccentFor.HasValue && musicAccentFor.Value == c.Value)
            {
                return musicAccent;
            }
            double r = c.Value.R / 255.0, g = c.Value.G / 255.0, b = c.Value.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double sat = max <= 0 ? 0 : (max - min) / max;
            if (sat < 0.28)
            {
                return MusicGreen;
            }
            // Se aviva y se ajusta el brillo para que se lea sobre el cristal.
            double scale = 1.0;
            if (max < 0.62)
            {
                scale = 0.62 / Math.Max(max, 0.01);
            }
            else if (max > 0.95)
            {
                scale = 0.95 / max;
            }
            Func<double, byte> f = v => (byte)Math.Max(0, Math.Min(255, Math.Round(v * scale * 255)));
            SolidColorBrush br = new SolidColorBrush(Color.FromRgb(f(r), f(g), f(b)));
            br.Freeze();
            musicAccent = br;
            musicAccentFor = c;
            return br;
        }

        private void ApplyMusicAccent()
        {
            if (musicSrc == null)
            {
                return;
            }
            Brush a = MusicAccentBrush();
            musicSrc.Foreground = a;
            if (musicArtPath != null)
            {
                musicArtPath.Stroke = a;
            }
            if (musicVolFill != null)
            {
                musicVolFill.Background = a;
            }
            if (musicCard != null && IsMusicRow())
            {
                musicCard.BorderBrush = a;
            }
        }
        private static readonly Brush MusicTrack = Make("#38FFFFFF");
        private static readonly Geometry GeoPlay = FrozenGeo("M7 4v16l13-8z");
        private static readonly Geometry GeoPause = FrozenGeo("M6 5h4v14H6zM14 5h4v14h-4z");

        private static Geometry FrozenGeo(string data)
        {
            Geometry g = Geometry.Parse(data);
            g.Freeze();
            return g;
        }

        private static UIElement FilledIcon(string data, double size, out Shapes.Path path)
        {
            path = new Shapes.Path { Data = FrozenGeo(data), Fill = Brushes.White };
            return new Viewbox
            {
                Width = size,
                Height = size,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Canvas { Width = 24, Height = 24, Children = { path } }
            };
        }

        private UIElement BuildMusicCard()
        {
            if (!musicHooked)
            {
                MusicWatcher.Instance.Changed += OnMusicChanged;
                musicHooked = true;
            }
            musicCard = new Border
            {
                Width = PipW,
                Height = MusicH,
                Margin = new Thickness(PipX, MusicY, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(0),
                Background = RowBrush,
                BorderBrush = GlassEdge,
                BorderThickness = new Thickness(1.5),
                Visibility = Visibility.Collapsed
            };
            StackPanel col = new StackPanel { Margin = new Thickness(14) };

            Grid top = new Grid { Height = 84 };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            musicArt = new Border { Width = 84, Height = 84, CornerRadius = new CornerRadius(14), Background = BadgeBrush };
            musicArtPath = MakeIcon("music");
            musicArtPath.Stroke = MusicGreen;
            musicArtIcon = new Viewbox { Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Center, Child = new Canvas { Width = 24, Height = 24, Children = { musicArtPath } } };
            Grid artHost = new Grid { Width = 84, Height = 84 };
            artHost.Children.Add(musicArt);
            artHost.Children.Add(musicArtIcon);
            musicArtShown = null;
            Grid.SetColumn(artHost, 0);
            top.Children.Add(artHost);

            StackPanel info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            musicSrc = new TextBlock { FontSize = 15, Foreground = MusicGreen, FontWeight = FontWeights.SemiBold };
            musicTitle = new TextBlock { FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = TextBrush, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 5, 0, 0) };
            musicArtist = new TextBlock { FontSize = 17, Foreground = MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
            musicSrc.VerticalAlignment = VerticalAlignment.Center;
            Shapes.Path volPath;
            UIElement volGlyph = FilledIcon("M3 9v6h4l5 5V4L7 9H3zM16.5 12A4.5 4.5 0 0 0 14 8v8a4.5 4.5 0 0 0 2.5-4z", 18, out volPath);
            volPath.Fill = MutedBrush;
            musicVolFill = new Border { Height = 4, Width = 0, CornerRadius = new CornerRadius(2), Background = MusicGreen, HorizontalAlignment = HorizontalAlignment.Left };
            Grid volTrack = new Grid { Width = 64, Height = 4, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            volTrack.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = MusicTrack });
            volTrack.Children.Add(musicVolFill);
            musicVol = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            musicVol.Children.Add(volGlyph);
            musicVol.Children.Add(volTrack);
            Grid srcLine = new Grid();
            srcLine.Children.Add(musicSrc);
            srcLine.Children.Add(musicVol);
            info.Children.Add(srcLine);
            info.Children.Add(musicTitle);
            info.Children.Add(musicArtist);
            Grid.SetColumn(info, 1);
            top.Children.Add(info);
            col.Children.Add(top);

            Grid bar = new Grid { Height = 6, Margin = new Thickness(0, 12, 0, 0) };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = MusicTrack });
            musicFill = new Border { CornerRadius = new CornerRadius(3), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            bar.Children.Add(musicFill);
            col.Children.Add(bar);

            Grid ctl = new Grid { Height = 34, Margin = new Thickness(0, 6, 0, 0) };
            ctl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ctl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ctl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            musicCur = new TextBlock { FontSize = 15, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center };
            musicDur = new TextBlock { FontSize = 15, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Shapes.Path dummy;
            UIElement prev = FilledIcon("M6 6h2v12H6zM20 6v12L10 12z", 24, out dummy);
            UIElement play = FilledIcon("M7 4v16l13-8z", 28, out musicPlayIcon);
            UIElement next = FilledIcon("M16 6h2v12h-2zM4 6v12l10-6z", 24, out dummy);
            ((FrameworkElement)play).Margin = new Thickness(22, 0, 22, 0);
            musicShufBox = FilledIcon("M10.59 9.17L5.41 4 4 5.41l5.17 5.17 1.42-1.41zM14.5 4l2.04 2.04L4 18.59 5.41 20 17.96 7.46 20 9.5V4h-5.5zm.33 9.41l-1.41 1.41 3.13 3.13L14.5 20H20v-5.5l-2.04 2.04-3.13-3.13z", 20, out musicShufPath);
            musicRepBox = FilledIcon("M7 7h10v3l4-4-4-4v3H5v6h2V7zm10 10H7v-3l-4 4 4 4v-3h12v-6h-2v4z", 20, out musicRepPath);
            ((FrameworkElement)musicShufBox).Margin = new Thickness(0, 0, 22, 0);
            ((FrameworkElement)musicRepBox).Margin = new Thickness(22, 0, 0, 0);
            musicShufBox.Visibility = Visibility.Collapsed;
            musicRepBox.Visibility = Visibility.Collapsed;
            buttons.Children.Add(musicShufBox);
            buttons.Children.Add(prev);
            buttons.Children.Add(play);
            buttons.Children.Add(next);
            buttons.Children.Add(musicRepBox);
            Grid.SetColumn(musicCur, 0);
            Grid.SetColumn(buttons, 1);
            Grid.SetColumn(musicDur, 2);
            ctl.Children.Add(musicCur);
            ctl.Children.Add(buttons);
            ctl.Children.Add(musicDur);
            col.Children.Add(ctl);

            // Logo de la app como marca de agua difuminada en la esquina superior derecha.
            musicMark = new Image
            {
                Width = 150,
                Height = 150,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -34, -34, 0),
                Opacity = 0.30,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            LinearGradientBrush fade = new LinearGradientBrush(Color.FromArgb(255, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), new Point(1, 0), new Point(0.15, 0.95));
            fade.Freeze();
            musicMark.OpacityMask = fade;
            RenderOptions.SetBitmapScalingMode(musicMark, BitmapScalingMode.HighQuality);
            Grid cardRoot = new Grid { Clip = new RectangleGeometry(new Rect(0, 0, PipW - 3, MusicH - 3), 18.5, 18.5) };
            cardRoot.Children.Add(musicMark);
            cardRoot.Children.Add(col);
            musicCard.Child = cardRoot;
            return musicCard;
        }

        private void OnMusicChanged()
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(UpdateMusic));
            }
            catch
            {
            }
        }

        private static string FormatTime(double sec)
        {
            TimeSpan t = TimeSpan.FromSeconds(Math.Max(0, sec));
            return t.TotalHours >= 1
                ? ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00")
                : t.Minutes + ":" + t.Seconds.ToString("00");
        }

        private void UpdateMusic()
        {
            if (musicCard == null)
            {
                return;
            }
            MusicInfo m = MusicWatcher.Instance.Current;
            musicCard.Visibility = m.HasSession && MusicRowLive() && !(recPanel != null && recPanel.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
            if (!m.HasSession)
            {
                return;
            }
            musicSrc.Text = (m.Source ?? "").ToUpperInvariant();
            musicVol.Visibility = musicVolOn && musicVolume >= 0 ? Visibility.Visible : Visibility.Collapsed;
            if (musicVolume >= 0)
            {
                musicVolFill.Width = 64 * Math.Max(0, Math.Min(1, musicVolume));
            }
            ImageSource appImg = null;
            if (entries != null)
            {
                foreach (MenuEntry me in entries)
                {
                    if (me.MainMusic && me.Image != null)
                    {
                        appImg = me.Image;
                        break;
                    }
                }
            }
            if (appImg != null)
            {
                musicMark.Source = appImg;
                musicMark.Visibility = Visibility.Visible;
            }
            else
            {
                musicMark.Visibility = Visibility.Collapsed;
            }
            ApplyMusicAccent();
            musicTitle.Text = string.IsNullOrEmpty(m.Title) ? "-" : m.Title;
            musicArtist.Text = m.Artist ?? "";
            if (!ReferenceEquals(m.Art, musicArtShown))
            {
                musicArtShown = m.Art;
                if (m.Art != null)
                {
                    ImageBrush ib = new ImageBrush(m.Art) { Stretch = Stretch.UniformToFill };
                    ib.Freeze();
                    musicArt.Background = ib;
                    musicArtIcon.Visibility = Visibility.Collapsed;
                }
                else
                {
                    musicArt.Background = BadgeBrush;
                    musicArtIcon.Visibility = Visibility.Visible;
                }
            }
            musicPlayIcon.Data = m.Playing ? GeoPause : GeoPlay;
            Brush accentNow = MusicAccentBrush();
            musicShufBox.Visibility = m.CanShuffle ? Visibility.Visible : Visibility.Collapsed;
            musicShufPath.Fill = m.Shuffle ? accentNow : MutedBrush;
            musicRepBox.Visibility = m.CanRepeat ? Visibility.Visible : Visibility.Collapsed;
            musicRepPath.Data = m.Repeat == 2 ? GeoRepeatOne : GeoRepeat;
            musicRepPath.Fill = m.Repeat > 0 ? accentNow : MutedBrush;
            UpdateMusicProgress();
        }

        // Una vez por segundo (junto al reloj): la posición se calcula sola, sin preguntar al reproductor.
        private void UpdateMusicProgress()
        {
            if (musicCard == null || musicCard.Visibility != Visibility.Visible)
            {
                return;
            }
            MusicInfo m = MusicWatcher.Instance.Current;
            double pos = m.PositionNow();
            musicCur.Text = m.DurationSec > 0 ? FormatTime(pos) : "";
            musicDur.Text = m.DurationSec > 0 ? FormatTime(m.DurationSec) : "";
            musicFill.Width = m.DurationSec > 0 ? Math.Max(0, Math.Min(1, pos / m.DurationSec)) * (PipW - 28) : 0;
        }

        private bool MusicRowLive()
        {
            return entries != null && entries.Any(x => x.Glyph == "music" && !x.OpenOnly);
        }

        // Cambia la fila de música entre «Abrir música» y «Música» sin reconstruir el menú (sin parpadeo ni salto del cursor).
        // Cambia el icono de la fila de música en el sitio (img null = vuelve a la nota).
        private void SetRowImage(int i, ImageSource img, Color? accent)
        {
            try
            {
                if (i >= badges.Count || !(badges[i].Child is Viewbox))
                {
                    return;
                }
                Viewbox vb = (Viewbox)badges[i].Child;
                entries[i].Image = img;
                entries[i].AccentColor = accent;
                if (img == null)
                {
                    // Nota de siempre: se crea de nuevo (reutilizar el elemento anterior da error de «ya tiene padre»).
                    Shapes.Path np = MakeIcon(entries[i].Glyph);
                    vb.Child = null;
                    vb.Width = 32;
                    vb.Height = 32;
                    vb.Child = new Canvas { Width = 24, Height = 24, Children = { np } };
                    if (i < icons.Count)
                    {
                        icons[i] = np;
                    }
                    entries[i].PlateBrush = null;
                    badges[i].Clip = null;
                    badges[i].Tag = ActionColor(entries[i].Glyph);
                    return;
                }
                if (!AppIcon.IsFullBleed(img))
                {
                    Color? pc = AppIcon.PlateColor(img);
                    SolidColorBrush pb = pc.HasValue ? new SolidColorBrush(pc.Value) : new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x2B));
                    pb.Freeze();
                    entries[i].PlateBrush = pb;
                }
                else
                {
                    entries[i].PlateBrush = Brushes.Transparent;
                }
                Image im = new Image { Source = img, Width = 56, Height = 56, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.HighQuality);
                vb.Child = null;
                vb.Width = 56;
                vb.Height = 56;
                vb.Child = im;
                badges[i].Clip = new RectangleGeometry(new Rect(0, 0, 56, 56), 16, 16);
                if (accent.HasValue)
                {
                    SolidColorBrush ab = new SolidColorBrush(accent.Value);
                    ab.Freeze();
                    badges[i].Tag = ab;
                }
            }
            catch
            {
            }
        }

        public void SetMusicRow(bool live, string label, string desc, string right, ImageSource img = null, Color? accent = null, bool setImage = false)
        {
            if (entries == null || achMode || galMode)
            {
                return;
            }
            for (int i = 0; i < entries.Count; i++)
            {
                if (!entries[i].MainMusic)
                {
                    continue;
                }
                entries[i].OpenOnly = !live;
                entries[i].Text = label;
                entries[i].Desc = desc;
                entries[i].Right = right;
                if (setImage)
                {
                    SetRowImage(i, img, accent);
                }
                if (i < labels.Count)
                {
                    labels[i].Text = label;
                }
                if (i < descs.Count && descs[i] != null)
                {
                    descs[i].Text = PadText(desc ?? "");
                }
                if (i < rights.Count && rights[i] != null)
                {
                    rights[i].Text = right ?? "";
                }
                break;
            }
            UpdateMusic();
            UpdateSelection();
        }

        // Acciones de música que resuelve el plugin sin cerrar el menú.
        public Func<bool> OpenMusicInline;
        // Sube o baja el volumen del reproductor (±0,05); el plugin responde con ShowMusicVolume.
        public Action<float> MusicVolumeChange;
        private float musicVolume = -1;
        private bool musicVolOn, musicVolFlash;
        private System.Windows.Threading.DispatcherTimer musicVolTimer;

        public void ShowMusicVolume(float v)
        {
            if (v < 0)
            {
                return;
            }
            musicVolume = v;
            if (musicVolFlash)
            {
                // Solo se muestra cuando el usuario lo toca, y se oculta solo a los pocos segundos.
                musicVolFlash = false;
                musicVolOn = true;
                if (musicVolTimer == null)
                {
                    musicVolTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                    musicVolTimer.Tick += (s2, e2) =>
                    {
                        musicVolTimer.Stop();
                        musicVolOn = false;
                        UpdateMusic();
                    };
                }
                musicVolTimer.Stop();
                musicVolTimer.Start();
            }
            UpdateMusic();
        }
        public Action CloseMusicInline;

        private bool IsMusicRow()
        {
            return !achMode && !galMode && entries != null && selected >= 0 && selected < entries.Count && entries[selected].Glyph == "music" && !entries[selected].OpenOnly;
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
            // Conseguidos primero (los más recientes arriba) y después los pendientes, como en las listas de trofeos.
            list = list.OrderByDescending(a => a.Unlocked).ThenByDescending(a => a.UnlockTime ?? DateTime.MinValue).ToList();
            ach = list;
            achAll = list;
            achFilter = 0;
            achCache.Clear();
            achCacheStart = -1;
            achCacheEnd = -1;
            achCacheSel = -1;
            entries = new List<MenuEntry>();
            for (int i = 0; i < list.Count; i++)
            {
                entries.Add(new MenuEntry(null, null));
            }
            cancelIndex = 0;
            rows.Clear();
            descs.Clear();
            accents.Clear();
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

            // Carátula, título, resumen y barra comparten el mismo eje: todo centrado en una columna de 360.
            StackPanel left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
            if (cover != null)
            {
                Image img = new Image { Source = cover, Stretch = Stretch.Uniform, MaxWidth = 360, MaxHeight = 440, HorizontalAlignment = HorizontalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                left.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(14),
                    HorizontalAlignment = HorizontalAlignment.Center,
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
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 22, 0, 8)
            });
            left.Children.Add(new TextBlock
            {
                Text = summary,
                FontSize = 22,
                Foreground = TextBrush,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 12)
            });
            Grid track = new Grid { Width = 360, Height = 10, HorizontalAlignment = HorizontalAlignment.Center };
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
            Grid.SetRow(achPanel, 1);
            achTabs = new TextBlock
            {
                FontSize = 24,
                Margin = new Thickness(4, 0, 0, 10),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            Grid achHead = new Grid();
            achHead.Children.Add(achTabs);
            // Trofeos conseguidos por tipo (platino, oro, plata, bronce).
            int[] got = new int[4];
            foreach (AchievementItem x in achAll ?? new List<AchievementItem>())
            {
                if (x.Unlocked)
                {
                    got[TrophyTier(x)]++;
                }
            }
            StackPanel counts = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 120, 6)
            };
            for (int t = 3; t >= 0; t--)
            {
                counts.Children.Add(TrophyIcon(t, true, false, 30));
                counts.Children.Add(new TextBlock
                {
                    Text = got[t].ToString(),
                    FontSize = 22,
                    Foreground = TextBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 16, 0)
                });
            }
            achHead.Children.Add(counts);
            achHead.Children.Add(achPos);
            Grid.SetRow(achHead, 0);
            right.Children.Add(achHead);
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
            descs.Clear();
            accents.Clear();
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
            galPos = new TextBlock { FontSize = 22, Foreground = MutedBrush, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
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
            Grid bottom = new Grid();
            bottom.Children.Add(hints);
            bottom.Children.Add(galPos);
            Grid.SetRow(bottom, 2);
            canvas.Children.Add(bottom);
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

        private static string ClipDuration(string path)
        {
            string s;
            if (durCache.TryGetValue(path, out s))
            {
                return s;
            }
            TimeSpan? d = Mp4Info.Duration(path);
            if (d.HasValue)
            {
                s = d.Value.TotalHours >= 1 ? d.Value.ToString(@"h\:mm\:ss") : d.Value.ToString(@"m\:ss");
            }
            else
            {
                s = "";
            }
            durCache[path] = s;
            return s;
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
            if (item.IsVideo)
            {
                string dur = ClipDuration(item.Path);
                if (!string.IsNullOrEmpty(dur))
                {
                    g.Children.Add(new Border
                    {
                        Margin = new Thickness(0, 10, 12, 0),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Top,
                        CornerRadius = new CornerRadius(8),
                        Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
                        Padding = new Thickness(8, 2, 8, 3),
                        Child = new TextBlock { Text = dur, FontSize = 16, Foreground = Brushes.White }
                    });
                }
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

        // Sustituye {A} {X} {Y} {L1} {R1} {L2} {R2} {R3} por los nombres de botón del estilo elegido (Xbox, PlayStation o Nintendo).
        private string PadText(string t)
        {
            if (string.IsNullOrEmpty(t) || t.IndexOf('{') < 0)
            {
                return t;
            }
            string a = "A", x = "X", y = "Y", l1 = "LB", r1 = "RB", l2 = "LT", r2 = "RT", r3 = "RS";
            if (string.Equals(buttonStyle, "PlayStation", StringComparison.OrdinalIgnoreCase))
            {
                a = "✕"; x = "□"; y = "△"; l1 = "L1"; r1 = "R1"; l2 = "L2"; r2 = "R2"; r3 = "R3";
            }
            else if (string.Equals(buttonStyle, "Nintendo", StringComparison.OrdinalIgnoreCase))
            {
                a = "B"; x = "Y"; y = "X"; l1 = "L"; r1 = "R"; l2 = "ZL"; r2 = "ZR"; r3 = "R3";
            }
            return t.Replace("{A}", a).Replace("{X}", x).Replace("{Y}", y).Replace("{L1}", l1).Replace("{R1}", r1)
                    .Replace("{L2}", l2).Replace("{R2}", r2).Replace("{R3}", r3);
        }

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
            viewMedia = new ClipPlayer
            {
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
                FontSize = 24,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(80, 0, 80, 84)
            };
            viewHint = new TextBlock
            {
                FontSize = 22,
                Foreground = MutedBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(80, 0, 80, 40)
            };
            viewer.Children.Add(viewImg);
            viewer.Children.Add(viewMedia);
            viewer.Children.Add(viewCaption);
            viewer.Children.Add(viewHint);
            Grid track = new Grid { Width = 1760, Height = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 128) };
            track.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), CornerRadius = new CornerRadius(2) });
            viewProgressFill = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            track.Children.Add(viewProgressFill);
            viewer.Children.Add(track);
            viewNoAudio = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 16, 40, 0), Visibility = Visibility.Collapsed, ToolTip = Loc.T("Este clip no tiene pista de audio") };
            viewNoAudio.Children.Add(SmallIcon("volume-x", MutedBrush, 36));
            viewer.Children.Add(viewNoAudio);
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
            if (viewNoAudio != null)
            {
                viewNoAudio.Visibility = Visibility.Collapsed;
            }
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
                    // El audio del clip sale de mpv (otro proceso): Playnite se queda silenciado.
                    SetClipAudio(false);
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
                            // Sin pista de audio no hay nada que oír: no se quita el silencio de Playnite (su música).
                            SetClipAudio(false);
                            if (viewNoAudio != null)
                            {
                                viewNoAudio.Visibility = Visibility.Visible;
                            }
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
                viewCaption.Text = viewCaptionBase + "   ·   " + cur + " / " + tot;
                double total = viewMedia.NaturalDuration.HasTimeSpan ? viewMedia.NaturalDuration.TimeSpan.TotalSeconds : 0;
                double ratio = total > 0 ? Math.Max(0, Math.Min(1, viewMedia.Position.TotalSeconds / total)) : 0;
                viewProgressFill.Width = 1760 * ratio;
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
            int n = ach.Count;
            int start = Math.Max(0, Math.Min(selected - AchVisible / 2, n - AchVisible));
            int end = Math.Min(n, start + AchVisible);
            if (start != achCacheStart || end != achCacheEnd || achCache.Count != end - start)
            {
                // Cambia la ventana visible: se reconstruyen las filas (a 6 por página, es barato).
                achPanel.Children.Clear();
                achCache.Clear();
                for (int i = start; i < end; i++)
                {
                    UIElement r = BuildAchRow(ach[i], i == selected);
                    achCache.Add(r);
                    achPanel.Children.Add(r);
                }
                achCacheStart = start;
                achCacheEnd = end;
                achCacheSel = selected;
            }
            else if (achCacheSel != selected)
            {
                // Solo cambia la selección: se reconstruyen las dos filas afectadas, no las seis.
                int oldSlot = achCacheSel - start;
                int newSlot = selected - start;
                if (oldSlot >= 0 && oldSlot < achCache.Count)
                {
                    ReplaceAchRow(oldSlot, start + oldSlot, false);
                }
                if (newSlot >= 0 && newSlot < achCache.Count)
                {
                    ReplaceAchRow(newSlot, start + newSlot, true);
                }
                achCacheSel = selected;
            }
            if (achPos != null)
            {
                achPos.Text = n == 0 ? "0 / 0" : (selected + 1) + " / " + n;
            }
            RenderAchTabs();
        }

        private void ReplaceAchRow(int slot, int index, bool sel)
        {
            UIElement r = BuildAchRow(ach[index], sel);
            int pos = achPanel.Children.IndexOf(achCache[slot]);
            if (pos < 0)
            {
                return;
            }
            achPanel.Children.RemoveAt(pos);
            achPanel.Children.Insert(pos, r);
            achCache[slot] = r;
        }

        private void RenderAchTabs()
        {
            if (achTabs == null || achAll == null)
            {
                return;
            }
            int got = achAll.Count(a => a.Unlocked);
            string[] names =
            {
                Loc.T("Todos") + " " + achAll.Count,
                Loc.T("Conseguidos") + " " + got,
                Loc.T("Pendientes") + " " + (achAll.Count - got)
            };
            achTabs.Inlines.Clear();
            for (int i = 0; i < names.Length; i++)
            {
                if (i > 0)
                {
                    achTabs.Inlines.Add(new System.Windows.Documents.Run("      "));
                }
                bool on = i == achFilter;
                achTabs.Inlines.Add(new System.Windows.Documents.Run(names[i])
                {
                    Foreground = on ? Make("#7FB2FF") : MutedBrush,
                    FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal
                });
            }
        }

        // Izquierda / derecha en logros: cambia entre Todos, Conseguidos y Pendientes.
        private void CycleAchFilter(int delta)
        {
            achFilter = (achFilter + delta + 3) % 3;
            ApplyAchFilter();
        }

        private void ApplyAchFilter()
        {
            if (achAll == null)
            {
                return;
            }
            int f = achFilter;
            ach = f == 0 ? achAll : achAll.Where(a => f == 1 ? a.Unlocked : !a.Unlocked).ToList();
            entries = new List<MenuEntry>();
            for (int i = 0; i < ach.Count; i++)
            {
                entries.Add(new MenuEntry(null, null));
            }
            selected = 0;
            achCache.Clear();
            achCacheStart = -1;
            achCacheEnd = -1;
            achCacheSel = -1;
            if (achPanel != null)
            {
                achPanel.Children.Clear();
            }
            UpdateSelection();
        }

        // Convierte el icono a escala de grises (sin efectos de GPU, que aquí no están disponibles).
        private static ImageSource Grayscale(ImageSource src)
        {
            BitmapSource bs = src as BitmapSource;
            if (bs == null)
            {
                return src;
            }
            FormatConvertedBitmap g = new FormatConvertedBitmap(bs, PixelFormats.Gray8, null, 0);
            g.Freeze();
            return g;
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
            // Logro conseguido y seleccionado: fila blanca con tinte crema, borde dorado y una pasada de luz al llegar.
            bool gold = sel && a.Unlocked && !secret;
            Brush main = sel ? SelTextBrush : TextBrush;
            Brush muted = gold ? GoldMuted : (sel ? Make("#4A5168") : MutedBrush);

            Border b = new Border
            {
                CornerRadius = new CornerRadius(16),
                Padding = gold ? new Thickness(12) : new Thickness(14),
                BorderThickness = gold ? new Thickness(2) : new Thickness(0),
                BorderBrush = gold ? GoldEdge : null,
                Margin = new Thickness(0, 0, 0, 10),
                Background = gold ? GoldRow : (sel ? SelBrush : RowBrush),
                Height = 114
            };
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            ImageSource icon = secret ? null : LoadIcon(a.IconPath);
            UIElement iconEl;
            if (icon != null)
            {
                Image im = new Image { Source = icon, Width = 86, Height = 86, Stretch = Stretch.Uniform, Opacity = a.Unlocked ? 1.0 : 0.38 };
                if (!a.Unlocked)
                {
                    // Los iconos de logros pendientes vienen en color: se pasan a gris de verdad.
                    im.Source = Grayscale(icon);
                }
                RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.HighQuality);
                iconEl = im;
            }
            else
            {
                Shapes.Path p = MakeIcon(secret ? "lock" : "trophy");
                p.Stroke = gold ? Brushes.White : (sel ? SelTextBrush : MutedBrush);
                iconEl = new Border
                {
                    Width = 86,
                    Height = 86,
                    CornerRadius = new CornerRadius(14),
                    Background = gold ? GoldBadge : (sel ? BadgeSelBrush : BadgeBrush),
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
            if (extra != null)
            {
                text.Children.Add(new TextBlock
                {
                    Text = extra,
                    FontSize = 16,
                    Foreground = gold ? GoldText : muted,
                    FontWeight = gold ? FontWeights.SemiBold : FontWeights.Normal,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }
            Grid.SetColumn(text, 1);
            g.Children.Add(text);

            if (!secret && a.Percent.HasValue)
            {
                StackPanel rarity = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                bool rare = a.Percent.Value < 10;
                bool veryRare = a.Percent.Value < 5;
                rarity.Children.Add(new TextBlock
                {
                    Text = a.Percent.Value.ToString("0.#") + " %",
                    FontSize = 26,
                    FontWeight = FontWeights.SemiBold,
                    // Mismo color que el trofeo del logro (bronce, plata, oro o platino).
                    Foreground = Make(sel ? TierDark[TrophyTier(a)] : TierText[TrophyTier(a)]),
                    Opacity = a.Unlocked ? 1.0 : 0.85,
                    HorizontalAlignment = HorizontalAlignment.Right
                });
                rarity.Children.Add(new TextBlock
                {
                    Text = veryRare ? Loc.T("Muy raro") : (rare ? Loc.T("Raro") : Loc.T("de jugadores")),
                    FontSize = 15,
                    Foreground = muted,
                    HorizontalAlignment = HorizontalAlignment.Right
                });
                Grid.SetColumn(rarity, 2);
                g.Children.Add(rarity);
            }
            // Barra de progreso (logros por etapas, p. ej. 210 / 666) a la derecha, con el número debajo.
            if (!secret && !a.Unlocked && a.ProgressDenom.HasValue && a.ProgressDenom.Value > 0)
            {
                double frac = Math.Max(0, Math.Min(1, (double)(a.ProgressNum ?? 0) / a.ProgressDenom.Value));
                StackPanel prog = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 18, 0), Width = 220 };
                Grid bar = new Grid { Height = 9 };
                bar.Children.Add(new Border { Background = sel ? Make("#22000000") : Make("#33FFFFFF"), CornerRadius = new CornerRadius(4.5) });
                if (frac > 0)
                {
                    bar.Children.Add(new Border
                    {
                        Background = Make("#E6A82B"),
                        CornerRadius = new CornerRadius(4.5),
                        Width = Math.Max(9, 220 * frac),
                        HorizontalAlignment = HorizontalAlignment.Left
                    });
                }
                prog.Children.Add(bar);
                prog.Children.Add(new TextBlock
                {
                    Text = (a.ProgressNum ?? 0) + " / " + a.ProgressDenom.Value,
                    FontSize = 17,
                    Foreground = muted,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 5, 0, 0)
                });
                Grid.SetColumn(prog, 3);
                g.Children.Add(prog);
            }

            // Trofeo según lo raro que sea el logro: color si está conseguido, gris apagado si no.
            UIElement trophy = TrophyIcon(TrophyTier(a), a.Unlocked && !secret, sel, 46);
            trophy.Opacity = a.Unlocked || secret ? 1 : 0.4;
            Grid.SetColumn(trophy, 4);
            g.Children.Add(trophy);
            b.Child = g;
            return b;
        }

        // Tipo de trofeo: 0 bronce, 1 plata, 2 oro, 3 platino. Se usa el del plugin de logros; si no lo trae,
        // estos umbrales del % global de jugadores.
        // Mismos límites que usa el plugin de logros: ultra raro hasta 5 %, raro hasta 20 %, poco común hasta 50 %.
        private const double SilverBelow = 50, GoldBelow = 20, PlatinumBelow = 5;

        internal static int TrophyTier(AchievementItem a)
        {
            // El tipo de trofeo lo define el plugin de logros para cada juego; el % solo se usa si no hay dato.
            if (a.Trophy.HasValue)
            {
                return Math.Max(0, Math.Min(3, a.Trophy.Value));
            }
            if (!a.Percent.HasValue)
            {
                return 0;
            }
            double p = a.Percent.Value;
            return p <= PlatinumBelow ? 3 : (p <= GoldBelow ? 2 : (p <= SilverBelow ? 1 : 0));
        }

        private static readonly string[] TierColor = { "#D98A3D", "#C5CEDB", "#FFC83D", "#8FD3FF" };
        private static readonly string[] TierColorSel = { "#B8651B", "#8A97A8", "#D9A000", "#3B9BD6" };

        // Copa rellena con degradado del color del nivel (bronce, plata, oro, platino). Pinceles congelados, se crean una vez.
        private static readonly string[] TierLight = { "#E8A25F", "#D3DAE6", "#FFD95A", "#9ADCFF" };
        private static readonly string[] TierText = { "#F0A560", "#D3DAE6", "#FFD95A", "#7FD0FF" };
        private static readonly string[] TierDark = { "#B8651B", "#8A97A8", "#D9A000", "#3B9BD6" };
        private static readonly Brush[] TierFill = MakeTierFills();

        private static Brush[] MakeTierFills()
        {
            Brush[] r = new Brush[4];
            for (int i = 0; i < 4; i++)
            {
                r[i] = MakeVertical(TierLight[i], TierDark[i]);
            }
            return r;
        }

        private UIElement TrophyIcon(int tier, bool colored, bool sel, double size)
        {
            Brush fill = colored ? TierFill[tier] : (sel ? Make("#B3BAC8") : MutedBrush);
            Brush dark = colored ? Make(TierDark[tier]) : (sel ? Make("#8A93A6") : MutedBrush);
            Shapes.Path body = new Shapes.Path { Fill = fill, Data = Geometry.Parse("M14 6H34V18A10 10 0 0 1 14 18Z") };
            Shapes.Path handles = new Shapes.Path
            {
                Stroke = dark,
                StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M14 9H8V12A7 7 0 0 0 15 19M34 9H40V12A7 7 0 0 1 33 19")
            };
            Shapes.Path stem = new Shapes.Path { Fill = dark, Data = Geometry.Parse("M21.5 27H26.5V35H21.5Z") };
            Shapes.Path baseP = new Shapes.Path { Fill = fill, Data = Geometry.Parse("M17 35H31A2 2 0 0 1 33 37V39A2 2 0 0 1 31 41H17A2 2 0 0 1 15 39V37A2 2 0 0 1 17 35Z") };
            Shapes.Path shine = new Shapes.Path
            {
                Stroke = Brushes.White,
                Opacity = colored ? 0.55 : 0.3,
                StrokeThickness = 2.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M19 9V17")
            };
            return new Viewbox
            {
                Width = size,
                Height = size,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Canvas { Width = 48, Height = 48, Children = { handles, body, stem, baseP, shine } }
            };
        }

        // Pasos del dorado de los logros conseguidos (pinceles congelados, se crean una vez).
        private static readonly Brush GoldEdge = Make("#F0C24F");
        private static readonly Brush GoldText = Make("#9A6A0A");
        private static readonly Brush GoldMuted = Make("#6B5A2A");
        private static readonly Brush GoldRow = MakeVertical("#FFFDF6", "#F6ECD2");
        private static readonly Brush GoldBadge = MakeDiagonal("#FFD76A", "#D99A1C");
        private static readonly Brush GoldStreak = MakeStreak();

        private static Brush MakeVertical(string top, string bottom)
        {
            LinearGradientBrush g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(top), 0));
            g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(bottom), 1));
            g.Freeze();
            return g;
        }

        private static Brush MakeDiagonal(string a, string b)
        {
            LinearGradientBrush g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(a), 0));
            g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(b), 1));
            g.Freeze();
            return g;
        }

        private static Brush MakeStreak()
        {
            LinearGradientBrush g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 196, 64), 0.0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(150, 255, 196, 64), 0.5));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 196, 64), 1.0));
            g.Freeze();
            return g;
        }

        // Una franja de luz dorada que cruza la fila una sola vez (unos 0,6 s). Sin animaciones continuas.
        private UIElement BuildGoldSweep()
        {
            TranslateTransform move = new TranslateTransform(-220, 0);
            TransformGroup tg = new TransformGroup();
            tg.Children.Add(new SkewTransform(-20, 0));
            tg.Children.Add(move);
            Border streak = new Border
            {
                Width = 160,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = GoldStreak,
                RenderTransform = tg,
                IsHitTestVisible = false
            };
            Grid.SetColumnSpan(streak, 3);
            if (UiFeedback.Animations)
            {
                move.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(-220, 1500, TimeSpan.FromMilliseconds(650)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            }
            else
            {
                streak.Visibility = Visibility.Collapsed;
            }
            return streak;
        }

        // Color de cada tipo de acción (boceto A2).
        private static Brush ActionColor(string glyph)
        {
            switch (glyph)
            {
                case "play": return Make("#3F8CFF");
                case "camera": return Make("#1FB5A0");
                case "video": return Make("#8B5CF6");
                case "images": return Make("#F59E0B");
                case "trophy": return Make("#EAB308");
                case "house":
                case "lock": return Make("#64748B");
                case "circle-x": return Make("#EF4444");
                case "power": return Make("#EC4899");
                case "music": return Make("#22C55E");
                case "check": return Make("#22C55E");
                case "x": return Make("#EF4444");
                case "arrow-left": return Make("#64748B");
                case "moon": return Make("#6366F1");
                case "refresh-cw": return Make("#F59E0B");
                default: return null;
            }
        }

        private UIElement BuildRow(int index)
        {
            MenuEntry e = entries[index];
            Border b = new Border
            {
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(14, 8, 22, 8),
                Margin = new Thickness(0, 0, 0, 12),
                BorderThickness = new Thickness(1),
                BorderBrush = GlassEdge,
                Cursor = Cursors.Hand,
                Height = 84,
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
            bool fullBleed = false;
            if (e.Image != null)
            {
                // El logo llena toda la insignia (esquinas redondeadas). Si no trae cuadrado propio, la base toma su color.
                fullBleed = true;
                if (!AppIcon.IsFullBleed(e.Image))
                {
                    Color? pc = AppIcon.PlateColor(e.Image);
                    SolidColorBrush pb = pc.HasValue ? new SolidColorBrush(pc.Value) : new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x2B));
                    pb.Freeze();
                    e.PlateBrush = pb;
                }
                else
                {
                    e.PlateBrush = Brushes.Transparent;
                }
                Image appIcon = new Image { Source = e.Image, Width = 56, Height = 56, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(appIcon, BitmapScalingMode.HighQuality);
                iconBox.Width = 56;
                iconBox.Height = 56;
                iconBox.Child = appIcon;
            }
            if (e.Platinum)
            {
                iconBox.Width = 48;
                iconBox.Height = 48;
                iconBox.Child = TrophyIcon(3, true, false, 48);
            }
            Border badge = new Border
            {
                Width = 56,
                Height = 56,
                CornerRadius = new CornerRadius(16),
                HorizontalAlignment = HorizontalAlignment.Left,
                ClipToBounds = fullBleed,
                Child = iconBox
            };
            if (fullBleed)
            {
                badge.Clip = new RectangleGeometry(new Rect(0, 0, 56, 56), 16, 16);
            }
            TextBlock label = new TextBlock
            {
                FontSize = 27,
                VerticalAlignment = VerticalAlignment.Center
            };
            label.Text = e.Text ?? "";
            TextBlock descBlock = new TextBlock
            {
                Text = PadText(e.Desc ?? ""),
                FontSize = e.MainMusic && !e.OpenOnly ? 16 : 19,
                Foreground = MutedBrush,
                Visibility = Visibility.Collapsed,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            };
            descs.Add(string.IsNullOrEmpty(e.Desc) ? null : descBlock);
            StackPanel textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textPanel.Children.Add(label);
            textPanel.Children.Add(descBlock);
            Grid.SetColumn(badge, 0);
            Grid.SetColumn(textPanel, 1);
            Brush accent = ActionColor(e.Glyph);
            if (e.Platinum && !e.AccentColor.HasValue)
            {
                e.AccentColor = (Color)ColorConverter.ConvertFromString("#5BB8EE");
            }
            if (e.AccentColor.HasValue)
            {
                SolidColorBrush ab = new SolidColorBrush(e.AccentColor.Value);
                ab.Freeze();
                accent = ab;
            }
            badge.Background = accent != null ? BadgeGradient(accent) : BadgeBrush;
            badge.Tag = accent;
            // Franja de color en el borde izquierdo de la fila: sigue la curva de las esquinas (como en el boceto)
            // y solo se ve en la opción activa. Cubre el relleno de la fila con márgenes negativos.
            Border bar = new Border
            {
                CornerRadius = new CornerRadius(20),
                BorderThickness = new Thickness(6, 0, 0, 0),
                BorderBrush = accent != null ? BarWhite : SelTextBrush,
                Margin = new Thickness(-15, -9, -23, -9),
                Opacity = 0,
                IsHitTestVisible = false
            };
            accents.Add(bar);
            g.Children.Add(badge);
            g.Children.Add(textPanel);
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
            Grid wrap = new Grid();
            wrap.Children.Add(g);
            wrap.Children.Add(bar);
            b.Child = wrap;

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
                FontSize = 20,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            p.Children.Add(pill);
            p.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 22,
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
            if (!UiFeedback.Animations)
            {
                foreach (Border r in rows)
                {
                    r.Opacity = 1;
                }
                return;
            }
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
                Brush tag = badges[i].Tag as Brush;
                bool colored = tag != null;
                // Con color: fila de cristal teñida del color de la acción y texto blanco. Sin color: tarjeta clara de siempre.
                rows[i].Background = sel ? (colored ? SelGradient(tag) : SelBrush) : RowBrush;
                badges[i].Background = entries[i].Image != null ? (entries[i].PlateBrush ?? AppBadgeBrush) : (colored ? BadgeGradient(tag) : (sel ? BadgeSelBrush : BadgeBrush));
                icons[i].Stroke = colored ? Brushes.White : (sel ? SelTextBrush : (entries[i].Danger ? DangerBrush : TextBrush));
                labels[i].Foreground = sel ? (colored ? Brushes.White : SelTextBrush) : TextBrush;
                if (i < descs.Count && descs[i] != null)
                {
                    descs[i].Visibility = sel ? Visibility.Visible : Visibility.Collapsed;
                    descs[i].Foreground = colored ? SelDescBrush : MutedBrush;
                }
                if (i < accents.Count && accents[i] != null)
                {
                    accents[i].Opacity = sel ? 1 : 0;
                }
                if (rights[i] != null)
                {
                    rights[i].Foreground = sel ? (colored ? Brushes.White : SelTextBrush) : MutedBrush;
                }
            }
            if (musicCard != null)
            {
                musicCard.BorderBrush = IsMusicRow() ? MusicAccentBrush() : GlassEdge;
            }
            UpdateRecents();
        }

        private void RefreshTexts()
        {
            if (clockText != null)
            {
                clockText.Text = DateTime.Now.ToShortTimeString();
            }
            UpdateClipClock();
            UpdateMusicProgress();
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
                case "X":
                    Act("mando", "close");
                    break;
                case "Y":
                    Act("mando", "shuffle");
                    break;
                case "RightStick":
                    Act("mando", "repeat");
                    break;
                case "TriggerRight":
                case "RightStickUp":
                    Act("mando", "volup");
                    break;
                case "TriggerLeft":
                case "RightStickDown":
                    Act("mando", "voldown");
                    break;
                case "RightShoulder":
                    Act("mando", "skipnext");
                    break;
                case "LeftShoulder":
                    Act("mando", "skipprev");
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
                case "down":
                case "left":
                case "right":
                    UiFeedback.Play(UiFeedback.Kind.Move);
                    break;
                case "accept":
                    UiFeedback.Play(UiFeedback.Kind.Accept);
                    break;
                case "cancel":
                    UiFeedback.Play(UiFeedback.Kind.Back);
                    break;
            }
            switch (action)
            {
                case "up":
                    if (galMode) { if (viewing) ViewStep(-1); else MoveGrid(-GalCols); } else { recFocus = -1; Move(-1); }
                    break;
                case "down":
                    if (galMode) { if (viewing) ViewStep(1); else MoveGrid(GalCols); } else { recFocus = -1; Move(1); }
                    break;
                case "left":
                    if (galMode) { if (viewing) ViewSeek(-1); else MoveGrid(-1); }
                    else if (achMode) { CycleAchFilter(-1); }
                    else if (recFocus >= 0) { recFocus = recFocus == 0 ? -1 : recFocus - 1; UpdateRecents(); }
                    else if (IsMusicRow()) { MusicWatcher.Instance.Seek(-10); }
                    break;
                case "right":
                    if (galMode) { if (viewing) ViewSeek(1); else MoveGrid(1); }
                    else if (achMode) { CycleAchFilter(1); }
                    else if (recPanel != null && (recFocus >= 0 || IsEndRow()))
                    {
                        // «Finalizar juego» + derecha entra en el panel; dentro, derecha avanza entre los pósters.
                        recFocus = recFocus < 0 ? 0 : Math.Min(Recents.Count - 1, recFocus + 1);
                        UpdateRecents();
                    }
                    else if (IsMusicRow()) { MusicWatcher.Instance.Seek(10); }
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
                case "volup":
                case "voldown":
                    if (!galMode && !achMode && MusicRowLive() && MusicWatcher.Instance.Current.HasSession && MusicVolumeChange != null)
                    {
                        musicVolFlash = true;
                        MusicVolumeChange(action == "volup" ? 0.05f : -0.05f);
                    }
                    break;
                case "skipnext":
                case "skipprev":
                    if (achMode)
                    {
                        // En logros, L1 / R1 cambian de pestaña (Todos, Conseguidos, Pendientes).
                        CycleAchFilter(action == "skipnext" ? 1 : -1);
                        break;
                    }
                    // R1 / L1: siguiente o anterior desde cualquier fila mientras la tarjeta de música está a la vista.
                    if (!galMode && !achMode && MusicRowLive() && MusicWatcher.Instance.Current.HasSession)
                    {
                        if (action == "skipnext") MusicWatcher.Instance.Next(); else MusicWatcher.Instance.Previous();
                    }
                    break;
                case "shuffle":
                case "repeat":
                    if (!galMode && !achMode && MusicRowLive() && MusicWatcher.Instance.Current.HasSession)
                    {
                        if (action == "shuffle") MusicWatcher.Instance.ToggleShuffle(); else MusicWatcher.Instance.CycleRepeat();
                    }
                    break;
                case "close":
                    if (IsMusicRow() && CloseMusicInline != null)
                    {
                        CloseMusicInline();
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
            if (entries.Count == 0)
            {
                return;
            }
            selected = (selected + delta + entries.Count) % entries.Count;
            UpdateSelection();
        }

        // Momento en que se mostró la página actual (para ignorar pulsaciones que llegan justo después).
        public DateTime ShownAtUtc { get { return shownAt; } }

        public void Accept()
        {
            if (achMode)
            {
                return;
            }
            if (recFocus >= 0 && Recents != null && recFocus < Recents.Count)
            {
                // Elegido un juego reciente: el índice va después de las filas del menú.
                Choose(entries.Count + recFocus);
                return;
            }
            if (!achMode && !galMode && selected >= 0 && selected < entries.Count && entries[selected].Glyph == "music" &&
                entries[selected].OpenOnly && OpenMusicInline != null && OpenMusicInline())
            {
                return;
            }
            if (IsMusicRow())
            {
                // Pausa o reanuda la música sin cerrar el menú.
                MusicWatcher.Instance.PlayPause();
                return;
            }
            Choose(selected);
        }

        public void Cancel()
        {
            if (recFocus >= 0)
            {
                // Dentro del panel «Cambiar a», atrás solo sale del panel.
                recFocus = -1;
                UpdateRecents();
                return;
            }
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
                case Key.OemPlus:
                case Key.Add:
                    Act("teclado", "volup");
                    e.Handled = true;
                    break;
                case Key.OemMinus:
                case Key.Subtract:
                    Act("teclado", "voldown");
                    e.Handled = true;
                    break;
                case Key.X:
                    Act("teclado", "close");
                    e.Handled = true;
                    break;
                case Key.S:
                    Act("teclado", "shuffle");
                    e.Handled = true;
                    break;
                case Key.R:
                    Act("teclado", "repeat");
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
