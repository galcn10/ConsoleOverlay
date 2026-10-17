using System;
using System.Collections.Generic;
using System.Threading;

namespace ConsoleOverlay
{
    // ----------------------------------------------------------------------
    // Atajos de teclado escritos como texto ("Ctrl+Shift+S", "Win+Alt+PrtScn")
    // y su envío al sistema, para accionar el programa de capturas del usuario.
    // ----------------------------------------------------------------------
    internal static class KeyChord
    {
        public static bool TryParse(string text, out List<byte> mods, out byte vk)
        {
            mods = new List<byte>();
            vk = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            bool haveKey = false;
            foreach (string raw in text.Split('+'))
            {
                string t = raw.Trim().ToLowerInvariant();
                if (t.Length == 0)
                {
                    return false;
                }

                switch (t)
                {
                    case "ctrl":
                    case "control":
                    case "ctl":
                        mods.Add(0x11);
                        continue;
                    case "alt":
                        mods.Add(0x12);
                        continue;
                    case "shift":
                    case "mayús":
                    case "mayus":
                        mods.Add(0x10);
                        continue;
                    case "win":
                    case "windows":
                    case "meta":
                        mods.Add(0x5B);
                        continue;
                }

                if (haveKey)
                {
                    return false;
                }
                byte k = KeyFromName(t);
                if (k == 0)
                {
                    return false;
                }
                vk = k;
                haveKey = true;
            }
            return haveKey;
        }

        private static byte KeyFromName(string t)
        {
            if (t.Length == 1)
            {
                char c = t[0];
                if (c >= 'a' && c <= 'z')
                {
                    return (byte)(c - 'a' + 'A');
                }
                if (c >= '0' && c <= '9')
                {
                    return (byte)c;
                }
            }

            int n;
            if (t.Length >= 2 && t[0] == 'f' && int.TryParse(t.Substring(1), out n) && n >= 1 && n <= 24)
            {
                return (byte)(0x6F + n);
            }

            switch (t)
            {
                case "printscreen":
                case "prtscn":
                case "prtsc":
                case "impr":
                case "imprpant":
                    return 0x2C;
                case "insert":
                case "ins":
                    return 0x2D;
                case "delete":
                case "del":
                case "supr":
                    return 0x2E;
                case "home":
                case "inicio":
                    return 0x24;
                case "end":
                case "fin":
                    return 0x23;
                case "pageup":
                case "repág":
                case "repag":
                    return 0x21;
                case "pagedown":
                case "avpág":
                case "avpag":
                    return 0x22;
                case "space":
                case "espacio":
                    return 0x20;
                case "enter":
                case "intro":
                    return 0x0D;
                case "tab":
                    return 0x09;
                case "esc":
                case "escape":
                    return 0x1B;
            }
            return 0;
        }

        public static bool IsValid(string text)
        {
            List<byte> mods;
            byte vk;
            return TryParse(text, out mods, out vk);
        }

        // Pulsa y suelta el atajo, con pequeñas pausas para que el programa receptor lo registre.
        public static bool Send(string text)
        {
            List<byte> mods;
            byte vk;
            if (!TryParse(text, out mods, out vk))
            {
                return false;
            }

            foreach (byte m in mods)
            {
                NativeMethods.keybd_event(m, 0, 0, UIntPtr.Zero);
                Thread.Sleep(30);
            }

            uint ext = IsExtended(vk) ? 1u : 0u;
            NativeMethods.keybd_event(vk, 0, ext, UIntPtr.Zero);
            Thread.Sleep(60);
            NativeMethods.keybd_event(vk, 0, ext | 2u, UIntPtr.Zero);
            Thread.Sleep(30);

            for (int i = mods.Count - 1; i >= 0; i--)
            {
                NativeMethods.keybd_event(mods[i], 0, 2, UIntPtr.Zero);
                Thread.Sleep(30);
            }
            return true;
        }

        private static bool IsExtended(byte vk)
        {
            return (vk >= 0x21 && vk <= 0x2E) || vk == 0x2C;
        }
    }
}
