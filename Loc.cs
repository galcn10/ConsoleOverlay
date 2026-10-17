using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ConsoleOverlay
{
    public sealed class LangOption
    {
        public string Code { get; set; }
        public string Name { get; set; }
    }

    // ----------------------------------------------------------------------
    // Idiomas. El texto original (en español) hace de clave: si falta una traducción se ve en español.
    // Cada idioma se puede ampliar o corregir con un archivo de texto en
    //   <datos del plugin>\lang\<código>.txt     (una traducción por línea:  original <TAB> traducción)
    // sin recompilar. \n dentro de una línea es un salto de línea.
    // ----------------------------------------------------------------------
    internal static class Loc
    {
        public static readonly string[][] Languages =
        {
            new[] { "es", "Español" },
            new[] { "en", "English" },
            new[] { "fr", "Français" },
            new[] { "de", "Deutsch" },
            new[] { "pt", "Português" },
            new[] { "it", "Italiano" },
            new[] { "ru", "Русский" },
            new[] { "zh", "简体中文" },
            new[] { "ja", "日本語" }
        };

        // Idioma de la propia aplicación (Playnite), p. ej. "es_ES". Lo informa el plugin al arrancar.
        public static string HostLanguage { get; set; }

        private static Dictionary<string, string> map;
        public static string Current { get; private set; } = "es";

        // code: "auto" (idioma de Playnite, y si no, el de Windows) o uno de los códigos de la lista.
        public static void Init(string dataPath, string code)
        {
            string lang = Resolve(code);
            Current = lang;
            Dictionary<string, string> m = null;
            if (lang != "es")
            {
                m = new Dictionary<string, string>(Lang.For(lang) ?? new Dictionary<string, string>());
                try
                {
                    string file = Path.Combine(dataPath ?? "", "lang", lang + ".txt");
                    if (File.Exists(file))
                    {
                        foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                        {
                            if (line.Length == 0 || line[0] == '#')
                            {
                                continue;
                            }
                            int tab = line.IndexOf('\t');
                            if (tab > 0)
                            {
                                m[line.Substring(0, tab).Replace("\\n", "\n")] = line.Substring(tab + 1).Replace("\\n", "\n");
                            }
                        }
                    }
                }
                catch
                {
                }
            }
            map = m;
        }

        public static string Resolve(string code)
        {
            string c = (code ?? "auto").Trim().ToLowerInvariant();
            if (c == "auto" || c.Length == 0)
            {
                c = null;
                // 1) el idioma de Playnite; 2) el de Windows; 3) inglés.
                try
                {
                    string h = (HostLanguage ?? "").Trim().ToLowerInvariant();
                    int cut = h.IndexOfAny(new[] { '_', '-' });
                    if (cut > 0)
                    {
                        h = h.Substring(0, cut);
                    }
                    if (h.Length >= 2 && h.Length <= 3)
                    {
                        c = h;
                    }
                }
                catch
                {
                }
                if (c == null)
                {
                    try
                    {
                        c = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
                    }
                    catch
                    {
                        c = "en";
                    }
                }
            }
            foreach (string[] l in Languages)
            {
                if (l[0] == c)
                {
                    return c;
                }
            }
            return c == "gl" || c == "ca" ? "es" : "en";
        }

        public static string T(string es)
        {
            string v;
            if (map != null && es != null && map.TryGetValue(es, out v) && !string.IsNullOrEmpty(v))
            {
                return v;
            }
            return es;
        }

        public static string T(string es, params object[] args)
        {
            try
            {
                return string.Format(T(es), args);
            }
            catch (FormatException)
            {
                return es;
            }
        }
    }
}
