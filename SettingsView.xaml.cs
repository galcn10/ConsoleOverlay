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
            Loaded += (s, e) => Translate(this);
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
