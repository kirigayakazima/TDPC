using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Jiaolong.App.Osd;

/// <summary>
/// Event toast overlay (top-centre). Displays brief on-screen notifications when
/// hardware states toggle (Caps/Num lock, performance mode switch, etc.).
/// </summary>
internal sealed class OsdOverlay : Window
{
    private readonly TextBlock _text;
    private readonly System.Windows.Threading.DispatcherTimer _hide;

    public OsdOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 120;

        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width / 2) - 200;
        Top = area.Top + 90;

        _text = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x15, 0x1B)),
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(34, 22, 34, 22),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        Content = new Border
        {
            // Margin gives the drop shadow room inside the window bounds; without it WPF clips
            // the effect at the window edge and you see hard square corners.
            Margin = new Thickness(26),
            Background = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(20),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00)),
            BorderThickness = new Thickness(1),
            Child = _text,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 30,
                ShadowDepth = 6,
                Direction = 270,
                Opacity = 0.28,
                Color = Colors.Black,
            },
        };

        _hide = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1700) };
        _hide.Tick += (_, _) => { _hide.Stop(); Hide(); };
    }

    public void ShowToast(string message)
    {
        _text.Text = message;
        if (!IsVisible) Show();
        _hide.Stop();
        _hide.Start();

        // SizeToContent resizes the window after layout, so re-centre once it has rendered;
        // otherwise the toast drifts left with every longer message.
        Dispatcher.BeginInvoke((Action)(() =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Top + 96;
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
