using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ConsoleOverlay
{
    internal sealed class CaptureItem
    {
        public string Path;
        public string Name;
        public bool IsVideo;
        public DateTime Time;
    }

    // ----------------------------------------------------------------------
    // Busca las capturas y clips de un juego en las carpetas habituales de los programas
    // de captura. No depende de ningún programa en concreto: solo mira carpetas.
    //  - Carpeta por juego:  <raíz>\<Juego>\...  (GameHQ, NVIDIA App...)
    //  - Archivos sueltos:   <raíz>\<Juego> 2026-10-13 ... (Xbox Game Bar...)
    // ----------------------------------------------------------------------
    internal static class CaptureScanner
    {
        private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };
        private static readonly string[] VideoExt = { ".mp4", ".mkv", ".webm", ".mov", ".avi", ".wmv" };

        public static List<string> DefaultRoots()
        {
            List<string> roots = new List<string>();
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            if (!string.IsNullOrEmpty(videos))
            {
                roots.Add(Path.Combine(videos, "GameHQ"));
                roots.Add(Path.Combine(videos, "SavePlay"));
                roots.Add(Path.Combine(videos, "PlayHQ"));
                roots.Add(Path.Combine(videos, "Captures"));
                roots.Add(videos);
            }
            if (!string.IsNullOrEmpty(pictures))
            {
                roots.Add(Path.Combine(pictures, "Screenshots"));
                roots.Add(pictures);
            }

            // Carpetas que el propio GameHQ tenga configuradas.
            foreach (string baseDir in new[] { roaming, local })
            {
                if (string.IsNullOrEmpty(baseDir))
                {
                    continue;
                }
                foreach (string cfg in new[]
                {
                    Path.Combine(baseDir, "GameHQ", "GameHQ", "config.json"),
                    Path.Combine(baseDir, "GameHQ", "config.json")
                })
                {
                    AddGameHqRoots(cfg, roots);
                }
            }
            return roots;
        }

        private static void AddGameHqRoots(string configFile, List<string> roots)
        {
            try
            {
                if (!File.Exists(configFile))
                {
                    return;
                }
                string text = File.ReadAllText(configFile);
                foreach (string key in new[] { "storage.screenshots_root", "storage.clips_root" })
                {
                    Match m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    if (m.Success)
                    {
                        string p = m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\/", "/");
                        if (Path.IsPathRooted(p))
                        {
                            roots.Add(p);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static string Norm(string s)
        {
            if (s == null)
            {
                return "";
            }
            char[] c = s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
            return new string(c);
        }

        // La carpeta se llama como el juego, o empieza por su nombre seguido de un separador
        // (p. ej. "Resonance_ A Plague Tale Legacy - 1.3.0.0 (2026...)").
        private static bool FolderMatches(string folder, HashSet<string> keys, List<string> rawNames)
        {
            if (string.IsNullOrEmpty(folder))
            {
                return false;
            }
            if (keys.Contains(Norm(folder)))
            {
                return true;
            }
            foreach (string raw in rawNames)
            {
                if (raw.Length >= 4 && folder.Length > raw.Length &&
                    folder.StartsWith(raw, StringComparison.OrdinalIgnoreCase) &&
                    !char.IsLetterOrDigit(folder[raw.Length]))
                {
                    return true;
                }
            }
            return false;
        }

        // Busca archivos de captura creados desde "since" (profundidad limitada). Se usa justo
        // después de pedir una captura para saber en qué carpeta la guarda el programa.
        public static string FindNewCapture(IEnumerable<string> roots, DateTime since)
        {
            string best = null;
            DateTime bestTime = DateTime.MinValue;
            int budget = 3000;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rootRaw in roots)
            {
                string root;
                try
                {
                    root = Path.GetFullPath(rootRaw.Trim().Trim('"'));
                }
                catch
                {
                    continue;
                }
                if (Directory.Exists(root) && seen.Add(root))
                {
                    NewestIn(root, 0, since, ref best, ref bestTime, ref budget);
                }
            }
            return best;
        }

        private static void NewestIn(string dir, int depth, DateTime since, ref string best, ref DateTime bestTime, ref int budget)
        {
            if (budget <= 0)
            {
                return;
            }
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    if (--budget <= 0)
                    {
                        return;
                    }
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (!ImageExt.Contains(ext) && !VideoExt.Contains(ext))
                    {
                        continue;
                    }
                    DateTime t = File.GetLastWriteTime(file);
                    if (t >= since && t > bestTime)
                    {
                        best = file;
                        bestTime = t;
                    }
                }
                if (depth >= 3)
                {
                    return;
                }
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    if (--budget <= 0)
                    {
                        return;
                    }
                    NewestIn(sub, depth + 1, since, ref best, ref bestTime, ref budget);
                }
            }
            catch
            {
            }
        }

        public static List<CaptureItem> Scan(string gameName, IEnumerable<string> roots, int max, IEnumerable<string> directFolders = null)
        {
            return Scan(new List<string> { gameName }, roots, max, directFolders);
        }

        // names: nombre del juego y variantes (parte antes de ":", carpeta de instalación, ejecutable...).
        public static List<CaptureItem> Scan(IEnumerable<string> names, IEnumerable<string> roots, int max, IEnumerable<string> directFolders)
        {
            List<CaptureItem> result = new List<CaptureItem>();
            HashSet<string> keys = new HashSet<string>();
            List<string> rawNames = new List<string>();
            foreach (string n in names)
            {
                string k = Norm(n);
                if (k.Length >= 4)
                {
                    keys.Add(k);
                    rawNames.Add(n.Trim());
                }
            }
            if (keys.Count == 0)
            {
                return result;
            }
            HashSet<string> seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string rootRaw in roots)
            {
                string root;
                try
                {
                    root = Path.GetFullPath(rootRaw.Trim().Trim('"'));
                }
                catch
                {
                    continue;
                }
                if (!Directory.Exists(root) || !seenRoots.Add(root))
                {
                    continue;
                }

                // 1) Carpeta con el nombre del juego dentro de la raíz.
                try
                {
                    foreach (string dir in Directory.EnumerateDirectories(root))
                    {
                        if (FolderMatches(Path.GetFileName(dir), keys, rawNames))
                        {
                            AddFiles(dir, SearchOption.AllDirectories, null, result, seenFiles);
                        }
                    }
                }
                catch
                {
                }

                // 2) Archivos sueltos en la raíz cuyo nombre empieza por el del juego.
                AddFiles(root, SearchOption.TopDirectoryOnly, keys, result, seenFiles);
            }

            // 3) Carpetas que ya son del juego (p. ej. las de Steam por appid).
            if (directFolders != null)
            {
                foreach (string d in directFolders)
                {
                    if (!string.IsNullOrEmpty(d) && Directory.Exists(d))
                    {
                        AddFiles(d, SearchOption.AllDirectories, null, result, seenFiles);
                    }
                }
            }

            return result.OrderByDescending(i => i.Time).Take(max).ToList();
        }

        // Diagnóstico para el log: subcarpetas (nombres) de cada raíz.
        public static string DescribeRoots(IEnumerable<string> roots)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string r in roots)
            {
                try
                {
                    string root = Path.GetFullPath(r.Trim().Trim('"'));
                    if (!Directory.Exists(root) || !seen.Add(root))
                    {
                        continue;
                    }
                    sb.Append(root).Append(" {");
                    int n = 0;
                    foreach (string d in Directory.EnumerateDirectories(root))
                    {
                        if (n++ >= 25)
                        {
                            sb.Append("…");
                            break;
                        }
                        sb.Append(Path.GetFileName(d)).Append("; ");
                    }
                    sb.Append("} ");
                }
                catch
                {
                }
            }
            return sb.ToString();
        }

        // Capturas de Steam: <Steam>\userdata\<usuario>\760\remote\<appid>\screenshots
        public static List<string> SteamFolders(string appId)
        {
            List<string> res = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(appId) || !appId.All(char.IsDigit))
                {
                    return res;
                }
                string steam = null;
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    steam = k == null ? null : k.GetValue("SteamPath") as string;
                }
                if (string.IsNullOrEmpty(steam))
                {
                    return res;
                }
                string ud = Path.Combine(steam.Replace('/', '\\'), "userdata");
                if (!Directory.Exists(ud))
                {
                    return res;
                }
                foreach (string user in Directory.EnumerateDirectories(ud))
                {
                    res.Add(Path.Combine(user, "760", "remote", appId, "screenshots"));
                }
            }
            catch
            {
            }
            return res;
        }

        // Como EnumerateFiles, pero un error en una subcarpeta (carpeta que se está creando, sin permiso...) no corta el resto.
        private static IEnumerable<string> EnumerateFilesSafe(string dir, int depth)
        {
            List<string> files = new List<string>();
            try
            {
                files.AddRange(Directory.GetFiles(dir));
            }
            catch
            {
            }
            if (depth > 0)
            {
                string[] subs = null;
                try
                {
                    subs = Directory.GetDirectories(dir);
                }
                catch
                {
                }
                if (subs != null)
                {
                    foreach (string sub in subs)
                    {
                        files.AddRange(EnumerateFilesSafe(sub, depth - 1));
                    }
                }
            }
            return files;
        }

        private static void AddFiles(string dir, SearchOption option, HashSet<string> prefixKeys,
                                     List<CaptureItem> result, HashSet<string> seen)
        {
            try
            {
                foreach (string file in EnumerateFilesSafe(dir, option == SearchOption.AllDirectories ? 6 : 0))
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    bool isVideo = VideoExt.Contains(ext);
                    if (!isVideo && !ImageExt.Contains(ext))
                    {
                        continue;
                    }
                    if (prefixKeys != null)
                    {
                        string fn = Norm(Path.GetFileNameWithoutExtension(file));
                        bool match = false;
                        foreach (string pk in prefixKeys)
                        {
                            if (fn.StartsWith(pk, StringComparison.Ordinal))
                            {
                                match = true;
                                break;
                            }
                        }
                        if (!match)
                        {
                            continue;
                        }
                    }
                    if (!seen.Add(file))
                    {
                        continue;
                    }
                    DateTime t;
                    try
                    {
                        t = File.GetLastWriteTime(file);
                    }
                    catch
                    {
                        t = DateTime.MinValue;
                    }
                    result.Add(new CaptureItem
                    {
                        Path = file,
                        Name = Path.GetFileNameWithoutExtension(file),
                        IsVideo = isVideo,
                        Time = t
                    });
                }
            }
            catch
            {
            }
        }
    }

    // ----------------------------------------------------------------------
    // Lectura mínima de un MP4/MOV: solo para saber qué pistas trae (vídeo, audio) y con qué formato.
    // ----------------------------------------------------------------------
    internal static class Mp4Info
    {
        public static bool HasAudioTrack(string path, out string description)
        {
            description = "no mp4";
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".mp4" && ext != ".mov" && ext != ".m4v")
                {
                    return false;
                }
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long pos = 0;
                    byte[] head = new byte[16];
                    while (pos + 8 <= fs.Length)
                    {
                        fs.Seek(pos, SeekOrigin.Begin);
                        if (fs.Read(head, 0, 8) < 8)
                        {
                            break;
                        }
                        long size = ReadU32(head, 0);
                        string type = System.Text.Encoding.ASCII.GetString(head, 4, 4);
                        long headerLen = 8;
                        if (size == 1)
                        {
                            if (fs.Read(head, 8, 8) < 8)
                            {
                                break;
                            }
                            size = ((long)ReadU32(head, 8) << 32) | ReadU32(head, 12);
                            headerLen = 16;
                        }
                        else if (size == 0)
                        {
                            size = fs.Length - pos;
                        }
                        if (size < headerLen)
                        {
                            break;
                        }
                        if (type == "moov")
                        {
                            long len = size - headerLen;
                            if (len <= 0 || len > 64L * 1024 * 1024)
                            {
                                description = "mp4: cabecera demasiado grande";
                                return false;
                            }
                            byte[] moov = new byte[len];
                            fs.Seek(pos + headerLen, SeekOrigin.Begin);
                            int got = 0;
                            while (got < len)
                            {
                                int r = fs.Read(moov, got, (int)len - got);
                                if (r <= 0)
                                {
                                    break;
                                }
                                got += r;
                            }
                            List<string> tracks = new List<string>();
                            Boxes = new List<string>();
                            Walk(moov, 0, got, tracks);
                            bool audio = tracks.Any(t => t.StartsWith("audio", StringComparison.Ordinal));
                            description = tracks.Count == 0 ? "mp4: sin pistas" : "mp4: " + string.Join(", ", tracks);
                            if (!audio)
                            {
                                // Diagnóstico: estructura de cajas del moov, para ver por qué no aparece la pista de audio.
                                description += " | cajas: " + string.Join(" ", Boxes.Take(40));
                            }
                            return audio;
                        }
                        pos += size;
                    }
                    description = "mp4: sin moov";
                }
            }
            catch (Exception ex)
            {
                description = "error al leer: " + ex.Message;
            }
            return false;
        }

        // Duración del clip (cabecera mvhd del moov). Null si no se puede leer.
        public static TimeSpan? Duration(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long pos = 0;
                    byte[] head = new byte[16];
                    while (pos + 8 <= fs.Length)
                    {
                        fs.Seek(pos, SeekOrigin.Begin);
                        if (fs.Read(head, 0, 8) < 8)
                        {
                            break;
                        }
                        long size = ReadU32(head, 0);
                        string type = System.Text.Encoding.ASCII.GetString(head, 4, 4);
                        long headerLen = 8;
                        if (size == 1)
                        {
                            if (fs.Read(head, 8, 8) < 8)
                            {
                                break;
                            }
                            size = ((long)ReadU32(head, 8) << 32) | ReadU32(head, 12);
                            headerLen = 16;
                        }
                        else if (size == 0)
                        {
                            size = fs.Length - pos;
                        }
                        if (size < headerLen)
                        {
                            break;
                        }
                        if (type == "moov")
                        {
                            long len = size - headerLen;
                            if (len <= 0 || len > 64L * 1024 * 1024)
                            {
                                return null;
                            }
                            byte[] moov = new byte[len];
                            fs.Seek(pos + headerLen, SeekOrigin.Begin);
                            int got = 0;
                            while (got < len)
                            {
                                int r = fs.Read(moov, got, (int)len - got);
                                if (r <= 0)
                                {
                                    break;
                                }
                                got += r;
                            }
                            return FindMvhd(moov, got);
                        }
                        pos += size;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static TimeSpan? FindMvhd(byte[] b, int end)
        {
            int p = 0;
            while (p + 8 <= end)
            {
                long size = ReadU32(b, p);
                string type = System.Text.Encoding.ASCII.GetString(b, p + 4, 4);
                if (size < 8 || p + size > end)
                {
                    break;
                }
                int body = p + 8;
                if (type == "mvhd" && body + 32 <= end)
                {
                    double timescale;
                    double dur;
                    if (b[body] == 1)
                    {
                        timescale = ReadU32(b, body + 20);
                        dur = (double)(((long)ReadU32(b, body + 24) << 32) | ReadU32(b, body + 28));
                    }
                    else
                    {
                        timescale = ReadU32(b, body + 12);
                        dur = ReadU32(b, body + 16);
                    }
                    if (timescale <= 0)
                    {
                        return null;
                    }
                    return TimeSpan.FromSeconds(dur / timescale);
                }
                p += (int)size;
            }
            return null;
        }

        private static long ReadU32(byte[] b, int o)
        {
            return ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3];
        }

        private static void Walk(byte[] b, int start, int end, List<string> tracks)
        {
            int p = start;
            while (p + 8 <= end)
            {
                long size = ReadU32(b, p);
                int hdr = 8;
                string type = System.Text.Encoding.ASCII.GetString(b, p + 4, 4);
                // Cajas con tamaño de 64 bits (1) o hasta el final (0): antes cortaban el recorrido y se perdía la pista de audio.
                if (size == 1 && p + 16 <= end)
                {
                    size = ((long)ReadU32(b, p + 8) << 32) | ReadU32(b, p + 12);
                    hdr = 16;
                }
                else if (size == 0)
                {
                    size = end - p;
                }
                if (size < hdr || p + size > end)
                {
                    break;
                }
                int bodyStart = p + hdr;
                int bodyEnd = (int)(p + size);
                Boxes.Add(type);
                if (type == "moov" || type == "trak" || type == "mdia" || type == "minf" || type == "stbl")
                {
                    if (type == "trak")
                    {
                        handler = null;
                    }
                    Walk(b, bodyStart, bodyEnd, tracks);
                }
                else if (type == "hdlr" && bodyEnd - bodyStart >= 12)
                {
                    handler = System.Text.Encoding.ASCII.GetString(b, bodyStart + 8, 4);
                }
                else if (type == "stsd" && bodyEnd - bodyStart >= 16)
                {
                    string format = System.Text.Encoding.ASCII.GetString(b, bodyStart + 12, 4);
                    string kind = handler == "soun" ? "audio" : (handler == "vide" ? "video" : handler);
                    tracks.Add(kind + " " + format);
                }
                p = bodyEnd;
            }
        }

        [ThreadStatic]
        private static string handler;

        [ThreadStatic]
        private static List<string> Boxes;
    }
}
