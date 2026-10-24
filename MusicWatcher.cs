using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Playnite.SDK;
using Windows.Media.Control;

namespace ConsoleOverlay
{
    // Lo que suena ahora mismo, tal como lo publica el reproductor al control de medios de Windows.
    internal sealed class MusicInfo
    {
        public bool HasSession;
        public bool Playing;
        public string Title = "";
        public string Artist = "";
        public string Album = "";
        public string Source = "";
        public string SourceId = "";   // id completo de la aplicación (para saber si sigue abierta)
        public double PositionSec;     // posición en el instante UpdatedUtc
        public double DurationSec;     // 0 si el reproductor no la da (directos, etc.)
        public double Rate = 1.0;
        public bool CanShuffle, CanRepeat, Shuffle;
        public int Repeat;             // 0 sin repetir, 1 lista, 2 canción
        public DateTime UpdatedUtc = DateTime.UtcNow;
        public BitmapSource Art;

        // Posición actual, extrapolada: no hace falta preguntar al reproductor cada segundo.
        public double PositionNow()
        {
            double p = PositionSec;
            if (Playing)
            {
                p += Math.Max(0, (DateTime.UtcNow - UpdatedUtc).TotalSeconds) * Rate;
            }
            if (DurationSec > 0)
            {
                p = Math.Min(p, DurationSec);
            }
            return Math.Max(0, p);
        }

        public MusicInfo Clone()
        {
            return (MusicInfo)MemberwiseClone();
        }
    }

    // Lee y controla el reproductor de música del sistema (Spotify, SimpMusic, YouTube Music en el navegador, VLC...)
    // con el control de medios de Windows 10/11. No usa red ni cuentas, y solo reacciona a avisos del sistema.
    internal sealed class MusicWatcher
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        public static readonly MusicWatcher Instance = new MusicWatcher();

        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private GlobalSystemMediaTransportControlsSessionManager manager;
        private volatile GlobalSystemMediaTransportControlsSession session;
        private volatile MusicInfo current = new MusicInfo();
        private volatile string preferred = "";
        private volatile bool again;
        private volatile bool started;
        private volatile bool dead;
        private string lastArtKey;
        private BitmapSource lastArt;

        public MusicInfo Current { get { return current; } }
        public bool Available { get { return started && !dead; } }

        // Se avisa desde un hilo secundario: quien escuche debe pasar a su propio hilo.
        public event Action Changed;

        public void Start(string preferredPlayer)
        {
            preferred = preferredPlayer ?? "";
            if (started)
            {
                Kick();
                return;
            }
            started = true;
            Task.Run(async () =>
            {
                try
                {
                    await InitAsync();
                }
                catch (Exception ex)
                {
                    dead = true;
                    logger.Info("Música: el control de medios de Windows no está disponible (" + ex.GetType().Name + "). Se desactiva.");
                }
            });
        }

        public void SetPreferred(string preferredPlayer)
        {
            preferred = preferredPlayer ?? "";
            if (Available)
            {
                Kick();
            }
        }

