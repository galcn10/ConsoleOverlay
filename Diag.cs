using System.Diagnostics;
using Playnite.SDK;

namespace ConsoleOverlay
{
    // Mide cuánto tardan las acciones principales y lo deja en el log. Solo actúa si el ajuste «Modo diagnóstico» está activo.
    internal static class Diag
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        public static volatile bool Enabled;
        private static long t0;

        public static void Begin()
        {
            t0 = Enabled ? Stopwatch.GetTimestamp() : 0;
        }

        // Cierra la medición iniciada con Begin (solo la primera vez que se llama tras cada Begin).
        public static void Done(string what)
        {
            long s = t0;
            if (!Enabled || s == 0)
            {
                return;
            }
            t0 = 0;
            Log(what, (Stopwatch.GetTimestamp() - s) * 1000 / Stopwatch.Frequency);
        }

        public static void Log(string what, long ms)
        {
            if (Enabled)
            {
                logger.Info("[Diagnóstico] " + what + ": " + ms + " ms");
            }
        }
    }
}
