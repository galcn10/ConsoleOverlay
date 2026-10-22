using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Playnite.SDK;

namespace ConsoleOverlay
{
    // ----------------------------------------------------------------------
    // Funciones de Windows
    // ----------------------------------------------------------------------

    internal static class NativeMethods
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }

        private const int SW_RESTORE = 9;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("powrprof.dll", SetLastError = true)] public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        public static void TapKey(byte vk)
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, 2, UIntPtr.Zero);
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr hObject);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);
        [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr processHandle);
        [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr processHandle);

        [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

        // 1 = proceso elevado (administrador), 0 = normal, -1 = no se puede saber (acceso denegado: protegido o elevado).
        public static int IsElevated(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero)
            {
                return -1;
            }
            try
            {
                IntPtr tok;
                if (!OpenProcessToken(h, 8, out tok))
                {
                    return -1;
                }
                try
                {
                    int elev;
                    int ret;
                    if (!GetTokenInformation(tok, 20, out elev, 4, out ret))
                    {
                        return -1;
                    }
                    return elev != 0 ? 1 : 0;
                }
                finally
                {
                    CloseHandle(tok);
                }
            }
            finally
            {
                CloseHandle(h);
            }
        }

        public static string GetProcessPath(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        public static bool SuspendProcess(int pid, out string reason)
        {
            reason = null;
            IntPtr h = OpenProcess(0x0800, false, pid);
            if (h == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                reason = "OpenProcess falló, error de Windows " + err + (err == 5 ? " (acceso denegado: proceso elevado o protegido)" : "");
                return false;
            }
            try
            {
                int st = NtSuspendProcess(h);
                if (st < 0)
                {
                    reason = "NtSuspendProcess devolvió 0x" + st.ToString("X");
                    return false;
                }
                return true;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        public static bool SuspendProcess(int pid)
        {
            IntPtr h = OpenProcess(0x0800, false, pid);
            if (h == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return NtSuspendProcess(h) >= 0;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        public static bool ResumeProcess(int pid)
        {
            IntPtr h = OpenProcess(0x0800, false, pid);
            if (h == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return NtResumeProcess(h) >= 0;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        public static long GetWindowArea(IntPtr h)
        {
            RECT r;
            if (IsIconic(h))
            {
                WINDOWPLACEMENT wp = new WINDOWPLACEMENT();
                wp.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
                if (!GetWindowPlacement(h, ref wp))
                {
                    return 0;
                }
                r = wp.rcNormalPosition;
            }
            else if (!GetWindowRect(h, out r))
            {
                return 0;
            }
            long w = Math.Max(0, r.Right - r.Left);
            long ht = Math.Max(0, r.Bottom - r.Top);
            return w * ht;
        }

        public static string Describe(IntPtr h)
        {
            if (h == IntPtr.Zero)
            {
                return "ninguna";
            }
            StringBuilder title = new StringBuilder(256);
            StringBuilder cls = new StringBuilder(256);
            GetWindowText(h, title, title.Capacity);
            GetClassName(h, cls, cls.Capacity);
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            return "0x" + h.ToInt64().ToString("X") + " clase=" + cls + " titulo=\"" + title + "\" pid=" + pid;
        }

        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        private const uint SWP_NOACTIVATE = 0x0010;

        public static void SendToBottom(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                return;
            }
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr hWnd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        public static bool IsMinimized(IntPtr hwnd)
        {
            return hwnd != IntPtr.Zero && IsWindow(hwnd) && IsIconic(hwnd);
        }

        // Minimiza sin activar (SW_SHOWMINNOACTIVE): la ventana deja de verse aunque esté "encima".
        public static void MinimizeNoActivate(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero && IsWindow(hwnd))
            {
                ShowWindow(hwnd, 7);
            }
        }

        public static bool IsTopmostWindow(IntPtr hwnd)
        {
            return (GetWindowLong32(hwnd, -20) & 0x8) != 0;
        }

        // Las primeras ventanas visibles en orden Z (de arriba abajo), para el diagnóstico.
        public static string DescribeZOrder(int max)
        {
            StringBuilder sb = new StringBuilder();
            int n = 0;
            IntPtr h = GetTopWindow(IntPtr.Zero);
            int guard = 0;
            while (h != IntPtr.Zero && n < max && guard++ < 400)
            {
                if (IsWindowVisible(h) && !IsIconic(h))
                {
                    StringBuilder title = new StringBuilder(128);
                    StringBuilder cls = new StringBuilder(128);
                    GetWindowText(h, title, title.Capacity);
                    GetClassName(h, cls, cls.Capacity);
                    if (title.Length > 0)
                    {
                        sb.Append("\n    ").Append(n + 1).Append(". 0x").Append(h.ToInt64().ToString("X"))
                          .Append(IsTopmostWindow(h) ? " [topmost]" : "")
                          .Append(" ").Append(cls).Append(" \"").Append(title).Append("\"");
                        n++;
                    }
                }
                h = GetWindow(h, 2); // GW_HWNDNEXT
            }
            return sb.ToString();
        }

        public static void Minimize(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero && IsWindow(hwnd))
            {
                ShowWindow(hwnd, 6);
            }
        }

        public static void SetTopmost(IntPtr hwnd, bool topmost)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                return;
            }
            SetWindowPos(hwnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        }

        // Quita el "siempre encima" a la ventana dejándola en lo alto del plano normal, sin activarla.
        public static void LowerTopmostNoActivate(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                return;
            }
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        // La ventana no recibe clics ni foco (WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW): sirve de telón sin estorbar al juego.
        public static void MakeClickThrough(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                return;
            }
            int ex = GetWindowLong32(hwnd, -20);
            SetWindowLong32(hwnd, -20, ex | 0x20 | 0x08000000 | 0x80);
        }

        // Sube la ventana al plano "siempre encima" sin darle el foco.
        public static void RaiseTopmostNoActivate(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                return;
            }
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        public static bool IsForeground(IntPtr hwnd)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == hwnd)
            {
                return true;
            }
            if (fg == IntPtr.Zero)
            {
                return false;
            }
            uint pidFg;
            uint pidTarget;
            GetWindowThreadProcessId(fg, out pidFg);
            GetWindowThreadProcessId(hwnd, out pidTarget);
            return pidFg == pidTarget;
        }

        private static void TryForeground(IntPtr hwnd, bool altTrick)
        {
            IntPtr fg = GetForegroundWindow();
            uint curThread = GetCurrentThreadId();
            uint ignored;
            uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out ignored) : 0;
            uint targetThread = GetWindowThreadProcessId(hwnd, out ignored);

            bool a1 = false;
            bool a2 = false;
            if (fgThread != 0 && fgThread != curThread)
            {
                a1 = AttachThreadInput(curThread, fgThread, true);
            }
            if (targetThread != 0 && targetThread != curThread && targetThread != fgThread)
            {
                a2 = AttachThreadInput(curThread, targetThread, true);
            }
            try
            {
                if (altTrick)
                {
                    keybd_event(0x12, 0, 0, UIntPtr.Zero);
                    keybd_event(0x12, 0, 2, UIntPtr.Zero);
                }
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (a1)
                {
                    AttachThreadInput(curThread, fgThread, false);
                }
                if (a2)
                {
                    AttachThreadInput(curThread, targetThread, false);
                }
            }
        }

        // Trae una ventana al frente sin bloquear a quien llama: si el juego (o Playnite) no responde, Windows
        // puede tardar en aceptar el cambio de foco y eso congelaba el menú. Se espera como mucho 700 ms.
        public static bool BringToFront(IntPtr hwnd, bool topmost)
        {
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }
            bool result = false;
            System.Threading.Tasks.Task t = System.Threading.Tasks.Task.Run(() => { result = BringToFrontCore(hwnd, topmost); });
            if (t.Wait(700))
            {
                return result;
            }
            return IsForeground(hwnd);
        }

        private static bool BringToFrontCore(IntPtr hwnd, bool topmost)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                return false;
            }
            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, SW_RESTORE);
            }
            if (topmost)
            {
                SetTopmost(hwnd, true);
            }
            for (int attempt = 0; attempt < 5; attempt++)
            {
                TryForeground(hwnd, attempt >= 1);
                if (attempt >= 2 && !IsForeground(hwnd))
                {
                    // Métodos extra para cuando Windows rechaza el cambio de foco (p. ej. juegos con anticheat).
                    try
                    {
                        SwitchToThisWindow(hwnd, true);
                        if (attempt >= 3)
                        {
                            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                            SetForegroundWindow(hwnd);
                        }
                    }
                    catch
                    {
                    }
                }
                // Se comprueba cada 10 ms en vez de esperar 150 ms fijos: el menú abre en cuanto Windows cede el foco.
                for (int w = 0; w < 15; w++)
                {
                    Thread.Sleep(10);
                    if (IsForeground(hwnd))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }

    // ----------------------------------------------------------------------
    // Miniatura en directo de otra ventana con el compositor de Windows (DWM): no copia imágenes ni usa CPU.
    // ----------------------------------------------------------------------
    internal static class DwmThumb
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DwmRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DwmSize
        {
            public int Width;
            public int Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DwmThumbnailProperties
        {
            public uint Flags;
            public DwmRect Destination;
            public DwmRect Source;
            public byte Opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool Visible;
            [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUnregisterThumbnail(IntPtr thumb);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DwmThumbnailProperties props);

        [DllImport("dwmapi.dll")]
        private static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out DwmSize size);

        // Registra la miniatura de "src" dentro de la ventana "dest". 0 si Windows no lo permite.
        public static IntPtr Register(IntPtr dest, IntPtr src, out int srcWidth, out int srcHeight)
        {
            srcWidth = 0;
            srcHeight = 0;
            try
            {
                IntPtr t;
                if (DwmRegisterThumbnail(dest, src, out t) != 0 || t == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }
                DwmSize sz;
                if (DwmQueryThumbnailSourceSize(t, out sz) != 0 || sz.Width <= 0 || sz.Height <= 0)
                {
                    DwmUnregisterThumbnail(t);
                    return IntPtr.Zero;
                }
                srcWidth = sz.Width;
                srcHeight = sz.Height;
                return t;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        public static bool Place(IntPtr thumb, int left, int top, int right, int bottom, bool visible)
        {
            try
            {
                DwmThumbnailProperties p = new DwmThumbnailProperties
                {
                    // 0x1 destino, 0x4 opacidad, 0x8 visible, 0x10 solo el área de cliente
                    Flags = 0x1 | 0x4 | 0x8 | 0x10,
                    Destination = new DwmRect { Left = left, Top = top, Right = right, Bottom = bottom },
                    Opacity = 255,
                    Visible = visible,
                    SourceClientAreaOnly = true
                };
                return DwmUpdateThumbnailProperties(thumb, ref p) == 0;
            }
            catch
            {
                return false;
            }
        }

        public static void Unregister(IntPtr thumb)
        {
            try
            {
                if (thumb != IntPtr.Zero)
                {
                    DwmUnregisterThumbnail(thumb);
                }
            }
            catch
            {
            }
        }
    }

    // ----------------------------------------------------------------------
    // Miniaturas de archivos (vídeos) con el propio Windows, sin programas externos.
    // ----------------------------------------------------------------------
    internal static class ShellThumb
    {
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int cx;
            public int cy;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr pbc,
            [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr h);

        // Devuelve la miniatura (congelada) o null si Windows no tiene una para ese archivo. Debe llamarse desde un hilo STA.
        public static System.Windows.Media.ImageSource Get(string path, int width, int height)
        {
            IShellItemImageFactory f = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                Guid iid = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out f);
                // 0x1 = admitir un tamaño mayor, 0x8 = solo miniatura (no el icono del tipo de archivo).
                int hr = f.GetImage(new NativeSize { cx = width, cy = height }, 0x1 | 0x8, out hbm);
                if (hr != 0 || hbm == IntPtr.Zero)
                {
                    return null;
                }
                System.Windows.Media.Imaging.BitmapSource bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hbm, IntPtr.Zero, System.Windows.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                return bs;
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
                if (f != null)
                {
                    Marshal.ReleaseComObject(f);
                }
            }
        }
    }

    // ----------------------------------------------------------------------
    // Silenciar el audio de procesos concretos (mezclador de Windows)
    // ----------------------------------------------------------------------

    internal static class AudioMuter
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        // Devuelve cuántas sesiones de audio de esos procesos se han tocado. Con fullVolume también sube su volumen al máximo.
        public static int SetMute(IEnumerable<int> pids, bool mute, bool fullVolume = false)
        {
            int touched = 0;
            HashSet<int> wanted = new HashSet<int>(pids);
            if (wanted.Count == 0)
            {
                return 0;
            }

            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioSessionManager2 manager = null;
            IAudioSessionEnumerator sessions = null;
            try
            {
                // Se crea por CLSID (en vez de "new" de la clase COM): evita el InvalidCastException
                // que aparecía al convertir el objeto cuando el hilo no es STA.
                Type enumType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
                enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumType);
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 1, out device));

                Guid iid = typeof(IAudioSessionManager2).GUID;
                object o;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out o));
                manager = (IAudioSessionManager2)o;

                Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out sessions));
                int count;
                Marshal.ThrowExceptionForHR(sessions.GetCount(out count));

                Guid ctx = Guid.Empty;
                for (int i = 0; i < count; i++)
                {
                    object ctlObj = null;
                    try
                    {
                        Marshal.ThrowExceptionForHR(sessions.GetSession(i, out ctlObj));
                        IAudioSessionControl2 ctl2 = ctlObj as IAudioSessionControl2;
                        if (ctl2 == null)
                        {
                            continue;
                        }
                        uint spid;
                        ctl2.GetProcessId(out spid);
                        if (!wanted.Contains((int)spid))
                        {
                            continue;
                        }
                        ISimpleAudioVolume vol = ctlObj as ISimpleAudioVolume;
                        if (vol != null)
                        {
                            vol.SetMute(mute, ref ctx);
                            if (fullVolume && !mute)
                            {
                                vol.SetMasterVolume(1.0f, ref ctx);
                            }
                            touched++;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(ex, "No se pudo cambiar el silencio de una sesión de audio");
                    }
                    finally
                    {
                        if (ctlObj != null)
                        {
                            Marshal.ReleaseComObject(ctlObj);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo cambiar el silencio del juego");
            }
            finally
            {
                if (sessions != null) Marshal.ReleaseComObject(sessions);
                if (manager != null) Marshal.ReleaseComObject(manager);
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
            return touched;
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject
        {
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
            [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
            [PreserveSig] int GetState(out int pdwState);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr AudioSessionGuid, int StreamFlags, out IntPtr SessionControl);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr AudioSessionGuid, int StreamFlags, out IntPtr AudioVolume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
            [PreserveSig] int RegisterSessionNotification(IntPtr SessionNotification);
            [PreserveSig] int UnregisterSessionNotification(IntPtr SessionNotification);
            [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionID, IntPtr duckNotification);
            [PreserveSig] int UnregisterDuckNotification(IntPtr duckNotification);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int SessionCount);
            [PreserveSig] int GetSession(int SessionCount, [MarshalAs(UnmanagedType.IUnknown)] out object Session);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            [PreserveSig] int GetState(out int pRetVal);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
            [PreserveSig] int GetGroupingParam(out Guid pRetVal);
            [PreserveSig] int SetGroupingParam(ref Guid Override, ref Guid EventContext);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr NewNotifications);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr NewNotifications);
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            [PreserveSig] int GetProcessId(out uint pRetVal);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float fLevel, ref Guid EventContext);
            [PreserveSig] int GetMasterVolume(out float pfLevel);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid EventContext);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        }
    }
}
