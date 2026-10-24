using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ConsoleOverlay
{
    // Pantalla de transición al cambiar de juego: tapa todo mientras se cierra el actual y arranca el nuevo,
    // así no se ven el escritorio ni Playnite entre medias. Es estática (sin animaciones continuas).
    internal sealed class SwitchSplash : Window
    {
        private bool closing;

        public SwitchSplash(string gameName, ImageSource cover, FontFamily font)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            Focusable = false;
            AllowsTransparency = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0F, 0x14));

            Grid root = new Grid { Width = 1920, Height = 1080, Background = Background };
            StackPanel col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            if (cover != null)
            {
                double w = 340;
                double h = cover.Width > 0 ? Math.Min(480, w * cover.Height / cover.Width) : 340;
                ImageBrush ib = new ImageBrush(cover) { Stretch = Stretch.UniformToFill };
                ib.Freeze();
                col.Children.Add(new Border
                {
                    Width = w,
                    Height = h,
                    CornerRadius = new CornerRadius(22),
                    Background = ib,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 36)
                });
            }
            col.Children.Add(new TextBlock
            {
                Text = Loc.T("Abriendo {0}…", gameName),
                FontSize = 40,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 1400
            });
            root.Children.Add(col);
            if (font != null)
            {
                System.Windows.Documents.TextElement.SetFontFamily(root, font);
            }
            Content = new Viewbox { Stretch = Stretch.Uniform, Child = root };
        }

        // Se desvanece (solo opacidad) y se cierra.
        public void FadeAndClose()
        {
            if (closing)
            {
                return;
            }
            closing = true;
            try
            {
                if (!UiFeedback.Animations)
                {
                    Close();
                    return;
                }
                DoubleAnimation fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
                fade.Completed += (s, e) =>
                {
                    try
                    {
                        Close();
                    }
                    catch
                    {
                    }
                };
                BeginAnimation(OpacityProperty, fade);
            }
            catch
            {
                try
                {
                    Close();
                }
                catch
                {
                }
            }
        }
    }
}