        private async Task InitAsync()
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
            manager.SessionsChanged += (s, e) => Kick();
            manager.CurrentSessionChanged += (s, e) => Kick();
            logger.Info("Música: control de medios de Windows iniciado.");
            await RefreshAsync();
        }

        private void Kick()
        {
            if (dead)
            {
                return;
            }
            Task.Run(async () =>
            {
                try
                {
                    await RefreshAsync();
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "Música: error al leer el reproductor");
                }
            });
        }

        // Varias señales seguidas se juntan en una sola lectura.
        private async Task RefreshAsync()
        {
            if (!await gate.WaitAsync(0))
            {
                again = true;
                return;
            }
            try
            {
                do
                {
                    again = false;
                    await RefreshCoreAsync();
                }
                while (again);
            }
            finally
            {
                gate.Release();
            }
        }

        private static bool IsPlaying(GlobalSystemMediaTransportControlsSession s)
        {
            try
            {
                var pb = s.GetPlaybackInfo();
                return pb != null && pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch
            {
                return false;
            }
        }

        private GlobalSystemMediaTransportControlsSession Pick()
        {
            var m = manager;
            if (m == null)
            {
                return null;
            }
            var list = m.GetSessions().ToList();
            if (list.Count == 0)
            {
                return null;
            }
            string pref = preferred;
            Func<GlobalSystemMediaTransportControlsSession, bool> isPref = s =>
                !string.IsNullOrWhiteSpace(pref) &&
                (s.SourceAppUserModelId ?? "").IndexOf(pref.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
            return list.FirstOrDefault(s => isPref(s) && IsPlaying(s))
                   ?? list.FirstOrDefault(isPref)
                   ?? list.FirstOrDefault(IsPlaying)
                   ?? m.GetCurrentSession()
                   ?? list[0];
        }

        private bool propsWarned;

        private async Task RefreshCoreAsync()
        {
            GlobalSystemMediaTransportControlsSession pick = Pick();
            if (!ReferenceEquals(pick, session))
            {
                Detach(session);
                session = pick;
                Attach(pick);
                lastArtKey = null;
                lastArt = null;
            }
            if (pick == null)
            {
                current = new MusicInfo();
                Raise();
                return;
            }

            MusicInfo info = new MusicInfo { HasSession = true, Source = Pretty(pick.SourceAppUserModelId), SourceId = pick.SourceAppUserModelId ?? "" };
            var pb = pick.GetPlaybackInfo();
            if (pb != null)
            {
                info.Playing = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                double? rate = pb.PlaybackRate;
                info.Rate = rate.HasValue && rate.Value > 0 ? rate.Value : 1.0;
                try
                {
                    var ctl = pb.Controls;
                    if (ctl != null)
                    {
                        info.CanShuffle = ctl.IsShuffleEnabled;
                        info.CanRepeat = ctl.IsRepeatEnabled;
                    }
                    info.Shuffle = pb.IsShuffleActive == true;
                    if (pb.AutoRepeatMode.HasValue)
                    {
                        info.Repeat = pb.AutoRepeatMode.Value == Windows.Media.MediaPlaybackAutoRepeatMode.Track ? 2 :
                            (pb.AutoRepeatMode.Value == Windows.Media.MediaPlaybackAutoRepeatMode.List ? 1 : 0);
                    }
                }
                catch
                {
                }
            }
            var tl = pick.GetTimelineProperties();
            if (tl != null)
            {
                info.PositionSec = Math.Max(0, (tl.Position - tl.StartTime).TotalSeconds);
                info.DurationSec = Math.Max(0, (tl.EndTime - tl.StartTime).TotalSeconds);
                DateTime upd = tl.LastUpdatedTime.UtcDateTime;
                info.UpdatedUtc = upd < DateTime.UtcNow.AddDays(-1) ? DateTime.UtcNow : upd;
            }
            GlobalSystemMediaTransportControlsSessionMediaProperties props = null;
            try
            {
                props = await pick.TryGetMediaPropertiesAsync().AsTask();
            }
            catch (Exception ex)
            {
                // Windows a veces rechaza la lectura desde otro hilo (RPC_E_WRONG_THREAD): se conservan los datos anteriores.
                if (!propsWarned)
                {
                    propsWarned = true;
                    logger.Info("Música: no se pudieron leer título y carátula (" + ex.GetType().Name + "); se usan los datos anteriores");
                }
                MusicInfo prev = current;
                if (prev.HasSession && prev.SourceId == info.SourceId)
                {
                    info.Title = prev.Title;
                    info.Artist = prev.Artist;
                    info.Album = prev.Album;
                    info.Art = prev.Art;
                }
            }
            if (props != null)
            {
                info.Title = props.Title ?? "";
                info.Artist = props.Artist ?? "";
                info.Album = props.AlbumTitle ?? "";
                string key = info.Title + "|" + info.Artist + "|" + info.Album;
                if (key != lastArtKey)
                {
                    lastArt = await LoadArtAsync(props);
                    lastArtKey = key;
                }
                info.Art = lastArt;
            }
            current = info;
            Raise();
        }

        private static async Task<BitmapSource> LoadArtAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
        {
            try
            {
                var thumb = props.Thumbnail;
                if (thumb == null)
                {
                    return null;
                }
                using (var rs = await thumb.OpenReadAsync().AsTask())
                using (Stream st = rs.AsStreamForRead())
                {
                    MemoryStream ms = new MemoryStream();
                    await st.CopyToAsync(ms);
                    if (ms.Length == 0)
                    {
                        return null;
                    }
                    ms.Position = 0;
                    BitmapImage bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.DecodePixelWidth = 240;
                    bi.StreamSource = ms;
                    bi.EndInit();
                    bi.Freeze();
                    return bi;
                }
            }
            catch (Exception ex)
            {
                logger.Info("Música: sin carátula (" + ex.GetType().Name + ")");
                return null;
            }
        }

        private void Attach(GlobalSystemMediaTransportControlsSession s)
        {
            if (s == null)
            {
                return;
            }
            s.PlaybackInfoChanged += OnChanged;
            s.MediaPropertiesChanged += OnChanged;
            s.TimelinePropertiesChanged += OnTimeline;
        }

        private void Detach(GlobalSystemMediaTransportControlsSession s)
        {
            if (s == null)
            {
                return;
            }
            try
            {
                s.PlaybackInfoChanged -= OnChanged;
                s.MediaPropertiesChanged -= OnChanged;
                s.TimelinePropertiesChanged -= OnTimeline;
            }
            catch
            {
            }
        }

        private void OnChanged(GlobalSystemMediaTransportControlsSession s, object e)
        {
            Kick();
        }

        // La barra de progreso se calcula sola; solo se relee si el reproductor salta de sitio (adelantar, cambiar de pista).
        private void OnTimeline(GlobalSystemMediaTransportControlsSession s, object e)
        {
            try
            {
                MusicInfo cur = current;
                var tl = s.GetTimelineProperties();
                if (tl == null || !cur.HasSession)
                {
                    return;
                }
                double pos = Math.Max(0, (tl.Position - tl.StartTime).TotalSeconds);
                double dur = Math.Max(0, (tl.EndTime - tl.StartTime).TotalSeconds);
                if (Math.Abs(pos - cur.PositionNow()) > 1.5 || Math.Abs(dur - cur.DurationSec) > 1.0)
                {
                    Kick();
                }
            }
            catch
            {
            }
        }

        private void Raise()
        {
            Action a = Changed;
            if (a != null)
            {
                try
                {
                    a();
                }
                catch
                {
                }
            }
        }

        private static string Pretty(string appId)
        {
            string s = appId ?? "";
            if (s.IndexOf("spotify", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "SPOTIFY";
            }
            int bang = s.LastIndexOf('!');
            if (bang >= 0 && bang < s.Length - 1)
            {
                s = s.Substring(bang + 1);
            }
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(0, s.Length - 4);
            }
            int us = s.IndexOf('_');
            if (us > 0)
            {
                s = s.Substring(0, us);
            }
            s = s.Trim().ToUpperInvariant();
            return s.Length > 20 ? s.Substring(0, 20) : s;
        }

        // ---- Órdenes ----

        public void PlayPause()
        {
            // Respuesta inmediata en pantalla; el reproductor confirmará enseguida.
            MusicInfo cur = current;
            if (cur.HasSession)
            {
                MusicInfo n = cur.Clone();
                n.PositionSec = cur.PositionNow();
                n.UpdatedUtc = DateTime.UtcNow;
                n.Playing = !cur.Playing;
                current = n;
                Raise();
            }
            Send(s => s.TryTogglePlayPauseAsync());
        }

        // Salta segundos dentro de la canción (si el reproductor no da duración, no hace nada).
        public void Seek(double deltaSec)
        {
            MusicInfo cur = current;
            if (!cur.HasSession || cur.DurationSec <= 0)
            {
                return;
            }
            double target = Math.Max(0, Math.Min(cur.DurationSec - 1, cur.PositionNow() + deltaSec));
            MusicInfo n = cur.Clone();
            n.PositionSec = target;
            n.UpdatedUtc = DateTime.UtcNow;
            current = n;
            Raise();
            long ticks = (long)(target * 10000000.0);
            Send(s => s.TryChangePlaybackPositionAsync(ticks));
        }

        public void ToggleShuffle()
        {
            MusicInfo cur = current;
            if (!cur.HasSession || !cur.CanShuffle)
            {
                return;
            }
            bool v = !cur.Shuffle;
            MusicInfo n = cur.Clone();
            n.Shuffle = v;
            current = n;
            Raise();
            Send(s => s.TryChangeShuffleActiveAsync(v));
        }

        // Sin repetir -> repetir lista -> repetir canción -> sin repetir.
        public void CycleRepeat()
        {
            MusicInfo cur = current;
            if (!cur.HasSession || !cur.CanRepeat)
            {
                return;
            }
            int next = (cur.Repeat + 1) % 3;
            MusicInfo n = cur.Clone();
            n.Repeat = next;
            current = n;
            Raise();
            Windows.Media.MediaPlaybackAutoRepeatMode mode = next == 2 ? Windows.Media.MediaPlaybackAutoRepeatMode.Track :
                (next == 1 ? Windows.Media.MediaPlaybackAutoRepeatMode.List : Windows.Media.MediaPlaybackAutoRepeatMode.None);
            Send(s => s.TryChangeAutoRepeatModeAsync(mode));
        }

        public void Next()
        {
            Send(s => s.TrySkipNextAsync());
        }

        public void Previous()
        {
            Send(s => s.TrySkipPreviousAsync());
        }

        private void Send(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> f)
        {
            GlobalSystemMediaTransportControlsSession s = session;
            if (s == null)
            {
                return;
            }
            Task.Run(async () =>
            {
                try
                {
                    bool ok = await f(s).AsTask();
                    if (!ok)
                    {
                        logger.Info("Música: el reproductor respondió que no a la orden");
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "Música: el reproductor no aceptó la orden");
                }
                Kick();
                // Algunos reproductores (Spotify) aplican el cambio un instante después: se vuelve a leer su estado.
                await Task.Delay(700);
                Kick();
            });
        }
    }
}
