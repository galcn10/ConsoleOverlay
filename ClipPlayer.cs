using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Playnite.SDK;

namespace ConsoleOverlay
{
    // Duración de un clip (igual que MediaElement.NaturalDuration para no cambiar el visor).
    internal struct ClipLength
    {
        public bool HasTimeSpan;
        public TimeSpan TimeSpan;
    }

    // Reproductor de clips basado en mpv (proceso propio). Dibuja el vídeo dentro de la ventana del plugin
    // y controla la reproducción por IPC (named pipe, JSON). Así el audio y los códecs no dependen de Windows.
    internal sealed class ClipPlayer : HwndHost
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly System.Collections.Generic.List<ClipPlayer> live = new System.Collections.Generic.List<ClipPlayer>();

        private static readonly Regex DataRx = new Regex("\"data\":(null|true|false|-?[0-9.]+(?:[eE][-+]?[0-9]+)?|\"[^\"]*\")", RegexOptions.Compiled);
        private static readonly Regex EventRx = new Regex("\"event\":\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex ReasonRx = new Regex("\"reason\":\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex ReqRx = new Regex("\"request_id\":([0-9]+)", RegexOptions.Compiled);

        private const int ReqDuration = 2;
        private const int ReqWidth = 6;
        private const int ReqHeight = 7;
        private const int ReqAudio = 5;
        private const int ReqTime = 1;

        public event EventHandler MediaOpened;
        public event EventHandler MediaFailed;
        public event EventHandler MediaEnded;

        public bool ScrubbingEnabled;
        public double Volume = 1.0;

        public int NaturalVideoWidth { get; private set; }
        public int NaturalVideoHeight { get; private set; }
        public bool HasAudio { get; private set; }

        public ClipLength NaturalDuration
        {
            get
            {
                ClipLength l = new ClipLength();
                lock (sync)
                {
                    l.HasTimeSpan = durationSec > 0;
                    l.TimeSpan = TimeSpan.FromSeconds(durationSec);
                }
                return l;
            }
        }

        public TimeSpan Position
        {
            get
            {
                lock (sync)
                {
                    return TimeSpan.FromSeconds(timePos);
                }
            }
            set
            {
                lock (sync)
                {
                    timePos = Math.Max(0, value.TotalSeconds);
                }
                Send("{\"command\":[\"seek\"," + value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"absolute+exact\"]}");
            }
        }

        // Ruta del clip. Asignar null lo para.
        public Uri Source
        {
            get { return source; }
            set
            {
                source = value;
                if (value == null)
                {
                    Send("{\"command\":[\"stop\"]}");
                    return;
                }
                if (connected)
                {
                    Send("{\"command\":[\"loadfile\",\"" + Escape(value.LocalPath) + "\",\"replace\"]}");
                }
            }
        }

