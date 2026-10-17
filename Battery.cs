using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Playnite.SDK;

namespace ConsoleOverlay
{
    internal sealed class BatteryInfo
    {
        public string Name;
        public int Level;       // 0 vacío, 1 bajo, 2 medio, 3 lleno (siempre válido)
        public int Percent = -1; // 0-100 si se conoce de verdad; -1 si solo hay nivel
        public bool Wired;      // por cable / cargando
        public string Source;   // "HID", "SDL" o "XInput"
        public int Index;       // 1..N, para distinguir mandos
        public bool IsSony;
    }

    // ----------------------------------------------------------------------
    // Batería de los mandos. Varias fuentes, de más a menos precisa, para que valga con cualquier modelo:
    //  1) HID directo (solo lectura) para DualSense / DualSense Edge / DualShock 4: porcentaje real.
    //  2) SDL2 (el SDL2.dll que ya usa Playnite): Xbox, Switch Pro, GameSir, 8BitDo...: 4 niveles.
    //  3) XInput de Windows: mandos Xbox o emulados: 4 niveles.
    // Devuelve la lista de todos los mandos con batería detectable (vacía si no hay).
    // ----------------------------------------------------------------------
    internal static class ControllerBattery
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static bool sdlFailed;
        private static bool sdlReady;
        private static bool hidFailed;
        private static int hidTimeouts;
        private static string lastLogged;
        private static string sdlDiag = "";
        private static string hidDiag = "";
        private static string hidDetail = "";

        public static void Preload(string playniteDir)
        {
            try
            {
                if (string.IsNullOrEmpty(playniteDir) || GetModuleHandle("SDL2.dll") != IntPtr.Zero)
                {
                    return;
                }
                string p = System.IO.Path.Combine(playniteDir, "SDL2.dll");
                if (System.IO.File.Exists(p))
                {
                    LoadLibrary(p);
                }
            }
            catch
            {
            }
        }

        private static string layoutCache;
        private static DateTime layoutAt = DateTime.MinValue;

        // Distribución de botones según los mandos conectados: "PlayStation", "Nintendo" o "Xbox".
        public static string GuessLayout()
        {
            if (layoutCache != null && (DateTime.UtcNow - layoutAt).TotalSeconds < 20)
            {
                return layoutCache;
            }
            string r = "Xbox";
            try
            {
                foreach (BatteryInfo b in ReadSdl())
                {
                    string n = (b.Name ?? "").ToLowerInvariant();
                    if (b.IsSony || n.Contains("dualsense") || n.Contains("dualshock") || n.Contains("ps4") || n.Contains("ps5") || n.Contains("playstation"))
                    {
                        r = "PlayStation";
                        break;
                    }
                    if (n.Contains("nintendo") || n.Contains("switch"))
                    {
                        r = "Nintendo";
                    }
                }
            }
            catch
            {
            }
            layoutCache = r;
            layoutAt = DateTime.UtcNow;
            logger.Info("Botones del mando detectados: " + r);
            return r;
        }

        public static List<BatteryInfo> Read()
        {
            List<BatteryInfo> hid = ReadHidSafe();
            List<BatteryInfo> sdl = ReadSdl();
            List<BatteryInfo> all = new List<BatteryInfo>();
            all.AddRange(hid);

            bool sdlNonSony = false;
            int sonyToSkip = hid.Count;
            foreach (BatteryInfo s in sdl)
            {
                // Los mandos Sony que ya dio HID (con porcentaje) no se repiten con los de SDL.
                if (s.IsSony && sonyToSkip > 0)
                {
                    sonyToSkip--;
                    continue;
                }
                if (!s.IsSony)
                {
                    sdlNonSony = true;
                }
                all.Add(s);
            }
            if (!sdlNonSony)
            {
                all.AddRange(ReadXInput());
            }
            for (int i = 0; i < all.Count; i++)
            {
                all[i].Index = i + 1;
            }

            StringBuilder key = new StringBuilder();
            foreach (BatteryInfo b in all)
            {
                key.Append(b.Source).Append('|').Append(b.Name).Append('|').Append(b.Level).Append('|').Append(b.Percent).Append('|').Append(b.Wired).Append(';');
            }
            string k = key.ToString();
            if (k != lastLogged)
            {
                lastLogged = k;
                if (all.Count == 0)
                {
                    logger.Info("Baterías: no se detecta ninguna. " + sdlDiag + " " + hidDiag);
                }
                else
                {
                    StringBuilder sb = new StringBuilder("Baterías (" + all.Count + "): ");
                    foreach (BatteryInfo b in all)
                    {
                        sb.Append("[").Append(b.Index).Append(" ").Append(b.Source).Append(": ").Append(b.Name)
                          .Append(", ").Append(b.Percent >= 0 ? b.Percent + "%" : "nivel " + b.Level + "/3")
                          .Append(b.Wired ? ", cable/carga" : "").Append("] ");
                    }
                    logger.Info(sb.ToString() + sdlDiag + " " + hidDiag);
                }
            }
            return all;
        }

