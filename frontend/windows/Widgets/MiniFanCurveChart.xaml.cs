using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using XmaX.Models;

namespace XmaX.Widgets;

/// <summary>
/// Minimal fan curve chart for display inside profile tiles.
/// Transparent background, no gridlines, no labels, no axes — just the curve line.
/// X-axis: temperature 20-100°C. Y-axis: fan speed 0-100%.
/// </summary>
public sealed partial class MiniFanCurveChart : UserControl
{
    // Small padding to prevent line clipping at edges
    private const double EdgePad = 2;

    private FanCurve? _curve;

    public MiniFanCurveChart()
    {
        this.InitializeComponent();
        this.SizeChanged += OnSizeChanged;
    }

    /// <summary>Set the fan curve to display.</summary>
    public void SetCurve(FanCurve? curve)
    {
        _curve = curve;
        Draw();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Draw();
    }

    private void Draw()
    {
        ChartCanvas.Children.Clear();

        if (_curve == null || _curve.Points.Count < 2)
            return;

        var w = this.ActualWidth - EdgePad * 2;
        var h = this.ActualHeight - EdgePad * 2;
        if (w <= 0 || h <= 0)
            return;

        var sorted = _curve.Points.OrderBy(p => p.TempC).ToList();

        // Build polyline points — extend to full 20-100°C range
        var pc = new PointCollection();

        // Extend from left edge (20°C) to first point
        var first = sorted.First();
        pc.Add(new Point(TempToX(20, w), SpeedToY(first.SpeedPercent, h)));
        pc.Add(new Point(TempToX(first.TempC, w), SpeedToY(first.SpeedPercent, h)));

        // Curve points
        foreach (var p in sorted)
        {
            pc.Add(new Point(TempToX(p.TempC, w), SpeedToY(p.SpeedPercent, h)));
        }

        // Extend from last point to right edge (100°C)
        var last = sorted.Last();
        pc.Add(new Point(TempToX(last.TempC, w), SpeedToY(last.SpeedPercent, h)));
        pc.Add(new Point(TempToX(100, w), SpeedToY(last.SpeedPercent, h)));

        var line = new Polyline
        {
            Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            StrokeThickness = 1.5,
            Points = pc,
        };

        ChartCanvas.Children.Add(line);
    }

    /// <summary>Convert temperature (20-100) to X coordinate.</summary>
    private static double TempToX(int temp, double graphWidth)
    {
        var ratio = (double)(temp - 20) / (100 - 20);
        return EdgePad + ratio * graphWidth;
    }

    /// <summary>Convert speed (0-100) to Y coordinate (inverted: 0% at bottom).</summary>
    private static double SpeedToY(int speed, double graphHeight)
    {
        var ratio = (double)speed / 100.0;
        return EdgePad + graphHeight - ratio * graphHeight;
    }
}
