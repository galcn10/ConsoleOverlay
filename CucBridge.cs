using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;

namespace ConsoleOverlay
{
    // Una descarga de Console Update Center (fila de status.json).
    internal sealed class CucItem
    {
        public string Name { get; set; }
        public string Platform { get; set; }
        public string GameId { get; set; }
        public string Status { get; set; }          // Pending, Downloading o Paused
        public double Progress { get; set; }        // 0..1
        public double BytesPerSecond { get; set; }
        public double SecondsLeft { get; set; }
    }

    internal sealed class CucStatus
    {
        public int Version { get; set; }
        public string Updated { get; set; }
        public int Pending { get; set; }
        public List<CucItem> Items { get; set; }
    }

    // Una actualización terminada mientras se jugaba.
    internal sealed class CucDone
    {
        public string Name;
        public DateTime At;
    }

    // Puente con el plugin Console Update Center. Solo por reflexión (sin referencia a su dll) y solo si está instalado
    // y su IntegrationApiVersion es 1. Si no está, todo devuelve "no disponible" y el overlay se comporta como siempre.
    internal sealed class CucBridge
    {
        private static readonly Guid PluginId = new Guid("6f3c2a7e-4b1d-4e8a-9c55-2d7f0b9a1e63");
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly IPlayniteAPI api;
        private object plugin;
        private bool searched;
        private PropertyInfo pPanelOpen;
        private PropertyInfo pStatusPath;
        private MethodInfo mOpen;
        private MethodInfo mClose;

        private readonly object sync = new object();
        private CucStatus last;
        private DateTime lastReadUtc = DateTime.MinValue;
        private string lastStamp;
        private Dictionary<string, CucItem> previous;
        private readonly List<CucDone> done = new List<CucDone>();

        public CucBridge(IPlayniteAPI api)
        {
            this.api = api;
        }

        // true si Console Update Center está instalado y es compatible.
        public bool Available
        {
            get
            {
                Locate();
                return plugin != null;
            }
        }

        private void Locate()
        {
            if (searched)
            {
                return;
            }
            lock (sync)
            {
                if (searched)
                {
                    return;
                }
                try
                {
                    object found = null;
                    foreach (var p in api.Addons.Plugins)
                    {
                        if (p != null && p.Id == PluginId)
                        {
                            found = p;
                            break;
                        }
                    }
                    if (found != null)
                    {
                        Type t = found.GetType();
                        PropertyInfo ver = t.GetProperty("IntegrationApiVersion");
                        int v = ver != null ? Convert.ToInt32(ver.GetValue(found, null)) : 0;
                        if (v != 1)
                        {
                            logger.Info("Console Update Center encontrado, pero su versión de API (" + v + ") no es compatible: no se integra.");
                        }
                        else
                        {
                            pPanelOpen = t.GetProperty("IsPanelOpen");
                            pStatusPath = t.GetProperty("StatusFilePath");
                            mOpen = t.GetMethod("OpenPanel", new[] { typeof(Guid?), typeof(Action) });
                            mClose = t.GetMethod("ClosePanel", Type.EmptyTypes);
                            if (pPanelOpen != null && pStatusPath != null && mOpen != null)
                            {
                                plugin = found;
                                logger.Info("Console Update Center detectado (API 1): integración activa.");
                            }
                            else
                            {
                                logger.Info("Console Update Center: faltan miembros de la API; no se integra.");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "No se pudo comprobar Console Update Center");
                    plugin = null;
                }
                searched = true;
            }
        }

        // true mientras su panel está abierto: el overlay debe ignorar el mando por completo.
        public bool IsPanelOpen
        {
            get
            {
                try
                {
                    if (!Available)
                    {
                        return false;
                    }
                    return (bool)pPanelOpen.GetValue(plugin, null);
                }
                catch
                {
                    return false;
                }
            }
        }

        // Abre el panel de descargas (con el juego actual seleccionado). onClosed se llama al cerrarlo. false si no se pudo.
        public bool OpenPanel(Guid? gameId, Action onClosed)
        {
            try
            {
                if (!Available)
                {
                    return false;
                }
                mOpen.Invoke(plugin, new object[] { gameId, onClosed });
                return true;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo abrir el panel de Console Update Center");
                return false;
            }
        }

        public void ClosePanel()
        {
            try
            {
                if (Available && mClose != null)
                {
                    mClose.Invoke(plugin, null);
                }
            }
            catch
            {
            }
        }

        // Lee status.json (como mucho cada 2 s, o al cambiar el archivo). null si no hay datos.
        public CucStatus Read()
        {
            try
            {
                if (!Available)
                {
                    return null;
                }
                string path = (string)pStatusPath.GetValue(plugin, null);
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return null;
                }
                FileInfo fi = new FileInfo(path);
                string stamp = fi.LastWriteTimeUtc.Ticks + ":" + fi.Length;
                lock (sync)
                {
                    if (last != null && stamp == lastStamp && (DateTime.UtcNow - lastReadUtc).TotalSeconds < 2)
                    {
                        return last;
                    }
                    if (last != null && stamp == lastStamp)
                    {
                        lastReadUtc = DateTime.UtcNow;
                        return last;
                    }
                }
                string json;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader sr = new StreamReader(fs))
                {
                    json = sr.ReadToEnd();
                }
                CucStatus st = Serialization.FromJson<CucStatus>(json);
                if (st == null)
                {
                    return null;
                }
                if (st.Items == null)
                {
                    st.Items = new List<CucItem>();
                }
                lock (sync)
                {
                    DetectDone(st);
                    last = st;
                    lastStamp = stamp;
                    lastReadUtc = DateTime.UtcNow;
                }
                return st;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo leer status.json de Console Update Center");
                return null;
            }
        }

