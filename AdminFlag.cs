using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace ConsoleOverlay
{
    // "Ejecutar este programa como administrador" desde Propiedades > Compatibilidad se guarda en
    // HKCU (o HKLM) \Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers como "~ RUNASADMIN".
    // Un juego elevado no se puede pausar desde un Playnite normal. Esta clase detecta y quita la marca de HKCU.
    internal static class AdminFlag
    {
        private const string LayersPath = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

        public static bool HasInUser(string exePath)
        {
            return Find(Registry.CurrentUser, exePath) != null;
        }

        public static bool HasInMachine(string exePath)
        {
            return Find(Registry.LocalMachine, exePath) != null;
        }

        private static string Find(RegistryKey root, string exePath)
        {
            try
            {
                if (string.IsNullOrEmpty(exePath))
                {
                    return null;
                }
                using (RegistryKey k = root.OpenSubKey(LayersPath))
                {
                    if (k == null)
                    {
                        return null;
                    }
                    foreach (string name in k.GetValueNames())
                    {
                        if (string.Equals(name, exePath, StringComparison.OrdinalIgnoreCase))
                        {
                            string v = k.GetValue(name) as string;
                            if (v != null && v.ToUpperInvariant().Contains("RUNASADMIN"))
                            {
                                return name;
                            }
                        }
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        // Quita RUNASADMIN de todas las marcas del usuario cuyo ejecutable esté dentro de la carpeta dada.
        // Devuelve las rutas modificadas. Se usa antes de iniciar un juego, así no hace falta reiniciarlo después.
        public static List<string> RemoveUnder(string dir)
        {
            List<string> changed = new List<string>();
            try
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    return changed;
                }
                string prefix = dir.TrimEnd('\\') + "\\";
                List<string> names = new List<string>();
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(LayersPath))
                {
                    if (k == null)
                    {
                        return changed;
                    }
                    foreach (string name in k.GetValueNames())
                    {
                        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            names.Add(name);
                        }
                    }
                }
                foreach (string n in names)
                {
                    if (RemoveFromUser(n))
                    {
                        changed.Add(n);
                    }
                }
            }
            catch
            {
            }
            return changed;
        }

        // Quita solo RUNASADMIN (conserva el resto de opciones de compatibilidad). Devuelve true si lo logró.
        public static bool RemoveFromUser(string exePath)
        {
            try
            {
                string name = Find(Registry.CurrentUser, exePath);
                if (name == null)
                {
                    return false;
                }
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(LayersPath, true))
                {
                    if (k == null)
                    {
                        return false;
                    }
                    string v = (string)k.GetValue(name);
                    List<string> kept = new List<string>();
                    foreach (string t in v.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!string.Equals(t, "RUNASADMIN", StringComparison.OrdinalIgnoreCase) && t != "~")
                        {
                            kept.Add(t);
                        }
                    }
                    if (kept.Count == 0)
                    {
                        k.DeleteValue(name, false);
                    }
                    else
                    {
                        k.SetValue(name, "~ " + string.Join(" ", kept), RegistryValueKind.String);
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
