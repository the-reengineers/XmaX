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
    private bool _showAxis;
    private int _gridlineInterval;

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

    /// <summary>Enable/disable axis lines display.</summary>
    public bool ShowAxis
    {
        get => _showAxis;
        set
        {
            _showAxis = value;
            Draw();
        }
    }

    /// <summary>Gridline interval (0 = no gridlines). Draws gridlines at specified interval for both axes, excluding edges.</summary>
    public int GridlineInterval
    {
        get => _gridlineInterval;
        set
        {
            _gridlineInterval = value;
            Draw();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Draw();
    }

    private void Draw()
    {
        ChartCanvas.Children.Clear();

        var w = this.ActualWidth - EdgePad * 2;
        var h = this.ActualHeight - EdgePad * 2;
        if (w <= 0 || h <= 0)
            return;

        // Draw axis lines if enabled
        if (_showAxis)
        {
            var axisBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

            // Horizontal axis (bottom)
            var hAxis = new Line
            {
                X1 = EdgePad,
                Y1 = EdgePad + h,
                X2 = EdgePad + w,
                Y2 = EdgePad + h,
                Stroke = axisBrush,
                StrokeThickness = 1,
            };
            ChartCanvas.Children.Add(hAxis);

            // Vertical axis (left)
            var vAxis = new Line
            {
                X1 = EdgePad,
                Y1 = EdgePad,
                X2 = EdgePad,
                Y2 = EdgePad + h,
                Stroke = axisBrush,
                StrokeThickness = 1,
            };
            ChartCanvas.Children.Add(vAxis);
        }

        // Draw gridlines if interval is set
        if (_gridlineInterval > 0)
        {
            var gridBrush = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

            // Y-axis gridlines (horizontal lines) - fan speed 0-100%
            for (int speed = _gridlineInterval; speed < 100; speed += _gridlineInterval)
            {
                var y = SpeedToY(speed, h);
                var gridLine = new Line
                {
                    X1 = EdgePad,
                    Y1 = y,
                    X2 = EdgePad + w,
                    Y2 = y,
                    Stroke = gridBrush,
                    StrokeThickness = 0.5,
                };
                ChartCanvas.Children.Add(gridLine);
            }

            // X-axis gridlines (vertical lines) - temperature 20-100°C
            for (int temp = 20 + _gridlineInterval; temp < 100; temp += _gridlineInterval)
            {
                var x = TempToX(temp, w);
                var gridLine = new Line
                {
                    X1 = x,
                    Y1 = EdgePad,
                    X2 = x,
                    Y2 = EdgePad + h,
                    Stroke = gridBrush,
                    StrokeThickness = 0.5,
                };
                ChartCanvas.Children.Add(gridLine);
            }
        }

        if (_curve == null || _curve.Points.Count < 2)
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
            StrokeThickness = 2,
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
