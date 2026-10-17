using System;
using System.Collections.Generic;
using System.Diagnostics;
using Playnite.SDK;

namespace ConsoleOverlay
{
    // ----------------------------------------------------------------------
    // Registro de procesos pausados: cada proceso se pausa y se reanuda una
    // sola vez (idempotente) y no se reanuda un PID reutilizado por Windows.
    // Idea inspirada en GameProcessSuspender de PlayniteNextOverlay (MIT).
    // ----------------------------------------------------------------------
    internal static class PauseTracker
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly object sync = new object();
        private static readonly Dictionary<int, long> paused = new Dictionary<int, long>();

        private static long StartTicks(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    return p.StartTime.Ticks;
                }
            }
            catch
            {
                return 0;
            }
        }

        public static bool Suspend(int pid, out string reason)
        {
            lock (sync)
            {
                if (paused.ContainsKey(pid))
                {
                    reason = null;
                    return true;
                }
                if (!NativeMethods.SuspendProcess(pid, out reason))
                {
                    return false;
                }
                paused[pid] = StartTicks(pid);
                return true;
            }
        }

        public static void Resume(IEnumerable<int> pids)
        {
            foreach (int pid in new List<int>(pids))
            {
                ResumeOne(pid);
            }
        }

        public static void ResumeAll()
        {
            List<int> all;
            lock (sync)
            {
                all = new List<int>(paused.Keys);
            }
            foreach (int pid in all)
            {
                ResumeOne(pid);
            }
        }

        private static void ResumeOne(int pid)
        {
            try
            {
                lock (sync)
                {
                    long start;
                    if (!paused.TryGetValue(pid, out start))
                    {
                        return;
                    }
                    paused.Remove(pid);

                    long now = StartTicks(pid);
                    if (start != 0 && now != start)
                    {
                        logger.Info("El PID " + pid + " ya no es el mismo proceso (terminó o se reutilizó); no se reanuda.");
                        return;
                    }
                    NativeMethods.ResumeProcess(pid);
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo reanudar el PID " + pid);
            }
        }

        // Olvida los procesos que ya no existen.
        public static void Prune()
        {
            lock (sync)
            {
                foreach (int pid in new List<int>(paused.Keys))
                {
                    bool alive;
                    try
                    {
                        using (Process p = Process.GetProcessById(pid))
                        {
                            alive = !p.HasExited;
                        }
                    }
                    catch
                    {
                        alive = false;
                    }
                    if (!alive)
                    {
                        paused.Remove(pid);
                    }
                }
            }
        }
    }

    // ----------------------------------------------------------------------
    // Procesos que nunca se pausan: Windows, navegadores, grabadores,
    // overlays y lanzadores.
    // ----------------------------------------------------------------------
    internal static class ProcessGuard
    {
        private static readonly HashSet<string> Denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Windows
            "explorer", "dwm", "csrss", "winlogon", "services", "svchost", "lsass", "smss", "wininit",
            "taskmgr", "sihost", "fontdrvhost", "ctfmon", "searchhost", "searchapp", "startmenuexperiencehost",
            "shellexperiencehost", "runtimebroker", "applicationframehost", "audiodg", "textinputhost",
            "systemsettings", "cmd", "conhost", "powershell", "pwsh", "windowsterminal",
            // Navegadores
            "msedge", "chrome", "firefox", "brave", "opera", "vivaldi",
            // Grabadores y overlays
            "obs64", "obs32", "nvidia share", "nvidia overlay", "nvcontainer", "nvsphelper64", "nvsphelper",
            "gamebar", "gamebarft", "gamebarpresencewriter", "discord", "overwolf", "medal", "msiafterburner",
            "rtss", "radeonsoftware", "amdrsserv", "joytokey", "joytokey_en", "steelseriesgg",
            // Lanzadores
            "steam", "steamwebhelper", "steamservice", "epicgameslauncher", "epicwebhelper", "galaxyclient",
            "galaxyclient helper", "upc", "ubisoftconnect", "eadesktop", "ealauncher", "origin", "battle.net",
            "playnite", "playnite.desktopapp", "playnite.fullscreenapp", "toolbox"
        };

        public static bool IsDeniedName(string processName)
        {
            return !string.IsNullOrEmpty(processName) && Denied.Contains(processName);
        }

        // PID del proceso de la ventana en primer plano (0 si no hay).
        public static int GetForegroundPid()
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero)
            {
                return 0;
            }
            uint pid;
            NativeMethods.GetWindowThreadProcessId(fg, out pid);
            return (int)pid;
        }
    }
}
