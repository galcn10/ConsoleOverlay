using System.Collections.Generic;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace ConsoleOverlay
{
    public class ConsoleOverlaySettings : ObservableObject, ISettings
    {
        private readonly ConsoleOverlayPlugin plugin;
        private ConsoleOverlaySettings editingClone;

        private string menuKey = "F10";
        private string endKey = "F11";
        private bool useCtrl = true;
        private bool useAlt = true;
        private bool useShift = false;
        private int longPressMs = 600;
        private bool confirmActions = true;
        private bool muteGame = true;
        private string restartCommand = "shutdown /r /t 0";
        private string shutdownCommand = "shutdown /s /t 0";
        private bool useGuideButton = true;
        private bool useChordButton = false;
        private bool firstRunDone = false;
        private string buttonStyle = "Auto";
        private string language = "auto";
        private string menuColors = "theme";
        private string capturePreset = "Ninguno";
        private string screenshotKeys = "";
        private string clipKeys = "";
        private string galleryKeys = "";
        private bool showBattery = true;
        private bool showLivePreview = true;
        private bool powerMenuOutsideGame = false;
        private int elevatedMode = 0;
        private bool noPauseAntiCheat = true;
        private bool autoRemoveAdmin = false;
        private string captureFolders = "";
        private bool keepGameInFront = true;
        private bool applying;

        public string MenuKey { get => menuKey; set => SetValue(ref menuKey, value); }
        public string EndKey { get => endKey; set => SetValue(ref endKey, value); }
        public bool UseCtrl { get => useCtrl; set => SetValue(ref useCtrl, value); }
        public bool UseAlt { get => useAlt; set => SetValue(ref useAlt, value); }
        public bool UseShift { get => useShift; set => SetValue(ref useShift, value); }
        public int LongPressMs { get => longPressMs; set => SetValue(ref longPressMs, value); }
        public bool ConfirmActions { get => confirmActions; set => SetValue(ref confirmActions, value); }
        public bool MuteGame { get => muteGame; set => SetValue(ref muteGame, value); }
        public string RestartCommand { get => restartCommand; set => SetValue(ref restartCommand, value); }
        public string ShutdownCommand { get => shutdownCommand; set => SetValue(ref shutdownCommand, value); }
        public bool UseGuideButton { get => useGuideButton; set => SetValue(ref useGuideButton, value); }
        // Select/Back + Start mantenidos ~1 s: segunda forma de abrir el menú con el mando (sin botón Guía).
        public bool UseChordButton { get => useChordButton; set => SetValue(ref useChordButton, value); }
        // Aviso de "así se abre el menú" ya mostrado.
        public bool FirstRunDone { get => firstRunDone; set => SetValue(ref firstRunDone, value); }
        public string ButtonStyle { get => buttonStyle; set => SetValue(ref buttonStyle, value); }
        public string Language { get => language; set => SetValue(ref language, string.IsNullOrEmpty(value) ? "auto" : value); }

        // Colores del fondo del menú: "theme" (los del tema de Playnite), "game" (los de la carátula) o "neutral".
        public string MenuColors { get => menuColors; set => SetValue(ref menuColors, string.IsNullOrEmpty(value) ? "theme" : value); }

        [DontSerialize]
        public List<LangOption> MenuColorOptions
        {
            get
            {
                return new List<LangOption>
                {
                    new LangOption { Code = "theme", Name = Loc.T("Tema de Playnite") },
                    new LangOption { Code = "game", Name = Loc.T("Color del juego") },
                    new LangOption { Code = "neutral", Name = Loc.T("Neutro") }
                };
            }
        }

        [DontSerialize]
        public List<LangOption> LanguageOptions
        {
            get
            {
                List<LangOption> l = new List<LangOption> { new LangOption { Code = "auto", Name = Loc.T("Automático (idioma de Playnite)") } };
                foreach (string[] x in Loc.Languages)
                {
                    l.Add(new LangOption { Code = x[0], Name = x[1] });
                }
                return l;
            }
        }
        public string CaptureFolders { get => captureFolders; set => SetValue(ref captureFolders, value); }
        // Juegos que se ejecutan como administrador (no se pueden pausar ni pasar el foco desde un Playnite normal):
        // 0 = abrir el menú encima del juego sin pausarlo, 1 = solo avisar en pantalla.
        public bool NoPauseAntiCheat { get => noPauseAntiCheat; set => SetValue(ref noPauseAntiCheat, value); }
        public bool AutoRemoveAdmin { get => autoRemoveAdmin; set => SetValue(ref autoRemoveAdmin, value); }
        public int ElevatedMode { get => elevatedMode; set => SetValue(ref elevatedMode, value < 0 || value > 1 ? 0 : value); }
        public bool PowerMenuOutsideGame { get => powerMenuOutsideGame; set => SetValue(ref powerMenuOutsideGame, value); }
        public bool ShowBattery { get => showBattery; set => SetValue(ref showBattery, value); }
        public bool ShowLivePreview { get => showLivePreview; set => SetValue(ref showLivePreview, value); }
        public bool KeepGameInFront { get => keepGameInFront; set => SetValue(ref keepGameInFront, value); }

        public string CapturePreset
        {
            get => capturePreset;
            set
            {
                string old = capturePreset;
                SetValue(ref capturePreset, value);
                if (!applying && value != old)
                {
                    ApplyPreset(value);
                }
            }
        }
        public string ScreenshotKeys { get => screenshotKeys; set => SetValue(ref screenshotKeys, value); }
        public string ClipKeys { get => clipKeys; set => SetValue(ref clipKeys, value); }
        public string GalleryKeys { get => galleryKeys; set => SetValue(ref galleryKeys, value); }

        [DontSerialize]
        public List<string> PresetOptions { get; } = new List<string>
        {
            "Ninguno", "GameHQ", "NVIDIA App", "Steam", "Xbox Game Bar", "Personalizado"
        };

        // Atajos habituales de cada programa. Son editables: cada usuario puede haberlos cambiado.
        private void ApplyPreset(string preset)
        {
            switch (preset)
            {
                case "GameHQ":
                    ScreenshotKeys = "Ctrl+Shift+S";
                    ClipKeys = "Ctrl+Shift+E";
                    GalleryKeys = "Ctrl+Shift+G";
                    break;
                case "NVIDIA App":
                    ScreenshotKeys = "Alt+F1";
                    ClipKeys = "Alt+F10";
                    GalleryKeys = "";
                    break;
                case "Steam":
                    ScreenshotKeys = "F12";
                    ClipKeys = "";
                    GalleryKeys = "";
                    break;
                case "Xbox Game Bar":
                    ScreenshotKeys = "Win+Alt+PrtScn";
                    ClipKeys = "Win+Alt+G";
                    GalleryKeys = "";
                    break;
                case "Ninguno":
                    ScreenshotKeys = "";
                    ClipKeys = "";
                    GalleryKeys = "";
                    break;
            }
        }

        [DontSerialize]
        public List<string> ButtonStyleOptions { get; } = new List<string> { "Auto", "Xbox", "PlayStation", "Nintendo" };

        [DontSerialize]
        public List<string> KeyOptions { get; } = new List<string>
        {
            "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12"
        };

        [DontSerialize]
        public int MenuVk => KeyToVk(MenuKey);

        [DontSerialize]
        public int EndVk => KeyToVk(EndKey);

        // Constructor sin parámetros: lo necesita el sistema de guardado de Playnite.
        public ConsoleOverlaySettings()
        {
        }

        public ConsoleOverlaySettings(ConsoleOverlayPlugin plugin)
        {
            this.plugin = plugin;
            ConsoleOverlaySettings saved = plugin.LoadPluginSettings<ConsoleOverlaySettings>();
            if (saved != null)
            {
                CopyFrom(saved);
            }
        }

        public static int KeyToVk(string key)
        {
            int n;
            if (!string.IsNullOrEmpty(key) && key.StartsWith("F") &&
                int.TryParse(key.Substring(1), out n) && n >= 1 && n <= 12)
            {
                return 0x6F + n;
            }
            return 0x79;
        }

        public string Combo(string key)
        {
            string s = "";
            if (UseCtrl) s += "Ctrl+";
            if (UseAlt) s += "Alt+";
            if (UseShift) s += Loc.T("Mayús") + "+";
            return s + key;
        }

        private void CopyFrom(ConsoleOverlaySettings o)
        {
            MenuKey = o.MenuKey;
            EndKey = o.EndKey;
            UseCtrl = o.UseCtrl;
            UseAlt = o.UseAlt;
            UseShift = o.UseShift;
            LongPressMs = o.LongPressMs;
            ConfirmActions = o.ConfirmActions;
            MuteGame = o.MuteGame;
            ShowBattery = o.ShowBattery;
            ShowLivePreview = o.ShowLivePreview;
            PowerMenuOutsideGame = o.PowerMenuOutsideGame;
            ElevatedMode = o.ElevatedMode;
            NoPauseAntiCheat = o.NoPauseAntiCheat;
            AutoRemoveAdmin = o.AutoRemoveAdmin;
            CaptureFolders = o.CaptureFolders ?? "";
            KeepGameInFront = o.KeepGameInFront;
            RestartCommand = o.RestartCommand;
            ShutdownCommand = o.ShutdownCommand;
            UseGuideButton = o.UseGuideButton;
            UseChordButton = o.UseChordButton;
            FirstRunDone = o.FirstRunDone;
            ButtonStyle = string.IsNullOrEmpty(o.ButtonStyle) ? "Auto" : o.ButtonStyle;
            Language = o.Language;
            MenuColors = o.MenuColors;
            applying = true;
            try
            {
                CapturePreset = string.IsNullOrEmpty(o.CapturePreset) ? "Ninguno" : o.CapturePreset;
                ScreenshotKeys = o.ScreenshotKeys ?? "";
                ClipKeys = o.ClipKeys ?? "";
                GalleryKeys = o.GalleryKeys ?? "";
            }
            finally
            {
                applying = false;
            }
        }

        public void BeginEdit()
        {
            editingClone = new ConsoleOverlaySettings();
            editingClone.CopyFrom(this);
        }

        public void CancelEdit()
        {
            if (editingClone != null)
            {
                CopyFrom(editingClone);
            }
        }

        public void EndEdit()
        {
            plugin.SavePluginSettings(this);
            plugin.ApplySettings();
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            if (LongPressMs < 300 || LongPressMs > 3000)
            {
                errors.Add(Loc.T("La pulsación larga debe estar entre 300 y 3000 ms."));
            }
            if (MenuKey == EndKey)
            {
                errors.Add(Loc.T("La tecla del menú y la de finalizar juego no pueden ser la misma."));
            }
            if (!string.IsNullOrWhiteSpace(ScreenshotKeys) && !KeyChord.IsValid(ScreenshotKeys))
            {
                errors.Add(Loc.T("El atajo de captura no es válido. Ejemplo: Ctrl+Shift+S."));
            }
            if (!string.IsNullOrWhiteSpace(ClipKeys) && !KeyChord.IsValid(ClipKeys))
            {
                errors.Add(Loc.T("El atajo de guardar clip no es válido. Ejemplo: Ctrl+Shift+E."));
            }
            if (!string.IsNullOrWhiteSpace(GalleryKeys) && !KeyChord.IsValid(GalleryKeys))
            {
                errors.Add(Loc.T("El atajo de ver capturas no es válido. Ejemplo: Ctrl+Shift+G."));
            }
            if (string.IsNullOrWhiteSpace(RestartCommand) || string.IsNullOrWhiteSpace(ShutdownCommand))
            {
                errors.Add(Loc.T("Los comandos de reiniciar y apagar no pueden estar vacíos."));
            }
            return errors.Count == 0;
        }
    }
}
