using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ConsoleOverlay
{
    // Icono de una aplicación (por ruta de .exe o por «shell:AppsFolder\id» en las apps de la Tienda) y su color dominante.
    internal static class AppIcon
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr h);

        public static ImageSource Load(string target, out Color? dominant)
        {
            dominant = null;
            if (string.IsNullOrEmpty(target) || target.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            IntPtr hbm = IntPtr.Zero;
            try
            {
                Guid iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                object o;
                SHCreateItemFromParsingName(target, IntPtr.Zero, ref iid, out o);
                IShellItemImageFactory f = (IShellItemImageFactory)o;
                SIZE sz = new SIZE { cx = 96, cy = 96 };
                // SIIGBF_BIGGERSIZEOK (1) | SIIGBF_ICONONLY (4)
                if (f.GetImage(sz, 5, out hbm) != 0 || hbm == IntPtr.Zero)
                {
                    return null;
                }
                BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                FormatConvertedBitmap conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                conv.Freeze();
                dominant = Dominant(conv);
                return conv;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hbm != IntPtr.Zero)
                {
                    DeleteObject(hbm);
                }
            }
        }

        // Icono a partir de una imagen (PNG del logo de una app de la Tienda).
        public static ImageSource LoadBytes(byte[] data, out Color? dominant)
        {
            dominant = null;
            try
            {
                if (data == null || data.Length == 0)
                {
                    return null;
                }
                BitmapImage bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 96;
                bi.StreamSource = new System.IO.MemoryStream(data);
                bi.EndInit();
                bi.Freeze();
                FormatConvertedBitmap conv = new FormatConvertedBitmap(bi, PixelFormats.Bgra32, null, 0);
                conv.Freeze();
                dominant = Dominant(conv);
                return conv;
            }
            catch
            {
                return null;
            }
        }

        // ¿El logo trae fondo propio hasta las esquinas (como el cuadrado verde de Spotify)? Entonces llena toda la insignia.
        public static bool IsFullBleed(ImageSource img)
        {
            try
            {
                BitmapSource bs = img as BitmapSource;
                if (bs == null || bs.PixelWidth < 8 || bs.PixelHeight < 8)
                {
                    return false;
                }
                int w = bs.PixelWidth, h = bs.PixelHeight;
                byte[] px = new byte[4];
                int[][] pts = { new[] { 2, 2 }, new[] { w - 3, 2 }, new[] { 2, h - 3 }, new[] { w - 3, h - 3 } };
                foreach (int[] pt in pts)
                {
                    bs.CopyPixels(new Int32Rect(pt[0], pt[1], 1, 1), px, 4, 0);
                    if (px[3] < 200)
                    {
                        return false;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Color del fondo del logo (el de su zona opaca más cercana al borde superior): sirve de base para que logos
        // sin cuadrado propio (como el círculo blanco de SimpMusic) llenen la insignia entera sin marco.
        public static Color? PlateColor(ImageSource img)
        {
            try
            {
                BitmapSource bs = img as BitmapSource;
                if (bs == null || bs.PixelWidth < 8 || bs.PixelHeight < 8)
                {
                    return null;
                }
                int w = bs.PixelWidth, h = bs.PixelHeight;
                byte[] px = new byte[4];
                for (int y = 0; y < h / 2; y++)
                {
                    bs.CopyPixels(new Int32Rect(w / 2, y, 1, 1), px, 4, 0);
                    if (px[3] >= 200)
                    {
                        int y2 = Math.Min(h - 1, y + 4);
                        bs.CopyPixels(new Int32Rect(w / 2, y2, 1, 1), px, 4, 0);
                        return Color.FromRgb(px[2], px[1], px[0]);
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        // Media de los píxeles visibles ponderada por su viveza (ignora fondos blancos, negros y grises).
        private static Color? Dominant(BitmapSource bmp)
        {
            try
            {
                int w = bmp.PixelWidth, h = bmp.PixelHeight;
                byte[] px = new byte[w * h * 4];
                bmp.CopyPixels(px, w * 4, 0);
                double r = 0, g = 0, b = 0, wt = 0;
                for (int i = 0; i < px.Length; i += 4)
                {
                    if (px[i + 3] < 128)
                    {
                        continue;
                    }
                    double bl = px[i], gr = px[i + 1], rd = px[i + 2];
                    double max = Math.Max(rd, Math.Max(gr, bl)), min = Math.Min(rd, Math.Min(gr, bl));
                    if (max < 40)
                    {
                        continue;
                    }
                    double sat = (max - min) / max;
                    double weight = sat * sat * (max / 255.0);
                    r += rd * weight;
                    g += gr * weight;
                    b += bl * weight;
                    wt += weight;
                }
                if (wt < 5)
                {
                    return null;
                }
                return Color.FromRgb((byte)(r / wt), (byte)(g / wt), (byte)(b / wt));
            }
            catch
            {
                return null;
            }
        }
    }
}
