using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Playnite.SDK;

namespace ConsoleOverlay
{
    // ----------------------------------------------------------------------
    // Colores del menú: los toma del tema de Playnite en uso (sus recursos de color) o de la carátula del juego,
    // y los normaliza para que el texto blanco siempre se lea bien sobre el fondo.
    // ----------------------------------------------------------------------
    internal static class Palette
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public static Color Mix(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromRgb((byte)Math.Round(a.R + (b.R - a.R) * t),
                                 (byte)Math.Round(a.G + (b.G - a.G) * t),
                                 (byte)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static void ToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            v = max;
            s = max <= 0 ? 0 : d / max;
            if (d <= 0)
            {
                h = 0;
            }
            else if (max == r)
            {
                h = 60 * (((g - b) / d) % 6);
            }
            else if (max == g)
            {
                h = 60 * ((b - r) / d + 2);
            }
            else
            {
                h = 60 * ((r - g) / d + 4);
            }
            if (h < 0)
            {
                h += 360;
            }
        }

        public static Color FromHsv(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            double m = v - c;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        // Deja el color con una saturación y un brillo agradables (ni grisáceo ni oscuro ni deslumbrante).
        public static Color Normalize(Color c)
        {
            double h, s, v;
            ToHsv(c, out h, out s, out v);
            s = Math.Max(0.45, Math.Min(0.85, s));
            v = Math.Max(0.65, Math.Min(0.95, v));
            return FromHsv(h, s, v);
        }

        // Color dominante de una imagen (los tonos vivos pesan más que los grises, negros o blancos). null si es casi gris.
        public static Color? Dominant(ImageSource src)
        {
            try
            {
                BitmapSource bs = src as BitmapSource;
                if (bs == null || bs.PixelWidth <= 0 || bs.PixelHeight <= 0)
                {
                    return null;
                }
                double sx = Math.Min(1.0, 32.0 / bs.PixelWidth), sy = Math.Min(1.0, 32.0 / bs.PixelHeight);
                BitmapSource small = new TransformedBitmap(bs, new ScaleTransform(sx, sy));
                small = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
                int w = small.PixelWidth, h = small.PixelHeight;
                byte[] px = new byte[w * h * 4];
                small.CopyPixels(px, w * 4, 0);
                double r = 0, g = 0, b = 0, sum = 0;
                for (int i = 0; i + 3 < px.Length; i += 4)
                {
                    if (px[i + 3] < 128)
                    {
                        continue;
                    }
                    double bb = px[i], gg = px[i + 1], rr = px[i + 2];
                    double max = Math.Max(rr, Math.Max(gg, bb)), min = Math.Min(rr, Math.Min(gg, bb));
                    double v = max / 255.0, sat = max <= 0 ? 0 : (max - min) / max;
                    if (v < 0.18 || sat < 0.12)
                    {
                        continue;
                    }
                    double weight = sat * sat * v;
                    r += rr * weight;
                    g += gg * weight;
                    b += bb * weight;
                    sum += weight;
                }
                if (sum < 0.4)
                {
                    return null;
                }
                return Color.FromRgb((byte)(r / sum), (byte)(g / sum), (byte)(b / sum));
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo calcular el color dominante");
                return null;
            }
        }

        // Color de acento del tema de Playnite en uso. Debe llamarse en el hilo de la interfaz de Playnite.
        // Prueba, por orden, los recursos habituales de los temas; se queda con el primero que tenga un tono vivo.
        public static Color? FromTheme(out string info)
        {
            info = "sin recursos";
            try
            {
                Application app = Application.Current;
                if (app == null)
                {
                    return null;
                }
                string[] keys =
                {
                    "GlyphColor", "GlyphBrush", "AccentColor", "AccentBrush", "HighlightColor", "HighlightBrush",
                    "SelectionBrush", "MainBackgourndBrush", "OverlayMenuBackgroundBrush", "TopBarBackgroundBrush",
                    "ControlBackgroundColor", "ControlBackgroundBrush"
                };
                List<string> seen = new List<string>();
                foreach (string k in keys)
                {
                    Color? c = ColorOf(app.TryFindResource(k));
                    if (c == null)
                    {
                        continue;
                    }
                    double h, s, v;
                    ToHsv(c.Value, out h, out s, out v);
                    seen.Add(k + "=#" + c.Value.R.ToString("X2") + c.Value.G.ToString("X2") + c.Value.B.ToString("X2"));
                    if (s >= 0.25 && v >= 0.15)
                    {
                        info = "recurso " + k + " (" + string.Join(", ", seen) + ")";
                        return c;
                    }
                }
                info = seen.Count == 0 ? "ningún recurso de color conocido" : "sin tonos vivos (" + string.Join(", ", seen) + ")";
            }
            catch (Exception ex)
            {
                info = "error: " + ex.Message;
            }
            return null;
        }

        private static Color? ColorOf(object o)
        {
            if (o is Color)
            {
                return (Color)o;
            }
            SolidColorBrush sb = o as SolidColorBrush;
            if (sb != null)
            {
                return sb.Color;
            }
            GradientBrush gb = o as GradientBrush;
            if (gb != null && gb.GradientStops.Count > 0)
            {
                // El tono más vivo de los puntos del degradado.
                Color best = gb.GradientStops[0].Color;
                double bestS = -1;
                foreach (GradientStop st in gb.GradientStops)
                {
                    double h, s, v;
                    ToHsv(st.Color, out h, out s, out v);
                    if (s > bestS)
                    {
                        bestS = s;
                        best = st.Color;
                    }
                }
                return best;
            }
            return null;
        }

        // Tipografía del tema de Playnite en uso (la de su skin). Debe llamarse en el hilo de la interfaz de Playnite.
        // Prueba el estilo base de los textos del tema y, si no fija fuente, los recursos de fuente habituales.
        // Devuelve null si no encuentra ninguna (el menú usa entonces la fuente del sistema).
        public static FontFamily ThemeFont(out string info)
        {
            info = "sin recursos";
            try
            {
                Application app = Application.Current;
                if (app == null)
                {
                    return null;
                }
                string systemFont = new TextBlock().FontFamily.Source;
                FontFamily ff = null;
                string via = null;
                Style st = app.TryFindResource("TextBlockBaseStyle") as Style;
                if (st != null)
                {
                    FontFamily f = new TextBlock { Style = st }.FontFamily;
                    if (f != null && f.Source != systemFont)
                    {
                        ff = f;
                        via = "TextBlockBaseStyle";
                    }
                }
                if (ff == null)
                {
                    foreach (string k in new[] { "FontFamily", "FontTitilliumWeb", "FontTitilliumWebLight" })
                    {
                        FontFamily f = app.TryFindResource(k) as FontFamily;
                        if (f != null)
                        {
                            ff = f;
                            via = k;
                            break;
                        }
                    }
                }
                if (ff == null)
                {
                    info = "ninguna fuente en el tema (se usa " + systemFont + ")";
                    return null;
                }
                // La variante fina (Light) se lee mal sobre fondo oscuro a distancia: se prefiere la normal de la misma familia.
                string src = ff.Source ?? "";
                if (src.IndexOf("Light", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    FontFamily regular = app.TryFindResource("FontTitilliumWeb") as FontFamily;
                    if (regular != null && regular.Source != null && regular.Source.IndexOf("Light", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        info = "fuente del tema (" + via + "): " + src + " -> variante normal " + regular.Source;
                        return regular;
                    }
                }
                info = "fuente del tema (" + via + "): " + src;
                return ff;
            }
            catch (Exception ex)
            {
                info = "error: " + ex.Message;
                return null;
            }
        }

        // Tipografía elegida en los ajustes: la del tema (null aquí = usar ThemeFont), Segoe UI o una de las incluidas en la carpeta fonts.
        public static FontFamily ChosenFont(string mode, out string info)
        {
            info = null;
            try
            {
                if (string.Equals(mode, "system", StringComparison.OrdinalIgnoreCase))
                {
                    info = "Segoe UI (ajuste)";
                    return new FontFamily("Segoe UI");
                }
                if (string.Equals(mode, "inter", StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "nunito", StringComparison.OrdinalIgnoreCase))
                {
                    string name = string.Equals(mode, "inter", StringComparison.OrdinalIgnoreCase) ? "Inter" : "Nunito";
                    string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Palette).Assembly.Location), "fonts");
                    if (System.IO.File.Exists(System.IO.Path.Combine(dir, name + "-Regular.ttf")))
                    {
                        info = name + " incluida (ajuste)";
                        return new FontFamily(new Uri(dir.TrimEnd('\\') + "\\"), "./#" + name);
                    }
                    info = "no se encontró la carpeta fonts; se usa la del tema";
                }
            }
            catch (Exception ex)
            {
                info = "error: " + ex.Message;
            }
            return null;
        }

        // Elige el color de acento según el ajuste: "theme" (tema y, si no hay, carátula), "game" (carátula y, si no, tema), "neutral".
        public static Color? Choose(string mode, ImageSource cover, ImageSource backdrop, out string info)
        {
            info = "neutro";
            if (string.Equals(mode, "neutral", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            bool gameFirst = string.Equals(mode, "game", StringComparison.OrdinalIgnoreCase);
            Color? c = null;
            string tinfo = "";
            if (!gameFirst)
            {
                c = FromTheme(out tinfo);
                if (c != null)
                {
                    info = "tema: " + tinfo;
                    return Normalize(c.Value);
                }
            }
            c = Dominant(cover) ?? Dominant(backdrop);
            if (c != null)
            {
                info = "carátula" + (gameFirst ? "" : " (tema: " + tinfo + ")");
                return Normalize(c.Value);
            }
            if (gameFirst)
            {
                c = FromTheme(out tinfo);
                if (c != null)
                {
                    info = "tema: " + tinfo;
                    return Normalize(c.Value);
                }
            }
            info = "sin color disponible";
            return null;
        }
    }
}
