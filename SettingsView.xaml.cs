using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace ConsoleOverlay
{
    public partial class ConsoleOverlaySettingsView : UserControl
    {
        public ConsoleOverlaySettingsView()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                if (!translated)
                {
                    ApplyThemeText();
                    translated = true;
                    Translate(this);
                    BuildIndex();
                }
            };
        }

        private bool translated;

        // Los textos sin estilo propio salían con el color por defecto (oscuro) sobre el fondo del tema: se toman
        // el color y la fuente del estilo de texto del tema de Playnite para que se lean como los demás complementos.
        private void ApplyThemeText()
        {
            try
            {
                // «Idioma:» no lleva estilo propio, así que ya sale con el color y la fuente del tema: se copian a todo lo demás.
                if (ThemeProbe != null && ThemeProbe.Foreground != null)
                {
                    Foreground = ThemeProbe.Foreground;
                    FontFamily = ThemeProbe.FontFamily;
                }
            }
            catch
            {
            }
        }

        // Texto de cada ajuste (ya traducido, sin tildes ni mayúsculas) para el buscador.
        private readonly Dictionary<FrameworkElement, string> rowText = new Dictionary<FrameworkElement, string>();

        private void BuildIndex()
        {
            foreach (TabItem tab in Tabs.Items.OfType<TabItem>())
            {
                CollectRows(tab, tab);
            }
        }

        private void CollectRows(DependencyObject node, TabItem tab)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                FrameworkElement fe = child as FrameworkElement;
                if (fe == null)
                {
                    continue;
                }
                if (fe.Tag as string == "row")
                {
                    StringBuilder sb = new StringBuilder();
                    GatherText(fe, sb);
                    rowText[fe] = Normalize(sb.ToString());
                }
                else
                {
                    CollectRows(fe, tab);
                }
            }
        }

        private static void GatherText(DependencyObject node, StringBuilder sb)
        {
            TextBlock tb = node as TextBlock;
            if (tb != null && !string.IsNullOrEmpty(tb.Text))
            {
                sb.Append(tb.Text).Append(' ');
            }
            ContentControl cc = node as ContentControl;
            if (cc != null && cc.Content is string)
            {
                sb.Append((string)cc.Content).Append(' ');
            }
            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                DependencyObject d = child as DependencyObject;
                if (d != null)
                {
                    GatherText(d, sb);
                }
            }
        }

        private static string Normalize(string t)
        {
            string d = (t ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
            StringBuilder sb = new StringBuilder();
            foreach (char c in d)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        // Buscador: oculta los ajustes que no coinciden y las pestañas que se quedan vacías.
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string q = Normalize(SearchBox.Text.Trim());
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!translated)
            {
                return;
            }
            bool any = false;
            TabItem firstShown = null;
            foreach (TabItem tab in Tabs.Items.OfType<TabItem>())
            {
                int shown = 0;
                foreach (KeyValuePair<FrameworkElement, string> kv in rowText)
                {
                    if (!IsInside(kv.Key, tab))
                    {
                        continue;
                    }
                    bool vis = q.Length == 0 || kv.Value.Contains(q);
                    kv.Key.Visibility = vis ? Visibility.Visible : Visibility.Collapsed;
                    if (vis)
                    {
                        shown++;
                    }
                }
                tab.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (shown > 0)
                {
                    any = true;
                    if (firstShown == null)
                    {
                        firstShown = tab;
                    }
                }
            }
            NoResults.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            TabItem cur = Tabs.SelectedItem as TabItem;
            if (firstShown != null && (cur == null || cur.Visibility != Visibility.Visible))
            {
                Tabs.SelectedItem = firstShown;
            }
        }

        private static bool IsInside(DependencyObject node, TabItem tab)
        {
            DependencyObject p = node;
            while (p != null)
            {
                if (ReferenceEquals(p, tab))
                {
                    return true;
                }
                p = LogicalTreeHelper.GetParent(p);
            }
            return false;
        }

        // Los textos del XAML están en español y hacen de clave; se traducen al idioma elegido.
        private static void Translate(DependencyObject node)
        {
            TextBlock tb = node as TextBlock;
            if (tb != null && !string.IsNullOrEmpty(tb.Text) && !BindingOperations.IsDataBound(tb, TextBlock.TextProperty))
            {
                tb.Text = Loc.T(tb.Text);
            }
            ContentControl cc = node as ContentControl;
            if (cc != null)
            {
                string c = cc.Content as string;
                if (!string.IsNullOrEmpty(c))
                {
                    cc.Content = Loc.T(c);
                }
            }
            HeaderedContentControl hc = node as HeaderedContentControl;
            if (hc != null)
            {
                string h = hc.Header as string;
                if (!string.IsNullOrEmpty(h))
                {
                    hc.Header = Loc.T(h);
                }
            }
            FrameworkElement fe = node as FrameworkElement;
            if (fe != null)
            {
                string tip = fe.ToolTip as string;
                if (!string.IsNullOrEmpty(tip))
                {
                    fe.ToolTip = Loc.T(tip);
                }
            }
            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                DependencyObject d = child as DependencyObject;
                if (d != null)
                {
                    Translate(d);
                }
            }
        }
    }
}