        public static int LevelFromPercent(int p)
        {
            return p > 70 ? 3 : (p > 20 ? 2 : (p > 5 ? 1 : 0));
        }

        // ------------------------------------------------------------ HID (Sony)

        private static List<BatteryInfo> ReadHidSafe()
        {
            List<BatteryInfo> none = new List<BatteryInfo>();
            if (hidFailed)
            {
                return none;
            }
            try
            {
                Task<List<BatteryInfo>> t = Task.Run(() => ReadHid());
                if (!t.Wait(1200))
                {
                    hidTimeouts++;
                    if (hidTimeouts >= 2)
                    {
                        hidFailed = true;
                        logger.Warn("Lectura HID de mandos demasiado lenta; se desactiva y se usa SDL/XInput.");
                    }
                    return none;
                }
                return t.Result;
            }
            catch (Exception ex)
            {
                hidFailed = true;
                logger.Warn(ex, "Lectura HID de mandos no disponible; se usa SDL/XInput.");
                return none;
            }
        }

        private static List<BatteryInfo> ReadHid()
        {
            List<BatteryInfo> list = new List<BatteryInfo>();
            string diag = "HID Sony: ";
            hidDetail = "";
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr set = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, 0x12);
            if (set == IntPtr.Zero || set == new IntPtr(-1))
            {
                hidDiag = diag + "sin acceso";
                return list;
            }
            int seen = 0;
            int opened = 0;
            try
            {
                for (uint i = 0; ; i++)
                {
                    SP_DEVICE_INTERFACE_DATA did = new SP_DEVICE_INTERFACE_DATA();
                    did.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref did))
                    {
                        break;
                    }
                    int req;
                    SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out req, IntPtr.Zero);
                    if (req <= 0)
                    {
                        continue;
                    }
                    IntPtr buf = Marshal.AllocHGlobal(req);
                    string path = null;
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref did, buf, req, out req, IntPtr.Zero))
                        {
                            path = Marshal.PtrToStringUni(buf + 4);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                    // Solo mandos Sony: VID 054C en la ruta (por USB "vid_054c", por Bluetooth "vid&0002054c"), así ni se abren otros dispositivos.
                    if (path == null || (path.IndexOf("054c", StringComparison.OrdinalIgnoreCase) < 0 || path.IndexOf("vid", StringComparison.OrdinalIgnoreCase) < 0))
                    {
                        continue;
                    }
                    seen++;
                    IntPtr h = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                    if (h == new IntPtr(-1))
                    {
                        continue;
                    }
                    try
                    {
                        opened++;
                        BatteryInfo b = ReadSony(h, path);
                        if (b != null)
                        {
                            list.Add(b);
                        }
                    }
                    finally
                    {
                        CloseHandle(h);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
            hidDiag = diag + seen + " interfaz(ces), " + opened + " abierta(s), " + list.Count + " con batería" + hidDetail;
            return list;
        }

        private static BatteryInfo ReadSony(IntPtr h, string path)
        {
            HIDD_ATTRIBUTES at = new HIDD_ATTRIBUTES();
            at.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
            if (!HidD_GetAttributes(h, ref at) || at.VendorID != 0x054C)
            {
                return null;
            }
            bool dualSense = at.ProductID == 0x0CE6 || at.ProductID == 0x0DF2;
            bool dualShock = at.ProductID == 0x05C4 || at.ProductID == 0x09CC;
            if (!dualSense && !dualShock)
            {
                return null;
            }
            string name = at.ProductID == 0x0DF2 ? "DualSense Edge" : (dualSense ? "DualSense" : "DualShock 4");

            // Por Bluetooth la ruta lleva el perfil HID (00001124...) y "vid&0002054c"; por USB, "vid_054c".
            bool bt = path.IndexOf("00001124", StringComparison.OrdinalIgnoreCase) >= 0 ||
                      path.IndexOf("vid&0002", StringComparison.OrdinalIgnoreCase) >= 0;

            // id del informe, longitud, posición del byte de estado, posición del primer byte de los sticks
            int id = dualSense ? (bt ? 0x31 : 0x01) : (bt ? 0x11 : 0x01);
            int len = bt ? 78 : 64;
            int statusIdx = dualSense ? (bt ? 54 : 53) : (bt ? 32 : 30);
            int stickIdx = dualSense ? (bt ? 2 : 1) : (bt ? 3 : 1);

            byte[] buf = new byte[len];
            buf[0] = (byte)id;
            bool got = HidD_GetInputReport(h, buf, len) && buf[0] == id && SticksPresent(buf, stickIdx);
            if (!got)
            {
                // Por Bluetooth los informes completos solo llegan por el flujo de datos: se espera uno.
                buf = ReadStream(h, len, (byte)id, 500);
                got = buf != null && SticksPresent(buf, stickIdx);
            }
            if (!got)
            {
                hidDetail += " [" + name + (bt ? " BT" : " USB") + ": sin informe 0x" + id.ToString("X2") + "]";
                return null;
            }

            byte st = buf[statusIdx];
            int percent;
            bool wired;
            if (dualSense)
            {
                int cap = st & 0x0F;
                int chg = (st >> 4) & 0x0F;
                percent = chg == 2 ? 100 : Math.Min(100, cap * 10 + 5);
                wired = chg == 1 || chg == 2;
            }
            else
            {
                int data = st & 0x0F;
                bool cable = (st & 0x10) != 0;
                percent = cable ? Math.Min(100, data * 100 / 11) : Math.Min(100, data * 100 / 8);
                wired = cable;
            }
            hidDetail += " [" + name + (bt ? " BT" : " USB") + " estado=0x" + st.ToString("X2") + "]";
            return new BatteryInfo
            {
                Name = name,
                Source = "HID",
                IsSony = true,
                Percent = percent,
                Level = LevelFromPercent(percent),
                Wired = wired
            };
        }

        // Un informe válido tiene los sticks cerca del centro (~0x80); todo ceros indica relleno.
        private static bool SticksPresent(byte[] buf, int idx)
        {
            for (int i = idx; i < idx + 4 && i < buf.Length; i++)
            {
                if (buf[i] != 0)
                {
                    return true;
                }
            }
            return false;
        }

        // Lee informes del mando (E/S solapada, con tiempo límite) hasta que llegue uno con el id pedido.
        private static byte[] ReadStream(IntPtr h, int len, byte wantId, int timeoutMs)
        {
            IntPtr ev = CreateEvent(IntPtr.Zero, true, false, null);
            IntPtr mem = Marshal.AllocHGlobal(len);
            int ovSize = 2 * IntPtr.Size + 8 + IntPtr.Size;
            IntPtr ov = Marshal.AllocHGlobal(ovSize);
            try
            {
                DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < end)
                {
                    for (int i = 0; i < ovSize; i++)
                    {
                        Marshal.WriteByte(ov, i, 0);
                    }
                    Marshal.WriteIntPtr(ov, 2 * IntPtr.Size + 8, ev);
                    ResetEvent(ev);
                    int read;
                    bool ok = ReadFile(h, mem, len, out read, ov);
                    if (!ok)
                    {
                        if (Marshal.GetLastWin32Error() != 997) // ERROR_IO_PENDING
                        {
                            return null;
                        }
                        if (WaitForSingleObject(ev, 150) != 0)
                        {
                            CancelIoEx(h, ov);
                            int dummy;
                            GetOverlappedResult(h, ov, out dummy, true);
                            continue;
                        }
                    }
                    int n;
                    if (!GetOverlappedResult(h, ov, out n, false) || n <= 0)
                    {
                        continue;
                    }
                    byte[] data = new byte[len];
                    Marshal.Copy(mem, data, 0, Math.Min(n, len));
                    if (data[0] == wantId)
                    {
                        return data;
                    }
                }
                return null;
            }
            finally
            {
                CloseHandle(ev);
                Marshal.FreeHGlobal(mem);
                Marshal.FreeHGlobal(ov);
            }
        }

        // ------------------------------------------------------------ SDL

        private static List<BatteryInfo> ReadSdl()
        {
            List<BatteryInfo> list = new List<BatteryInfo>();
            if (sdlFailed)
            {
                return list;
            }
            try
            {
                if (!sdlReady)
                {
                    // SDL cuenta las inicializaciones: si Playnite ya lo inició, esto solo suma una referencia.
                    if (SDL_InitSubSystem(0x200) != 0)
                    {
                        logger.Warn("SDL: no se pudo iniciar el subsistema de mandos: " + Marshal.PtrToStringAnsi(SDL_GetError()));
                        sdlFailed = true;
                        return list;
                    }
                    sdlReady = true;
                }
                int n = SDL_NumJoysticks();
                SDL_version ver;
                SDL_GetVersion(out ver);
                string diag = "SDL " + ver.major + "." + ver.minor + "." + ver.patch + ", " + n + " mando(s):";
                for (int i = 0; i < n; i++)
                {
                    IntPtr joy = SDL_JoystickOpen(i);
                    if (joy == IntPtr.Zero)
                    {
                        continue;
                    }
                    try
                    {
                        int level = SDL_JoystickCurrentPowerLevel(joy);
                        string name = Marshal.PtrToStringAnsi(SDL_JoystickName(joy)) ?? "Mando";
                        ushort vendor = 0;
                        try
                        {
                            vendor = SDL_JoystickGetVendor(joy);
                        }
                        catch
                        {
                        }
                        diag += " [" + name + " nivel=" + level + "]";
                        // -1 desconocido, 0 vacío, 1 bajo, 2 medio, 3 lleno, 4 por cable, 5 máximo
                        if (level < 0)
                        {
                            // Un mando Sony conectado al que SDL no da nivel está enchufado al cable (cargando).
                            if (vendor == 0x054C)
                            {
                                list.Add(new BatteryInfo { Name = name, Source = "SDL", IsSony = true, Wired = true, Level = 3 });
                            }
                            continue;
                        }
                        list.Add(new BatteryInfo
                        {
                            Name = name,
                            Source = "SDL",
                            IsSony = vendor == 0x054C,
                            Wired = level == 4,
                            Level = level == 4 || level == 5 ? 3 : Math.Min(3, level)
                        });
                    }
                    finally
                    {
                        SDL_JoystickClose(joy);
                    }
                }
                sdlDiag = diag;
                return list;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "SDL no disponible para leer la batería; se usa XInput.");
                sdlFailed = true;
                return list;
            }
        }

        // ------------------------------------------------------------ XInput

        private static List<BatteryInfo> ReadXInput()
        {
            List<BatteryInfo> list = new List<BatteryInfo>();
            try
            {
                for (uint i = 0; i < 4; i++)
                {
                    XINPUT_BATTERY_INFORMATION bi;
                    if (XInputGetBatteryInformation(i, 0, out bi) != 0)
                    {
                        continue;
                    }
                    // BatteryType: 0 desconectado, 1 cable, 2 alcalina, 3 NiMH, 4 desconocido
                    if (bi.BatteryType == 0 || bi.BatteryType == 4)
                    {
                        continue;
                    }
                    list.Add(new BatteryInfo
                    {
                        Name = "Mando " + (i + 1),
                        Source = "XInput",
                        Wired = bi.BatteryType == 1,
                        Level = bi.BatteryType == 1 ? 3 : Math.Min(3, (int)bi.BatteryLevel)
                    });
                }
            }
            catch
            {
            }
            return list;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_BATTERY_INFORMATION
        {
            public byte BatteryType;
            public byte BatteryLevel;
        }

        [DllImport("xinput1_4.dll")]
        private static extern uint XInputGetBatteryInformation(uint userIndex, byte devType, out XINPUT_BATTERY_INFORMATION info);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_InitSubSystem(uint flags);
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_NumJoysticks();
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetError();
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_GetVersion(out SDL_version ver);

        [StructLayout(LayoutKind.Sequential)]
        private struct SDL_version
        {
            public byte major;
            public byte minor;
            public byte patch;
        }
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_JoystickOpen(int index);
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_JoystickClose(IntPtr joystick);
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_JoystickCurrentPowerLevel(IntPtr joystick);
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_JoystickName(IntPtr joystick);
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern ushort SDL_JoystickGetVendor(IntPtr joystick);

        // ------------------------------------------------------------ HID / SetupAPI

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES attrs);
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetInputReport(IntPtr h, byte[] buffer, int length);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid iface, uint index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr h, IntPtr buffer, int toRead, out int read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateEvent(IntPtr attrs, bool manualReset, bool initial, string name);
        [DllImport("kernel32.dll")]
        private static extern bool ResetEvent(IntPtr ev);
        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(IntPtr h, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(IntPtr h, IntPtr overlapped, out int transferred, bool wait);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);
    }
}
