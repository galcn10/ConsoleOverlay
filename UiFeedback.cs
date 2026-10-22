using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleOverlay
{
    // Sonidos del menú y vibración del mando.
    // Los sonidos salen de un proceso auxiliar (PowerShell) con su propia sesión de audio: así el silenciado de la
    // música de Playnite no les afecta, y no hace falta silenciar ni desilenciar nada por cada sonido.
    internal static class UiFeedback
    {
        internal enum Kind { Open, Move, Accept, Back }

        // Diagnóstico: el plugin conecta esto con su logger.
        internal static Action<string> Log = _ => { };

        // Carpeta de datos del plugin (aquí se guardan los WAV).
        internal static string DataDir;

        internal static bool Animations = true;
        internal static bool Sounds = true;
        internal static bool Vibration = true;

        private static int volume = 60;
        private static bool wavsDirty = true;
        private static readonly object sync = new object();
        private static Process helper;
        private static Timer stopTimer;
        private static volatile bool helperReady;
        private static readonly DateTime[] lastPlay = new DateTime[4];

        internal static void Apply(bool animations, bool sounds, int soundVolume, bool vibration)
        {
            lock (sync)
            {
                Animations = animations;
                Sounds = sounds;
                Vibration = vibration;
                int v = Math.Max(0, Math.Min(100, soundVolume));
                if (v != volume)
                {
                    volume = v;
                    wavsDirty = true;
                    StopHelperLocked();
                }
            }
            Prewarm();
        }

        // Arranca el auxiliar de sonido antes de abrir el menú, para que el primer sonido no llegue tarde.
        internal static void Prewarm()
        {
            if (!Sounds)
            {
                return;
            }
            Task.Run(() =>
            {
                lock (sync)
                {
                    if (helper == null || helper.HasExited)
                    {
                        StartHelperLocked();
                    }
                }
            });
        }

        internal static void Play(Kind kind)
        {
            if (!Sounds)
            {
                return;
            }
            // Mientras el auxiliar no esté listo no se encola nada: así no suena una ráfaga de golpe al arrancar.
            if (!helperReady)
            {
                Prewarm();
                return;
            }
            int k = (int)kind;
            DateTime now = DateTime.UtcNow;
            if ((now - lastPlay[k]).TotalMilliseconds < 60)
            {
                return;
            }
            lastPlay[k] = now;
            string name = kind.ToString().ToLowerInvariant();
            Task.Run(() =>
            {
                try
                {
                    lock (sync)
                    {
                        if (helper == null || helper.HasExited)
                        {
                            return;
                        }
                        helper.StandardInput.WriteLine(name);
                        helper.StandardInput.Flush();
                    }
                }
                catch (Exception ex)
                {
                    Log("Sonido auxiliar: error al enviar " + name + ": " + ex.Message);
                    lock (sync)
                    {
                        StopHelperLocked();
                    }
                }
            });
        }

        // Al cerrar Playnite se para el proceso auxiliar.
        internal static void Shutdown()
        {
            lock (sync)
            {
                StopHelperLocked();
            }
        }

        private static void StartHelperLocked()
        {
            try
            {
                if (string.IsNullOrEmpty(DataDir))
                {
                    Log("Sonido auxiliar: no hay carpeta de datos del plugin.");
                    return;
                }
                string dir = Path.Combine(DataDir, "sounds");
                Directory.CreateDirectory(dir);
                if (wavsDirty)
                {
                    foreach (Kind k in Enum.GetValues(typeof(Kind)))
                    {
                        File.WriteAllBytes(Path.Combine(dir, k.ToString().ToLowerInvariant() + ".wav"), Synth(k, volume / 100.0));
                    }
                    wavsDirty = false;
                }

                string script =
                    "$ErrorActionPreference = 'SilentlyContinue'\n" +
                    "Add-Type -AssemblyName System.Media\n" +
                    "$dir = '" + dir.Replace("'", "''") + "'\n" +
                    "$p = @{}\n" +
                    "foreach ($k in 'open','move','accept','back') {\n" +
                    "  $sp = New-Object System.Media.SoundPlayer (Join-Path $dir ($k + '.wav'))\n" +
                    "  $sp.Load()\n" +
                    "  $p[$k] = $sp\n" +
                    "}\n" +
                    "[Console]::Out.WriteLine('ready')\n" +
                    "[Console]::Out.Flush()\n" +
                    "while ($true) {\n" +
                    "  $line = [Console]::In.ReadLine()\n" +
                    "  if ($line -eq $null) { break }\n" +
                    "  if ($p.ContainsKey($line)) { $p[$line].Play() }\n" +
                    "}\n";
                string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + encoded,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                helperReady = false;
                helper = Process.Start(psi);
                helper.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == "ready")
                    {
                        helperReady = true;
                    }
                };
                helper.BeginOutputReadLine();
                Log("Sonido auxiliar iniciado (PID " + helper.Id + ")");
            }
            catch (Exception ex)
            {
                helper = null;
                Log("Sonido auxiliar no disponible: " + ex.Message);
            }
        }

        private static void StopHelperLocked()
        {
            try
            {
                if (helper != null && !helper.HasExited)
                {
                    helper.Kill();
                }
            }
            catch
            {
            }
            helper = null;
            helperReady = false;
        }

        // Vibración corta de ambos motores. Solo llega a mandos XInput.
        internal static void Pulse(int ms, double strength)
        {
            if (!Vibration)
            {
                return;
            }
            ushort s = (ushort)(Math.Max(0, Math.Min(1, strength)) * 65535);
            try
            {
                uint first = 1;
                for (uint user = 0; user < 4; user++)
                {
                    XInputVibration v = new XInputVibration { LeftMotorSpeed = s, RightMotorSpeed = s };
                    uint r = XInputSetState(user, ref v);
                    if (user == 0 || r == 0)
                    {
                        first = r;
                    }
                    if (r == 0)
                    {
                        Log("Vibración: mando XInput " + user + " responde (0 = correcto)");
                    }
                }
                if (first != 0)
                {
                    Log("Vibración: ningún mando XInput aceptó la orden (código " + first + "; 1167 = no conectado)");
                }
                lock (sync)
                {
                    if (stopTimer != null)
                    {
                        stopTimer.Dispose();
                    }
                    stopTimer = new Timer(_ => Stop(), null, ms, Timeout.Infinite);
                }
            }
            catch (DllNotFoundException ex)
            {
                Log("Vibración: no se encuentra xinput1_4.dll: " + ex.Message);
            }
            catch (EntryPointNotFoundException ex)
            {
                Log("Vibración: XInputSetState no disponible: " + ex.Message);
            }
        }

        private static void Stop()
        {
            try
            {
                for (uint user = 0; user < 4; user++)
                {
                    XInputVibration off = new XInputVibration();
                    XInputSetState(user, ref off);
                }
            }
            catch
            {
            }
        }

        // Notas cortas con caída rápida. Frecuencias en Hz, duración en ms.
        private static byte[] Synth(Kind kind, double gain)
        {
            const int rate = 22050;
            double[][] notes;
            switch (kind)
            {
                case Kind.Open:
                    notes = new[] { new double[] { 523, 70 }, new double[] { 659, 70 }, new double[] { 784, 120 } };
                    break;
                case Kind.Accept:
                    notes = new[] { new double[] { 880, 60 }, new double[] { 1320, 110 } };
                    break;
                case Kind.Back:
                    notes = new[] { new double[] { 660, 60 }, new double[] { 440, 100 } };
                    break;
                default:
                    notes = new[] { new double[] { 1500, 22 } };
                    break;
            }

            int total = 0;
            foreach (double[] n in notes)
            {
                total += (int)(rate * n[1] / 1000.0);
            }
            short[] pcm = new short[total];
            int pos = 0;
            double amp = 0.22 * gain;
            foreach (double[] n in notes)
            {
                int len = (int)(rate * n[1] / 1000.0);
                for (int i = 0; i < len; i++)
                {
                    double t = i / (double)rate;
                    double env = Math.Exp(-6.0 * i / len);
                    pcm[pos++] = (short)(amp * env * Math.Sin(2 * Math.PI * n[0] * t) * 32767);
                }
            }

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                int dataBytes = pcm.Length * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + dataBytes);
                w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)1);
                w.Write(rate);
                w.Write(rate * 2);
                w.Write((short)2);
                w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(dataBytes);
                foreach (short s in pcm)
                {
                    w.Write(s);
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputVibration
        {
            public ushort LeftMotorSpeed;
            public ushort RightMotorSpeed;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
        private static extern uint XInputSetState(uint userIndex, ref XInputVibration vibration);
    }
}
