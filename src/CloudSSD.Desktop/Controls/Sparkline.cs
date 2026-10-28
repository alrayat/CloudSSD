using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CloudSSD.Desktop.Controls;

/// <summary>
/// Minimal time-series chart. Renders once per data update with a single StreamGeometry, so it costs almost nothing
/// while a game is running. Null samples are drawn as gaps ("Unavailable"), never interpolated.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(double?[]), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(nameof(MaxValue), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(nameof(Capacity), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(120, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(nameof(Format), typeof(Func<double, string>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double?[]? Values { get => (double?[]?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    /// <summary>Fixed maximum (0 = auto-scale to the visible data).</summary>
    public double MaxValue { get => (double)GetValue(MaxValueProperty); set => SetValue(MaxValueProperty, value); }
    /// <summary>Number of most recent samples shown.</summary>
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
    public Func<double, string>? Format { get => (Func<double, string>?)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }

    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1) { DashStyle = DashStyles.Dot });
    private static readonly Typeface Face = new("Segoe UI");

    private static Pen Freeze(Pen p)
    {
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 4 || h < 4) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        for (int i = 1; i <= 3; i++) dc.DrawLine(GridPen, new Point(0, h * i / 4.0), new Point(w, h * i / 4.0));

        var all = Values ?? [];
        var data = all.Length > Capacity ? all[^Capacity..] : all;
        if (data.Length == 0 || data.All(v => v is null))
        {
            var ft = Text("Unavailable", Brushes.Gray);
            dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
            return;
        }
        var max = MaxValue > 0 ? MaxValue : Math.Max(1e-9, data.Where(v => v.HasValue).Max(v => v!.Value) * 1.15);
        var step = w / Math.Max(1, Capacity - 1);
        var offset = (Capacity - data.Length) * step;

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            bool open = false;
            Point first = default, last = default;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] is not { } v)
                {
                    if (open) { ac.LineTo(new Point(last.X, h), false, false); ac.LineTo(new Point(first.X, h), false, false); }
                    open = false;
                    continue;
                }
                var p = new Point(offset + i * step, h - Math.Clamp(v / max, 0, 1) * (h - 2) - 1);
                if (!open)
                {
                    lc.BeginFigure(p, false, false);
                    ac.BeginFigure(p, true, true);
                    first = p;
                    open = true;
                }
                else
                {
                    lc.LineTo(p, true, true);
                    ac.LineTo(p, false, false);
                }
                last = p;
            }
            if (open) { ac.LineTo(new Point(last.X, h), false, false); ac.LineTo(new Point(first.X, h), false, false); }
        }
        line.Freeze();
        area.Freeze();
        var fill = Stroke.Clone();
        fill.Opacity = 0.12;
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);
        var pen = new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);

        if (Format != null)
        {
            var label = Text("max " + Format(max / 1.15), Brushes.Gray);
            dc.DrawText(label, new Point(w - label.Width - 2, 2));
        }
    }

    private FormattedText Text(string s, Brush b) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 11, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is bool v && v || value is int i && i > 0 || value is string s && s.Length > 0 || (value != null && value is not bool and not int and not string);
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Maps semantic tone names ("ok", "warn", "error", "info", "muted", "accent") to theme brushes.</summary>
public sealed class ToneToBrush : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is CloudSSD.Core.Severity sev)
            value = sev switch { CloudSSD.Core.Severity.Error => "error", CloudSSD.Core.Severity.Warning => "warn", _ => "muted" };
        var key = (value as string) switch
        {
            "ok" => "Success",
            "warn" => "Warning",
            "error" => "Danger",
            "accent" => "Accent",
            "purple" => "Purple",
            "muted" => "Dim",
            _ => "Muted"
        };
        return Application.Current.FindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