        private static string Key(CucItem i)
        {
            return (i.GameId ?? "") + "|" + (i.Name ?? "");
        }

        // status.json solo lista lo pendiente: lo que estaba descargando y desaparece se da por completado.
        // (Mientras no haya un evento en la API de CUC, es la única forma de enterarse desde fuera.)
        private void DetectDone(CucStatus st)
        {
            Dictionary<string, CucItem> now = new Dictionary<string, CucItem>();
            foreach (CucItem i in st.Items)
            {
                now[Key(i)] = i;
            }
            if (previous != null)
            {
                foreach (KeyValuePair<string, CucItem> kv in previous)
                {
                    if (!now.ContainsKey(kv.Key) && string.Equals(kv.Value.Status, "Downloading", StringComparison.OrdinalIgnoreCase) &&
                        kv.Value.Progress >= 0.6)
                    {
                        done.Add(new CucDone { Name = kv.Value.Name, At = DateTime.Now });
                        logger.Info("Console Update Center: actualización terminada: " + kv.Value.Name);
                    }
                }
            }
            previous = now;
        }

        // Actualizaciones terminadas aún no vistas en el menú.
        public List<CucDone> Done()
        {
            lock (sync)
            {
                return done.ToList();
            }
        }

        public void ClearDone()
        {
            lock (sync)
            {
                done.Clear();
            }
        }

        public void Forget()
        {
            lock (sync)
            {
                previous = null;
                last = null;
                lastStamp = null;
            }
        }

        // Descargas en curso (Downloading primero; si no hay, las pausadas).
        public static List<CucItem> Active(CucStatus st)
        {
            if (st == null || st.Items == null)
            {
                return new List<CucItem>();
            }
            return st.Items
                .Where(i => string.Equals(i.Status, "Downloading", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public static CucItem ForGame(CucStatus st, Game g)
        {
            if (st == null || st.Items == null || g == null)
            {
                return null;
            }
            string id = g.Id.ToString();
            return st.Items.FirstOrDefault(i => string.Equals(i.GameId, id, StringComparison.OrdinalIgnoreCase));
        }

        public static string FormatEta(double seconds)
        {
            if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
            {
                return null;
            }
            if (seconds < 90)
            {
                return Loc.T("{0} s", (int)Math.Round(seconds));
            }
            double min = seconds / 60.0;
            if (min < 90)
            {
                return Loc.T("{0} min", (int)Math.Round(min));
            }
            double h = min / 60.0;
            return Loc.T("{0} h", Math.Round(h, 1).ToString("0.#"));
        }
    }
}