        private Uri source;
        private IntPtr childHwnd;
        private Process proc;
        private NamedPipeClientStream pipe;
        // Comandos pendientes: la interfaz solo encola; un hilo propio escribe en el pipe (nunca bloquea el menú).
        private readonly System.Collections.Concurrent.BlockingCollection<string> outQueue = new System.Collections.Concurrent.BlockingCollection<string>();
        private volatile bool connected;
        private volatile bool stopping;
        private bool playRequested;
        private double timePos;
        private double durationSec;
        private readonly object sync = new object();
        private DispatcherTimer poll;
        private string pipeName;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h,
                                                    IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hwnd);

        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            int w = (int)Math.Max(1, ActualWidth);
            int h = (int)Math.Max(1, ActualHeight);
            childHwnd = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
                                       0, 0, w, h, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (playRequested)
            {
                Dispatcher.BeginInvoke(new Action(StartProcess));
            }
            return new HandleRef(this, childHwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            Shutdown();
            try
            {
                DestroyWindow(hwnd.Handle);
            }
            catch
            {
            }
            childHwnd = IntPtr.Zero;
        }

        public void Play()
        {
            playRequested = true;
            if (proc == null && childHwnd != IntPtr.Zero)
            {
                StartProcess();
            }
            else
            {
                Send("{\"command\":[\"set\",\"pause\",\"no\"]}");
            }
        }

        public void Pause()
        {
            Send("{\"command\":[\"set\",\"pause\",\"yes\"]}");
        }

        public void Stop()
        {
            Send("{\"command\":[\"stop\"]}");
        }

        private static string ExePath()
        {
            string dir = Path.GetDirectoryName(typeof(ClipPlayer).Assembly.Location);
            return Path.Combine(dir ?? "", "mpv", "mpv.exe");
        }

        private void StartProcess()
        {
            if (proc != null || childHwnd == IntPtr.Zero)
            {
                return;
            }
            string exe = ExePath();
            if (!File.Exists(exe))
            {
                logger.Warn("No se encuentra mpv en: " + exe);
                RaiseFailed();
                return;
            }
            try
            {
                pipeName = "ConsoleOverlay-mpv-" + Process.GetCurrentProcess().Id + "-" + Environment.TickCount;
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--no-config --no-terminal --idle=yes --keep-open=yes --force-window=yes" +
                                " --wid=" + childHwnd.ToInt64() +
                                " --osc=no --osd-level=0 --no-osc --no-input-default-bindings --input-vo-keyboard=no" +
                                " --input-cursor=no --no-border --hwdec=auto --volume=100 --keep-open-pause=no" +
                                " --input-ipc-server=\\\\.\\pipe\\" + pipeName,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                proc = Process.Start(psi);
                lock (live)
                {
                    live.Add(this);
                }
                logger.Info("mpv iniciado (PID " + proc.Id + ")");
                Thread t = new Thread(() => ConnectAndRead(pipeName)) { IsBackground = true, Name = "mpv-ipc" };
                t.Start();
                poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                poll.Tick += (s, e) =>
                {
                    if (connected)
                    {
                        Send("{\"command\":[\"get_property\",\"time-pos\"],\"request_id\":" + ReqTime + "}");
                    }
                };
                poll.Start();
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "No se pudo iniciar mpv");
                RaiseFailed();
            }
        }

        private void ConnectAndRead(string name)
        {
            NamedPipeClientStream ps = null;
            try
            {
                for (int i = 0; i < 100 && !stopping; i++)
                {
                    try
                    {
                        // Asíncrono: lectura y escritura pueden ir a la vez sin bloquearse la una a la otra.
                        ps = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                        ps.Connect(100);
                        break;
                    }
                    catch
                    {
                        if (ps != null)
                        {
                            ps.Dispose();
                            ps = null;
                        }
                        Thread.Sleep(50);
                    }
                }
                if (ps == null || !ps.IsConnected)
                {
                    logger.Warn("No se pudo conectar con mpv por IPC");
                    Dispatcher.BeginInvoke(new Action(RaiseFailed));
                    return;
                }
                pipe = ps;
                Thread wt = new Thread(() => WriteLoop(ps)) { IsBackground = true, Name = "mpv-ipc-write" };
                wt.Start();
                connected = true;
                Send("{\"command\":[\"observe_property\",1,\"time-pos\"]}");
                if (source != null)
                {
                    Send("{\"command\":[\"loadfile\",\"" + Escape(source.LocalPath) + "\",\"replace\"]}");
                }
                using (StreamReader reader = new StreamReader(ps, Encoding.UTF8))
                {
                    string line;
                    while (!stopping && (line = reader.ReadLine()) != null)
                    {
                        HandleLine(line);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Conexión con mpv interrumpida");
            }
            finally
            {
                connected = false;
            }
        }

        private void HandleLine(string line)
        {
            Match ev = EventRx.Match(line);
            if (ev.Success)
            {
                switch (ev.Groups[1].Value)
                {
                    case "file-loaded":
                        Send("{\"command\":[\"get_property\",\"duration\"],\"request_id\":" + ReqDuration + "}");
                        Send("{\"command\":[\"get_property\",\"width\"],\"request_id\":" + ReqWidth + "}");
                        Send("{\"command\":[\"get_property\",\"height\"],\"request_id\":" + ReqHeight + "}");
                        Send("{\"command\":[\"get_property\",\"audio-codec-name\"],\"request_id\":" + ReqAudio + "}");
                        break;
                    case "end-file":
                        Match r = ReasonRx.Match(line);
                        string reason = r.Success ? r.Groups[1].Value : "";
                        if (reason == "error")
                        {
                            Dispatcher.BeginInvoke(new Action(RaiseFailed));
                        }
                        else if (reason == "eof")
                        {
                            Dispatcher.BeginInvoke(new Action(() => { if (MediaEnded != null) MediaEnded(this, EventArgs.Empty); }));
                        }
                        break;
                }
                return;
            }

            Match req = ReqRx.Match(line);
            if (!req.Success)
            {
                return;
            }
            int id = int.Parse(req.Groups[1].Value);
            Match d = DataRx.Match(line);
            string data = d.Success ? d.Groups[1].Value : "null";
            double num;
            switch (id)
            {
                case ReqTime:
                    if (double.TryParse(data, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out num))
                    {
                        lock (sync)
                        {
                            timePos = num;
                        }
                    }
                    break;
                case ReqDuration:
                    if (double.TryParse(data, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out num))
                    {
                        lock (sync)
                        {
                            durationSec = num;
                        }
                    }
                    break;
                case ReqWidth:
                    if (double.TryParse(data, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out num))
                    {
                        NaturalVideoWidth = (int)num;
                    }
                    break;
                case ReqHeight:
                    if (double.TryParse(data, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out num))
                    {
                        NaturalVideoHeight = (int)num;
                    }
                    break;
                case ReqAudio:
                    // La última petición de la tanda: aquí ya están listos duración y tamaño.
                    HasAudio = data != "null";
                    Dispatcher.BeginInvoke(new Action(() => { if (MediaOpened != null) MediaOpened(this, EventArgs.Empty); }));
                    break;
            }
        }

        private void RaiseFailed()
        {
            if (MediaFailed != null)
            {
                MediaFailed(this, EventArgs.Empty);
            }
        }

        private void WriteLoop(NamedPipeClientStream ps)
        {
            try
            {
                using (StreamWriter sw = new StreamWriter(ps, new UTF8Encoding(false)))
                {
                    sw.AutoFlush = true;
                    foreach (string line in outQueue.GetConsumingEnumerable())
                    {
                        sw.Write(line + "\n");
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Cierre normal: la tubería ya estaba cerrada al vaciar el último mensaje.
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Escritura hacia mpv interrumpida");
            }
        }

        private void Send(string json)
        {
            if (stopping)
            {
                return;
            }
            try
            {
                outQueue.Add(json);
            }
            catch
            {
            }
        }

        private static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // Cierra todos los mpv abiertos (al apagar Playnite no deben quedar procesos huérfanos).
        public static void ShutdownAll()
        {
            ClipPlayer[] all;
            lock (live)
            {
                all = live.ToArray();
            }
            foreach (ClipPlayer c in all)
            {
                c.Shutdown();
            }
        }

        // Cierra mpv al destruir el visor.
        public void Shutdown()
        {
            stopping = true;
            lock (live)
            {
                live.Remove(this);
            }
            if (poll != null)
            {
                poll.Stop();
                poll = null;
            }
            try
            {
                outQueue.Add("{\"command\":[\"quit\"]}");
                outQueue.CompleteAdding();
            }
            catch
            {
            }
            try
            {
                if (proc != null)
                {
                    if (!proc.WaitForExit(500))
                    {
                        proc.Kill();
                    }
                    proc.Dispose();
                    proc = null;
                }
            }
            catch
            {
            }
            try
            {
                if (pipe != null)
                {
                    pipe.Dispose();
                    pipe = null;
                }
            }
            catch
            {
            }
            connected = false;
        }
    }
}
