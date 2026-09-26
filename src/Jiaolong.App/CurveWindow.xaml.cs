using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Jiaolong.Core.Config;

namespace Jiaolong.App;

/// <summary>
/// Six-point fan curve editor. Edits the caller's <see cref="AppConfig"/> in place; the main
/// window applies it to the FanController when the dialog closes.
/// </summary>
public partial class CurveWindow : Window
{
    private const int N = 6;
    private readonly AppConfig _cfg;
    private readonly TextBox[] _temps = new TextBox[N];
    private readonly TextBox[] _rpms = new TextBox[N];

    public CurveWindow(AppConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        BuildRows();
        Redraw();
    }

    private void BuildRows()
    {
        var label = Res("LabelText");
        var row = MakeRow();
        AddCell(row, new TextBlock { Text = "点", Foreground = label, VerticalAlignment = VerticalAlignment.Center }, 0);
        AddCell(row, new TextBlock { Text = "温度 ℃", Foreground = label, VerticalAlignment = VerticalAlignment.Center }, 1);
        AddCell(row, new TextBlock { Text = "转速 RPM", Foreground = label, VerticalAlignment = VerticalAlignment.Center }, 2);
        row.Margin = new Thickness(0, 0, 0, 8);
        PointsPanel.Children.Add(row);

        for (int i = 0; i < N; i++)
        {
            var r = MakeRow();
            AddCell(r, new TextBlock
            {
                Text = $"#{i + 1}",
                Foreground = Res("SectionText"),
                VerticalAlignment = VerticalAlignment.Center,
            }, 0);

            var t = NewBox(_cfg.CurveTemps[i].ToString(CultureInfo.InvariantCulture));
            var m = NewBox(_cfg.CurveRpm[i].ToString(CultureInfo.InvariantCulture));
            t.TextChanged += (_, _) => Redraw();
            m.TextChanged += (_, _) => Redraw();
            _temps[i] = t;
            _rpms[i] = m;
            AddCell(r, t, 1);
            AddCell(r, m, 2);
            PointsPanel.Children.Add(r);
        }
    }

    private static Grid MakeRow() => new()
    {
        ColumnDefinitions =
        {
            new ColumnDefinition { Width = new GridLength(46) },
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
        },
    };

    private static void AddCell(Grid row, FrameworkElement cell, int col)
    {
        Grid.SetColumn(cell, col);
        row.Children.Add(cell);
        if (cell is TextBox tb) tb.Margin = new Thickness(0, 0, 10, 0);
    }

    private TextBox NewBox(string text) => new()
    {
        Style = (Style)FindResource("PointBox"),
        Text = text,
    };

    private Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    // ---------------------------------------------------------------- preview
    private void Redraw()
    {
        if (CurvePoly.Parent is not FrameworkElement host) return;
        double w = host.ActualWidth, h = host.ActualHeight;
        if (w < 20 || h < 20)
        {
            Dispatcher.BeginInvoke((Action)Redraw, DispatcherPriority.Loaded);
            return;
        }

        var temps = Read(_temps);
        var rpms = Read(_rpms);
        bool ascending = IsAscending(temps);
        PreviewNote.Text = ascending ? "" : "⚠ 温度必须递增";

        var points = new PointCollection();
        double tMin = 30, tMax = 105, rMin = 1800, rMax = 5800;
        for (int i = 0; i < N; i++)
        {
            double x = (temps[i] - tMin) / (tMax - tMin) * w;
            double y = h - (rpms[i] - rMin) / (rMax - rMin) * h;
            points.Add(new Point(Math.Clamp(x, 0, w), Math.Clamp(y, 0, h)));
        }
        CurvePoly.Points = points;
    }

    private static int[] Read(TextBox[] boxes)
    {
        var v = new int[boxes.Length];
        for (int i = 0; i < boxes.Length; i++)
        {
            if (!int.TryParse(boxes[i].Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) n = 0;
            v[i] = n;
        }
        return v;
    }

    private static bool IsAscending(int[] v)
    {
        for (int i = 1; i < v.Length; i++)
            if (v[i] <= v[i - 1]) return false;
        return true;
    }

    // ---------------------------------------------------------------- actions
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var temps = Read(_temps);
        var rpms = Read(_rpms);

        if (!IsAscending(temps))
        {
            PreviewNote.Text = "⚠ 温度必须严格递增";
            return;
        }

        for (int i = 0; i < N; i++)
        {
            temps[i] = Math.Clamp(temps[i], 30, 105);
            int r = Math.Clamp(rpms[i], 1800, 5800);
            rpms[i] = r - r % 100;
            _rpms[i].Text = rpms[i].ToString(CultureInfo.InvariantCulture);
        }

        Array.Copy(temps, _cfg.CurveTemps, N);
        Array.Copy(rpms, _cfg.CurveRpm, N);
        _cfg.CurveEnabled = true;
        DialogResult = true;
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cfg.CurveEnabled = false;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) return;
        try { DragMove(); } catch { /* released too fast */ }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        Jiaolong.App.Platform.Dwm.EnableRoundedCorners(hwnd);
        Jiaolong.App.Platform.Dwm.EnableDarkTitleBar(hwnd);
        Dispatcher.BeginInvoke((Action)Redraw, DispatcherPriority.Loaded);
    }
}
