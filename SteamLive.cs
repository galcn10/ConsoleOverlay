using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace ConsoleOverlay
{
    // ----------------------------------------------------------------------
    // Lectura de solo lectura de los archivos locales de Steam
    //   Steam\appcache\stats\UserGameStatsSchema_<appid>.bin  (definición de logros y estadísticas)
    //   Steam\appcache\stats\UserGameStats_<cuenta>_<appid>.bin (tu estado)
    // Steam reescribe el segundo cuando el juego guarda estadísticas, así que refleja los logros y contadores
    // casi al instante, sin red ni claves. Se usa solo para completar lo que ya leyó Playnite Achievements.
    // Nunca escribe en esos archivos ni toca el proceso del juego.
    // ----------------------------------------------------------------------
    internal static class SteamLive
    {
        private sealed class Kv
        {
            public string Key;
            public string Str;
            public long? Num;
            public List<Kv> Kids;

            public Kv Child(string key)
            {
                if (Kids == null)
                {
                    return null;
                }
                foreach (Kv k in Kids)
                {
                    if (string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase))
                    {
                        return k;
                    }
                }
                return null;
            }

            public string Text()
            {
                return Str ?? (Num.HasValue ? Num.Value.ToString(CultureInfo.InvariantCulture) : null);
            }

            public Kv Find(string key)
            {
                Kv direct = Child(key);
                if (direct != null)
                {
                    return direct;
                }
                if (Kids != null)
                {
                    foreach (Kv k in Kids)
                    {
                        Kv r = k.Find(key);
                        if (r != null)
                        {
                            return r;
                        }
                    }
                }
                return null;
            }
        }

        private static string cachedDir;

        private static string StatsDir()
        {
            if (cachedDir != null)
            {
                return cachedDir.Length == 0 ? null : cachedDir;
            }
            string dir = "";
            try
            {
                string steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                if (!string.IsNullOrEmpty(steam))
                {
                    string d = Path.Combine(steam.Replace('/', '\\'), "appcache", "stats");
                    if (Directory.Exists(d))
                    {
                        dir = d;
                    }
                }
            }
            catch
            {
            }
            cachedDir = dir;
            return dir.Length == 0 ? null : dir;
        }

        private static string UserFile(string dir, string appId)
        {
            try
            {
                object au = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null);
                if (au is int && (int)au != 0)
                {
                    string exact = Path.Combine(dir, "UserGameStats_" + (int)au + "_" + appId + ".bin");
                    if (File.Exists(exact))
                    {
                        return exact;
                    }
                }
            }
            catch
            {
            }
            try
            {
                // Sin sesión activa conocida: el archivo más reciente de ese juego.
                return Directory.GetFiles(dir, "UserGameStats_*_" + appId + ".bin")
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        public static bool IsSteamAppId(string gameId)
        {
            long n;
            return !string.IsNullOrEmpty(gameId) && long.TryParse(gameId, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 0;
        }

        // Última escritura del archivo de estado (0 si no hay).
        public static long Stamp(string appId)
        {
            try
            {
                string dir = StatsDir();
                if (dir == null || !IsSteamAppId(appId))
                {
                    return 0;
                }
                string f = UserFile(dir, appId);
                return f == null ? 0 : File.GetLastWriteTimeUtc(f).Ticks;
            }
            catch
            {
                return 0;
            }
        }

        // Completa la lista con el estado local de Steam. Devuelve cuántos logros o contadores cambió.
        public static int Apply(string appId, List<AchievementItem> items, Action<string> log)
        {
            try
            {
                string dir = StatsDir();
                if (dir == null || !IsSteamAppId(appId) || items == null || items.Count == 0)
                {
                    return 0;
                }
                string userFile = UserFile(dir, appId);
                string schemaFile = Path.Combine(dir, "UserGameStatsSchema_" + appId + ".bin");
                if (userFile == null || !File.Exists(schemaFile))
                {
                    log("Steam local: no hay archivos de estadísticas para " + appId + " en " + dir);
                    return 0;
                }

                Kv schema = Parse(schemaFile);
                Kv user = Parse(userFile);
                Kv stats = schema.Find("stats");
                Kv cache = user.Child("cache") ?? user.Find("cache") ?? (user.Kids != null && user.Kids.Count > 0 ? user.Kids[0] : null);
                if (stats == null || stats.Kids == null || cache == null)
                {
                    log("Steam local: formato no reconocido (esquema " + (stats != null) + ", estado " + (cache != null) + ")");
                    return 0;
                }

                // nombre de estadística numérica -> id del grupo
                Dictionary<string, string> statIdByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                // nombre API del logro -> (grupo, bit, estadística de progreso, máximo)
                Dictionary<string, Tuple<string, int, string, long>> ach =
                    new Dictionary<string, Tuple<string, int, string, long>>(StringComparer.OrdinalIgnoreCase);
                string steamLang = SteamLanguage(Loc.Current);
                Dictionary<string, Tuple<string, string>> texts =
                    new Dictionary<string, Tuple<string, string>>(StringComparer.OrdinalIgnoreCase);
                foreach (Kv grp in stats.Kids)
                {
                    string gid = grp.Child("id") != null ? grp.Child("id").Text() : grp.Key;
                    Kv nm = grp.Child("name");
                    if (nm != null && !string.IsNullOrEmpty(nm.Text()))
                    {
                        statIdByName[nm.Text()] = gid;
                    }
                    Kv bits = grp.Child("bits");
                    if (bits == null || bits.Kids == null)
                    {
                        continue;
                    }
                    foreach (Kv bit in bits.Kids)
                    {
                        Kv name = bit.Child("name");
                        int bi;
                        if (name == null || string.IsNullOrEmpty(name.Text()) || !int.TryParse(bit.Key, out bi))
                        {
                            continue;
                        }
                        string progStat = null;
                        long progMax = 0;
                        Kv prog = bit.Child("progress");
                        if (prog != null)
                        {
                            Kv op = prog.Find("operand1");
                            progStat = op != null ? op.Text() : null;
                            Kv mx = prog.Child("max_val");
                            long m;
                            if (mx != null && long.TryParse(mx.Text(), NumberStyles.Integer, CultureInfo.InvariantCulture, out m))
                            {
                                progMax = m;
                            }
                        }
                        ach[name.Text()] = Tuple.Create(gid, bi, progStat, progMax);
                        Kv disp = bit.Child("display");
                        if (disp != null)
                        {
                            string ln = Localized(disp.Child("name"), steamLang);
                            string ld = Localized(disp.Child("desc"), steamLang);
                            if (ln != null || ld != null)
                            {
                                texts[name.Text()] = Tuple.Create(ln, ld);
                            }
                        }
                    }
                }

                if (dumped.Add(appId))
                {
                    Dump(appId, stats, cache, statIdByName, ach, log);
                }
                int changed = 0, unlockedFile = 0, matched = 0, translated = 0;
                foreach (AchievementItem it in items)
                {
                    Tuple<string, int, string, long> def;
                    if (string.IsNullOrEmpty(it.Api) || !ach.TryGetValue(it.Api, out def))
                    {
                        continue;
                    }
                    matched++;
                    Tuple<string, string> tx;
                    if (texts.TryGetValue(it.Api, out tx))
                    {
                        if (!string.IsNullOrWhiteSpace(tx.Item1) && it.Name != tx.Item1)
                        {
                            it.Name = tx.Item1;
                            translated++;
                        }
                        if (!string.IsNullOrWhiteSpace(tx.Item2) && it.Description != tx.Item2)
                        {
                            it.Description = tx.Item2;
                        }
                    }
                    Kv grp = cache.Child(def.Item1);
                    long data = 0;
                    if (grp != null && grp.Child("data") != null && grp.Child("data").Num.HasValue)
                    {
                        data = grp.Child("data").Num.Value;
                    }
                    bool got = def.Item2 >= 0 && def.Item2 < 32 && ((data >> def.Item2) & 1) != 0;
                    if (got)
                    {
                        unlockedFile++;
                    }
                    if (got && !it.Unlocked)
                    {
                        it.Unlocked = true;
                        Kv times = grp != null ? grp.Child("AchievementTimes") : null;
                        Kv tk = times != null ? times.Child(def.Item2.ToString(CultureInfo.InvariantCulture)) : null;
                        if (tk != null && tk.Num.HasValue && tk.Num.Value > 0)
                        {
                            it.UnlockTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(tk.Num.Value);
                        }
                        else
                        {
                            it.UnlockTime = DateTime.UtcNow;
                        }
                        changed++;
                    }
                    if (!it.Unlocked && def.Item3 != null && def.Item4 > 0)
                    {
                        string sid;
                        Kv sg;
                        if (statIdByName.TryGetValue(def.Item3, out sid) && (sg = cache.Child(sid)) != null)
                        {
                            // El valor puede venir como nodo propio o dentro de «data», según el tipo de estadística.
                            long? raw = sg.Child("data") != null && sg.Child("data").Num.HasValue ? sg.Child("data").Num : sg.Num;
                            if (raw.HasValue)
                            {
                                int val = (int)Math.Max(0, Math.Min(int.MaxValue, raw.Value));
                                int max = (int)Math.Min(int.MaxValue, def.Item4);
                                log("Steam local: progreso " + (it.Name ?? it.Api) + " plugin=" + it.ProgressNum + "/" + it.ProgressDenom +
                                    " steam=" + val + "/" + max);
                                if (it.ProgressNum != val || it.ProgressDenom != max)
                                {
                                    it.ProgressNum = val;
                                    it.ProgressDenom = max;
                                    changed++;
                                }
                            }
                            else
                            {
                                log("Steam local: la estadística " + def.Item3 + " no tiene valor numérico");
                            }
                        }
                        else
                        {
                            log("Steam local: estadística " + def.Item3 + " no encontrada en el estado");
                        }
                    }
                }
                log("Steam local: " + matched + "/" + items.Count + " logros reconocidos, " + unlockedFile +
                    " conseguidos según Steam, " + changed + " cambios aplicados, " + translated + " textos en '" + steamLang + "' (" + texts.Count + " con traducción; archivo " + Path.GetFileName(userFile) + ")");
                return changed;
            }
            catch (Exception ex)
            {
                log("Steam local: no se pudo leer (" + ex.GetType().Name + ": " + ex.Message + ")");
                return 0;
            }
        }

        // Idioma de la interfaz del plugin -> nombre de idioma de Steam.
        private static string SteamLanguage(string code)
        {
            string c = (code ?? "en").ToLowerInvariant();
            if (c.StartsWith("es")) { return "spanish"; }
            if (c.StartsWith("fr")) { return "french"; }
            if (c.StartsWith("de")) { return "german"; }
            if (c.StartsWith("pt")) { return c.Contains("br") ? "brazilian" : "portuguese"; }
            if (c.StartsWith("it")) { return "italian"; }
            if (c.StartsWith("ru")) { return "russian"; }
            if (c == "zht") { return "tchinese"; }
            if (c.StartsWith("zh")) { return "schinese"; }
            if (c.StartsWith("ja")) { return "japanese"; }
            if (c.StartsWith("pl")) { return "polish"; }
            if (c.StartsWith("tr")) { return "turkish"; }
            if (c.StartsWith("ko")) { return "koreana"; }
            if (c.StartsWith("nl")) { return "dutch"; }
            if (c.StartsWith("uk")) { return "ukrainian"; }
            return "english";
        }

        // Texto de un nodo {english:…, spanish:…}; null si no está en ese idioma.
        private static string Localized(Kv node, string lang)
        {
            if (node == null)
            {
                return null;
            }
            Kv v = node.Child(lang);
            return v != null ? v.Text() : (node.Kids == null ? node.Text() : null);
        }

        private static readonly HashSet<string> dumped = new HashSet<string>();

        // Volcado único (por juego y sesión) de la estructura que ve el lector, para ajustar el formato si hace falta.
        private static void Dump(string appId, Kv stats, Kv cache, Dictionary<string, string> statIdByName,
                                 Dictionary<string, Tuple<string, int, string, long>> ach, Action<string> log)
        {
            try
            {
                StringBuilder sb = new StringBuilder("Steam local [diagnóstico " + appId + "]: grupos del esquema=");
                foreach (Kv g in stats.Kids.Take(12))
                {
                    sb.Append("{").Append(g.Key).Append(":");
                    if (g.Kids != null)
                    {
                        sb.Append(string.Join(",", g.Kids.Take(10).Select(k => k.Key)));
                    }
                    sb.Append("} ");
                }
                log(sb.ToString());
                log("Steam local [diagnóstico]: estadísticas con nombre=" + string.Join(", ", statIdByName.Take(25).Select(p => p.Key + "→" + p.Value)));
                int n = 0;
                foreach (KeyValuePair<string, Tuple<string, int, string, long>> p in ach)
                {
                    if (p.Value.Item3 != null || p.Value.Item4 > 0)
                    {
                        log("Steam local [diagnóstico]: logro con progreso " + p.Key + " estadística=" + p.Value.Item3 + " máx=" + p.Value.Item4);
                        if (++n >= 8)
                        {
                            break;
                        }
                    }
                }
                if (true)
                {
                    foreach (Kv g in stats.Kids)
                    {
                        Kv bits = g.Child("bits");
                        if (bits != null && bits.Kids != null && bits.Kids.Count > 0)
                        {
                            Kv first = bits.Kids[0];
                            log("Steam local [diagnóstico]: claves de un logro: " +
                                string.Join(",", (first.Kids ?? new List<Kv>()).Select(k => k.Key + (k.Kids != null ? "{" + string.Join("/", k.Kids.Take(6).Select(c => c.Key)) + "}" : ""))));
                            break;
                        }
                    }
                }
                StringBuilder us = new StringBuilder("Steam local [diagnóstico]: estado=");
                foreach (Kv c in (cache.Kids ?? new List<Kv>()).Take(40))
                {
                    Kv d = c.Child("data");
                    us.Append(c.Key).Append("=").Append(d != null ? d.Text() : (c.Num.HasValue ? c.Num.ToString() : "·")).Append(" ");
                }
                log(us.ToString());
            }
            catch (Exception ex)
            {
                log("Steam local [diagnóstico] falló: " + ex.Message);
            }
        }

        // ---- Lector del formato binario clave-valor de Valve ----

        private static Kv Parse(string path)
        {
            byte[] bytes;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (MemoryStream ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                bytes = ms.ToArray();
            }
            int pos = 0;
            Kv root = new Kv { Key = "", Kids = new List<Kv>() };
            ReadObject(bytes, ref pos, root, 0);
            return root;
        }

        private static void ReadObject(byte[] b, ref int pos, Kv parent, int depth)
        {
            if (depth > 32)
            {
                throw new InvalidDataException("anidación excesiva");
            }
            while (pos < b.Length)
            {
                byte t = b[pos++];
                if (t == 8)
                {
                    return;
                }
                string key = ReadCStr(b, ref pos);
                Kv node = new Kv { Key = key };
                switch (t)
                {
                    case 0:
                        node.Kids = new List<Kv>();
                        ReadObject(b, ref pos, node, depth + 1);
                        break;
                    case 1:
                        node.Str = ReadCStr(b, ref pos);
                        break;
                    case 2:
                    case 4:
                    case 6:
                        node.Num = BitConverter.ToUInt32(b, Need(b, pos, 4));
                        pos += 4;
                        break;
                    case 3:
                        node.Num = (long)BitConverter.ToSingle(b, Need(b, pos, 4));
                        pos += 4;
                        break;
                    case 5:
                        while (pos + 1 < b.Length && !(b[pos] == 0 && b[pos + 1] == 0))
                        {
                            pos += 2;
                        }
                        pos += 2;
                        break;
                    case 7:
                    case 10:
                        node.Num = BitConverter.ToInt64(b, Need(b, pos, 8));
                        pos += 8;
                        break;
                    default:
                        throw new InvalidDataException("tipo desconocido " + t + " en " + key);
                }
                parent.Kids.Add(node);
            }
        }

        private static int Need(byte[] b, int pos, int n)
        {
            if (pos + n > b.Length)
            {
                throw new EndOfStreamException();
            }
            return pos;
        }

        private static string ReadCStr(byte[] b, ref int pos)
        {
            int s = pos;
            while (pos < b.Length && b[pos] != 0)
            {
                pos++;
            }
            string r = Encoding.UTF8.GetString(b, s, pos - s);
            pos++;
            return r;
        }
    }
}
