using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ConsoleOverlay
{
    internal sealed class AchievementItem
    {
        public string Name;
        public string Description;
        public bool Unlocked;
        public DateTime? UnlockTime;
        public string IconPath;      // archivo local o null
        public bool Hidden;          // logro secreto
        public double? Percent;      // % global de jugadores que lo tienen
        public int? ProgressNum;
        public int? ProgressDenom;
        public string Api;           // nombre API (clave del logro en la base de datos)
        public int? Trophy;          // tipo de trofeo definido por el plugin: 0 bronce, 1 plata, 2 oro, 3 platino (null = no hay dato)
    }

    // ----------------------------------------------------------------------
    // Lectura de solo lectura de la base de datos del plugin "Playnite Achievements"
    // (ExtensionsData\e6aad2c9-...\achievement_cache.db) con el sqlite3.x86.dll que
    // trae Playnite. Nunca escribe. El esquema de consultas sigue el trabajo de
    // PlayniteNextOverlay (MIT).
    // ----------------------------------------------------------------------
    internal static class AchievementReader
    {
        private static readonly Guid PluginId = new Guid("e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b");
        private const string DbName = "achievement_cache.db";

        private const string BaseColumns =
            "SELECT ad.DisplayName, ad.ApiName, ad.Description, ad.UnlockedIconPath, ad.LockedIconPath, " +
            "ad.Hidden, ad.GlobalPercentUnlocked, IFNULL(ua.Unlocked, 0), ua.UnlockTimeUtc";
        private const string ExtraColumns = ", ua.ProgressNum, ua.ProgressDenom";
        private const string FromClause =
            " FROM Games g " +
            "JOIN UserGameProgress ugp ON ugp.GameId = g.Id " +
            "JOIN Users u ON u.Id = ugp.UserId AND u.IsCurrentUser = 1 " +
            "JOIN AchievementDefinitions ad ON ad.GameId = g.Id " +
            "LEFT JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id AND ua.AchievementDefinitionId = ad.Id ";
        private const string OverridesClause =
            "LEFT JOIN AchievementOverrides ao ON ao.PlayniteGameId = g.PlayniteGameId AND ao.ApiName = ad.ApiName " +
            "WHERE g.PlayniteGameId = ?1 COLLATE NOCASE AND IFNULL(ao.IsFiltered, 0) = 0 AND IFNULL(ao.IsSummaryFiltered, 0) = 0 " +
            "ORDER BY g.Id, ad.Id;";
        private const string PlainClause =
            "WHERE g.PlayniteGameId = ?1 COLLATE NOCASE ORDER BY g.Id, ad.Id;";

        private static readonly string[] Queries =
        {
            BaseColumns + ExtraColumns + FromClause + OverridesClause,
            BaseColumns + ExtraColumns + FromClause + PlainClause,
            BaseColumns + FromClause + OverridesClause,
            BaseColumns + FromClause + PlainClause
        };

        private static bool preloaded;

        public static void Preload(string playniteDir)
        {
            if (preloaded || string.IsNullOrEmpty(playniteDir))
            {
                return;
            }
            try
            {
                string p = Path.Combine(playniteDir, Native.Lib);
                if (File.Exists(p) && LoadLibrary(p) != IntPtr.Zero)
                {
                    preloaded = true;
                }
            }
            catch
            {
            }
        }

        // Devuelve null si el plugin/los datos no existen o algo falla (se anota en el log, no lanza).
        public static List<AchievementItem> Read(string extensionsDataPath, Guid gameId, Action<string> log)
        {
            try
            {
                if (string.IsNullOrEmpty(extensionsDataPath))
                {
                    return null;
                }
                string dataDir = Path.Combine(extensionsDataPath, PluginId.ToString());
                string dbPath = Path.Combine(dataDir, DbName);
                if (!File.Exists(dbPath))
                {
                    log("Logros: no existe " + dbPath + " (¿Playnite Achievements instalado y con el juego escaneado?)");
                    return null;
                }
                if (IntPtr.Size != 4)
                {
                    log("Logros: proceso de 64 bits, no se puede usar sqlite3.x86.dll.");
                    return null;
                }

                List<AchievementItem> result = new List<AchievementItem>();
                IntPtr db = IntPtr.Zero;
                IntPtr stmt = IntPtr.Zero;
                try
                {
                    int rc = Native.sqlite3_open_v2(Utf8z(dbPath), out db, 1, IntPtr.Zero);
                    if (rc != 0)
                    {
                        throw new InvalidOperationException("open " + rc + ": " + ErrMsg(db));
                    }
                    Native.sqlite3_busy_timeout(db, 3000);

                    rc = 1;
                    for (int q = 0; q < Queries.Length; q++)
                    {
                        rc = Native.sqlite3_prepare16_v2(db, Queries[q], -1, out stmt, IntPtr.Zero);
                        if (rc == 0)
                        {
                            break;
                        }
                        if (stmt != IntPtr.Zero)
                        {
                            Native.sqlite3_finalize(stmt);
                            stmt = IntPtr.Zero;
                        }
                    }
                    if (rc != 0)
                    {
                        throw new InvalidOperationException("prepare " + rc + ": " + ErrMsg(db));
                    }
                    bool extra = Native.sqlite3_column_count(stmt) >= 11;

                    rc = Native.sqlite3_bind_text16(stmt, 1, gameId.ToString(), -1, new IntPtr(-1));
                    if (rc != 0)
                    {
                        throw new InvalidOperationException("bind " + rc + ": " + ErrMsg(db));
                    }

                    // La consulta puede devolver la misma definición varias veces (varias filas de Games o de progreso
                    // para el mismo juego). Se deduplica por nombre API y se queda la versión conseguida, si la hay.
                    Dictionary<string, int> seen = new Dictionary<string, int>();
                    while ((rc = Native.sqlite3_step(stmt)) == 100)
                    {
                        string display = Text(stmt, 0);
                        string api = Text(stmt, 1);
                        string dedupeKey = string.IsNullOrEmpty(api) ? display : api;
                        string unlockedIcon = Text(stmt, 3);
                        string lockedIcon = Text(stmt, 4);
                        bool unlocked = Native.sqlite3_column_int64(stmt, 7) != 0;
                        string icon = ResolveIcon(dataDir, unlocked ? unlockedIcon : (lockedIcon ?? unlockedIcon))
                                      ?? ResolveIcon(dataDir, unlockedIcon);
                        AchievementItem item = new AchievementItem
                        {
                            Api = api,
                            Name = string.IsNullOrWhiteSpace(display) ? api : display,
                            Description = Text(stmt, 2),
                            Unlocked = unlocked,
                            UnlockTime = unlocked ? ParseUtc(Text(stmt, 8)) : null,
                            IconPath = icon,
                            Hidden = Native.sqlite3_column_int64(stmt, 5) != 0,
                            Percent = Native.sqlite3_column_type(stmt, 6) == 5 ? (double?)null : Native.sqlite3_column_double(stmt, 6),
                            ProgressNum = extra ? NullableInt(stmt, 9) : null,
                            ProgressDenom = extra ? NullableInt(stmt, 10) : null
                        };
                        int existing;
                        if (seen.TryGetValue(dedupeKey ?? "", out existing))
                        {
                            if (unlocked && !result[existing].Unlocked)
                            {
                                result[existing] = item;
                            }
                            continue;
                        }
                        seen[dedupeKey ?? ""] = result.Count;
                        result.Add(item);
                    }
                    if (rc != 101)
                    {
                        throw new InvalidOperationException("step " + rc + ": " + ErrMsg(db));
                    }
                    Native.sqlite3_finalize(stmt);
                    stmt = IntPtr.Zero;
                    try
                    {
                        ReadTrophies(db, gameId, result, log);
                    }
                    catch (Exception ex)
                    {
                        log("Logros: no se pudo leer el tipo de trofeo (" + ex.GetType().Name + ": " + ex.Message + ")");
                    }
                }
                finally
                {
                    if (stmt != IntPtr.Zero)
                    {
                        Native.sqlite3_finalize(stmt);
                    }
                    if (db != IntPtr.Zero)
                    {
                        Native.sqlite3_close(db);
                    }
                }
                if (result.Count == 0)
                {
                    log("Logros: la base de datos no tiene logros para el juego " + gameId +
                        " (¿está escaneado en Playnite Achievements?)");
                    return null;
                }
                log("Logros: " + result.Count + " leídos para el juego " + gameId);
                return result;
            }
            catch (Exception ex)
            {
                log("Logros: no se pudieron leer (" + ex.GetType().Name + ": " + ex.Message + ")");
                return null;
            }
        }

        // Columnas de una tabla (PRAGMA table_info).
        private static List<string> Columns(IntPtr db, string table)
        {
            List<string> cols = new List<string>();
            IntPtr st = IntPtr.Zero;
            try
            {
                if (Native.sqlite3_prepare16_v2(db, "PRAGMA table_info(" + table + ");", -1, out st, IntPtr.Zero) != 0)
                {
                    return cols;
                }
                while (Native.sqlite3_step(st) == 100)
                {
                    string n = Text(st, 1);
                    if (!string.IsNullOrEmpty(n))
                    {
                        cols.Add(n);
                    }
                }
            }
            finally
            {
                if (st != IntPtr.Zero)
                {
                    Native.sqlite3_finalize(st);
                }
            }
            return cols;
        }

        // Tipo de trofeo, como lo calcula el plugin Playnite Achievements: primero el que haya puesto el usuario
        // (tabla AchievementOverrides), después el del proveedor (AchievementDefinitions.TrophyType: platinum/gold/silver/bronze)
        // y por último su nivel de rareza (columna Rarity: Common/Uncommon/Rare/UltraRare). Si no hay nada, el % de jugadores.
        private static void ReadTrophies(IntPtr db, Guid gameId, List<AchievementItem> items, Action<string> log)
        {
            List<string> cols = Columns(db, "AchievementDefinitions");
            bool hasTroph = cols.Any(c => c.Equals("TrophyType", StringComparison.OrdinalIgnoreCase));
            bool hasRar = cols.Any(c => c.Equals("Rarity", StringComparison.OrdinalIgnoreCase));
            bool hasOv = Columns(db, "AchievementOverrides").Any(c => c.Equals("TrophyType", StringComparison.OrdinalIgnoreCase));
            log("Logros: trofeo - TrophyType=" + hasTroph + ", Rarity=" + hasRar + ", override=" + hasOv);
            if (!hasTroph && !hasRar && !hasOv)
            {
                log("Logros: no hay datos de tipo de trofeo; se usa el % de jugadores. Columnas: " + string.Join(", ", cols));
                return;
            }
            IntPtr st = IntPtr.Zero;
            try
            {
                string sql = "SELECT ad.ApiName, " + (hasTroph ? "ad.TrophyType" : "NULL") + ", " + (hasRar ? "ad.Rarity" : "NULL") + ", " +
                             (hasOv ? "ao.TrophyType" : "NULL") + " FROM AchievementDefinitions ad JOIN Games g ON ad.GameId = g.Id " +
                             (hasOv ? "LEFT JOIN AchievementOverrides ao ON ao.PlayniteGameId = g.PlayniteGameId AND ao.ApiName = ad.ApiName " : "") +
                             "WHERE g.PlayniteGameId = ?1 COLLATE NOCASE;";
                if (Native.sqlite3_prepare16_v2(db, sql, -1, out st, IntPtr.Zero) != 0)
                {
                    log("Logros: no se pudo leer el tipo de trofeo: " + ErrMsg(db));
                    return;
                }
                Native.sqlite3_bind_text16(st, 1, gameId.ToString(), -1, new IntPtr(-1));
                Dictionary<string, int?> map = new Dictionary<string, int?>();
                HashSet<string> seenValues = new HashSet<string>();
                while (Native.sqlite3_step(st) == 100)
                {
                    string api = Text(st, 0) ?? "";
                    string def = Text(st, 1);
                    string rar = Text(st, 2);
                    string ov = Text(st, 3);
                    if (seenValues.Count < 8)
                    {
                        seenValues.Add((ov ?? "-") + "/" + (def ?? "-") + "/" + (rar ?? "-"));
                    }
                    map[api] = ParseTrophy(ov) ?? ParseTrophy(def) ?? ParseTrophy(rar);
                }
                log("Logros: trofeos (override/proveedor/rareza) vistos = " + string.Join(" | ", seenValues));
                int known = 0;
                foreach (AchievementItem it in items)
                {
                    int? t;
                    if (it.Api != null && map.TryGetValue(it.Api, out t) && t.HasValue)
                    {
                        it.Trophy = t;
                        known++;
                    }
                }
                log("Logros: " + known + " de " + items.Count + " con tipo de trofeo del plugin");
            }
            finally
            {
                if (st != IntPtr.Zero)
                {
                    Native.sqlite3_finalize(st);
                }
            }
        }

        // Texto o número del plugin a 0 bronce, 1 plata, 2 oro, 3 platino. Null si no se reconoce.
        private static int? ParseTrophy(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }
            string v = raw.Trim().ToLowerInvariant();
            if (v.Contains("platin"))
            {
                return 3;
            }
            if (v.Contains("gold") || v.Contains("oro"))
            {
                return 2;
            }
            if (v.Contains("silver") || v.Contains("plata"))
            {
                return 1;
            }
            if (v.Contains("bronze") || v.Contains("bronce"))
            {
                return 0;
            }
            // Niveles de rareza: común, poco común, raro, ultra raro.
            if (v.Contains("ultra"))
            {
                return 3;
            }
            if (v.Contains("uncommon") || v.Contains("poco"))
            {
                return 1;
            }
            if (v.Contains("common") || v.Contains("com"))
            {
                return 0;
            }
            if (v.Contains("rare") || v.Contains("raro"))
            {
                return 2;
            }
            int n;
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0 && n <= 3)
            {
                return n;
            }
            return null;
        }

        private static string ResolveIcon(string dataDir, string stored)
        {
            if (string.IsNullOrWhiteSpace(stored))
            {
                return null;
            }
            stored = stored.Trim();
            if (stored.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return null; // sin red: solo se usan iconos ya descargados
            }
            try
            {
                if (stored.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    stored = new Uri(stored).LocalPath;
                }
                string path = Path.IsPathRooted(stored) ? stored : Path.Combine(dataDir, stored.Replace('/', '\\'));
                return File.Exists(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }

        private static DateTime? ParseUtc(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return null;
            }
            DateTime dt;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
            {
                return dt.ToLocalTime();
            }
            long secs;
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out secs) && secs > 0 && secs < 100000000000L)
            {
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(secs).ToLocalTime();
            }
            return null;
        }

        private static string Text(IntPtr stmt, int col)
        {
            if (Native.sqlite3_column_type(stmt, col) == 5)
            {
                return null;
            }
            IntPtr p = Native.sqlite3_column_text16(stmt, col);
            return p == IntPtr.Zero ? null : Marshal.PtrToStringUni(p);
        }

        private static int? NullableInt(IntPtr stmt, int col)
        {
            if (Native.sqlite3_column_type(stmt, col) == 5)
            {
                return null;
            }
            long v = Native.sqlite3_column_int64(stmt, col);
            return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, v));
        }

        private static string ErrMsg(IntPtr db)
        {
            try
            {
                if (db == IntPtr.Zero)
                {
                    return "";
                }
                IntPtr p = Native.sqlite3_errmsg16(db);
                return p == IntPtr.Zero ? "" : Marshal.PtrToStringUni(p);
            }
            catch
            {
                return "";
            }
        }

        private static byte[] Utf8z(string s)
        {
            byte[] b = System.Text.Encoding.UTF8.GetBytes(s);
            byte[] z = new byte[b.Length + 1];
            Buffer.BlockCopy(b, 0, z, 0, b.Length);
            return z;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        private static class Native
        {
            public const string Lib = "sqlite3.x86.dll";

            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_busy_timeout(IntPtr db, int ms);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
            public static extern int sqlite3_prepare16_v2(IntPtr db, [MarshalAs(UnmanagedType.LPWStr)] string sql, int nBytes, out IntPtr stmt, IntPtr tail);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
            public static extern int sqlite3_bind_text16(IntPtr stmt, int index, [MarshalAs(UnmanagedType.LPWStr)] string value, int nBytes, IntPtr destructor);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_step(IntPtr stmt);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_column_type(IntPtr stmt, int col);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr sqlite3_column_text16(IntPtr stmt, int col);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern long sqlite3_column_int64(IntPtr stmt, int col);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern double sqlite3_column_double(IntPtr stmt, int col);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_column_count(IntPtr stmt);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_finalize(IntPtr stmt);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_close(IntPtr db);
            [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr sqlite3_errmsg16(IntPtr db);
        }
    }
}
