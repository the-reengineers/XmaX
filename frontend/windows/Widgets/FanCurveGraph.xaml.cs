using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using XmaX.Models;
using Microsoft.UI.Input;

namespace XmaX.Widgets;

/// <summary>
/// Interactive graph control for visualizing and editing fan curves with draggable points.
/// Points snap to configurable increments (default: 5°C for temperature, 5% for speed).
/// </summary>
public sealed partial class FanCurveGraph : UserControl
{
    // Padding around the graph area for labels
    private const double PaddingLeft = 40;
    private const double PaddingRight = 10;
    private const double PaddingTop = 10;
    private const double PaddingBottom = 40;

    // Point visual properties
    private const double PointRadius = 6;
    private const double PointBorderThickness = 2;

    // Point count limits
    private const int MinPoints = 2;
    private const int MaxPoints = 10;

    // Drag state
    private FanCurvePoint? _draggingPoint;
    private Ellipse? _draggingEllipse;
    private Point _dragOffset;

    // Cached UI elements for efficient updates
    private Polyline? _polyline;
    private Line? _firstExtensionLine;
    private Line? _lastExtensionLine;
    private readonly Dictionary<FanCurvePoint, Ellipse> _pointEllipses = new();

    // Tooltip state
    private FanCurvePoint? _hoveredPoint;

    // Delete button state
    private FanCurvePoint? _selectedPointForDeletion;

    // Add point state
    private FanCurvePoint? _pendingAddPoint;
    private Flyout? _addFlyout;

    /// <summary>
    /// Dependency property for the collection of fan curve points.
    /// </summary>
    public static readonly DependencyProperty PointsProperty =
        DependencyProperty.Register(
            nameof(Points),
            typeof(ObservableCollection<FanCurvePoint>),
            typeof(FanCurveGraph),
            new PropertyMetadata(null, OnPointsChanged));

    /// <summary>
    /// Dependency property for minimum temperature (x-axis).
    /// </summary>
    public static readonly DependencyProperty TempMinProperty =
        DependencyProperty.Register(
            nameof(TempMin),
            typeof(int),
            typeof(FanCurveGraph),
            new PropertyMetadata(20, OnRangeChanged));

    /// <summary>
    /// Dependency property for maximum temperature (x-axis).
    /// </summary>
    public static readonly DependencyProperty TempMaxProperty =
        DependencyProperty.Register(
            nameof(TempMax),
            typeof(int),
            typeof(FanCurveGraph),
            new PropertyMetadata(100, OnRangeChanged));

    /// <summary>
    /// Dependency property for minimum fan speed (y-axis).
    /// </summary>
    public static readonly DependencyProperty SpeedMinProperty =
        DependencyProperty.Register(
            nameof(SpeedMin),
            typeof(int),
            typeof(FanCurveGraph),
            new PropertyMetadata(0, OnRangeChanged));

    /// <summary>
    /// Dependency property for maximum fan speed (y-axis).
    /// </summary>
    public static readonly DependencyProperty SpeedMaxProperty =
        DependencyProperty.Register(
            nameof(SpeedMax),
            typeof(int),
            typeof(FanCurveGraph),
            new PropertyMetadata(100, OnRangeChanged));

    /// <summary>
    /// Dependency property for temperature snap increment.
    /// </summary>
    public static readonly DependencyProperty SnapTempProperty =
        DependencyProperty.Register(
            nameof(SnapTemp),
            typeof(int),
            typeof(FanCurveGraph),
            new PropertyMetadata(5));

    /// <summary>
    /// Dependency property for fan speed snap increment.
    /// </summary>
    public static readonly DependencyProperty SnapSpeedProperty =
        DependencyProperty.Register(
            nameof(SnapSpeed),
            typeof(int),
            typeof(FanCurveGraph),
            new PropertyMetadata(5));

    /// <summary>
    /// Gets or sets the collection of fan curve points to display and edit.
    /// </summary>
    public ObservableCollection<FanCurvePoint> Points
    {
        get => (ObservableCollection<FanCurvePoint>)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <summary>
    /// Gets or sets the minimum temperature value (x-axis start).
    /// </summary>
    public int TempMin
    {
        get => (int)GetValue(TempMinProperty);
        set => SetValue(TempMinProperty, value);
    }

    /// <summary>
    /// Gets or sets the maximum temperature value (x-axis end).
    /// </summary>
    public int TempMax
    {
        get => (int)GetValue(TempMaxProperty);
        set => SetValue(TempMaxProperty, value);
    }

    /// <summary>
    /// Gets or sets the minimum fan speed value (y-axis start, displayed at bottom).
    /// </summary>
    public int SpeedMin
    {
        get => (int)GetValue(SpeedMinProperty);
        set => SetValue(SpeedMinProperty, value);
    }

    /// <summary>
    /// Gets or sets the maximum fan speed value (y-axis end, displayed at top).
    /// </summary>
    public int SpeedMax
    {
        get => (int)GetValue(SpeedMaxProperty);
        set => SetValue(SpeedMaxProperty, value);
    }

    /// <summary>
    /// Gets or sets the temperature snap increment in degrees Celsius.
    /// </summary>
    public int SnapTemp
    {
        get => (int)GetValue(SnapTempProperty);
        set => SetValue(SnapTempProperty, value);
    }

    /// <summary>
    /// Gets or sets the fan speed snap increment in percent.
    /// </summary>
    public int SnapSpeed
    {
        get => (int)GetValue(SnapSpeedProperty);
        set => SetValue(SnapSpeedProperty, value);
    }

    /// <summary>
    /// Event raised when a point is changed (dragged).
    /// </summary>
    public event EventHandler<FanCurvePoint>? PointChanged;

    public FanCurveGraph()
    {
        this.InitializeComponent();

        // Set default size
        this.Width = 300;
        this.Height = 300;

        // Redraw on size change
        this.SizeChanged += OnSizeChanged;

        // Right-click on empty graph area to add a point
        RootGrid.RightTapped += OnGraphRightTapped;

        // Double-click on empty graph area to add a point immediately
        RootGrid.DoubleTapped += OnGraphDoubleTapped;

        // Initial draw
        this.Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DrawAll();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawAll();
    }

    private static void OnPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FanCurveGraph graph)
        {
            // Unsubscribe from old collection
            if (e.OldValue is ObservableCollection<FanCurvePoint> oldCollection)
            {
                oldCollection.CollectionChanged -= graph.OnPointsCollectionChanged;
            }

            // Subscribe to new collection
            if (e.NewValue is ObservableCollection<FanCurvePoint> newCollection)
            {
                newCollection.CollectionChanged += graph.OnPointsCollectionChanged;
            }

            graph.DrawPoints();
        }
    }

    private void OnPointsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DrawAll();
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FanCurveGraph graph)
        {
            graph.DrawAll();
        }
    }

    /// <summary>
    /// Refreshes the entire graph visualization.
    /// </summary>
    public void Refresh()
    {
        DrawAll();
    }

    private void DrawAll()
    {
        GridCanvas.Children.Clear();
        PointsCanvas.Children.Clear();
        DrawGrid();
        DrawAxes();
        DrawPoints();
    }

    private void DrawGrid()
    {
        var graphWidth = this.Width - PaddingLeft - PaddingRight;
        var graphHeight = this.Height - PaddingTop - PaddingBottom;

        // Draw danger zone (85-100°C)
        var dangerLeft = TempToX(85);
        var dangerRight = TempToX(100);
        var dangerZone = new Rectangle
        {
            Width = dangerRight - dangerLeft,
            Height = graphHeight,
            Fill = new SolidColorBrush(Color.FromArgb(30, 255, 0, 0)),
        };
        Canvas.SetLeft(dangerZone, dangerLeft);
        Canvas.SetTop(dangerZone, PaddingTop);
        GridCanvas.Children.Add(dangerZone);

        // Draw vertical grid lines (temperature)
        for (int temp = TempMin; temp <= TempMax; temp += SnapTemp)
        {
            var x = TempToX(temp);
            var line = new Line
            {
                X1 = x,
                Y1 = PaddingTop,
                X2 = x,
                Y2 = PaddingTop + graphHeight,
                Stroke = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                StrokeThickness = 1,
            };
            GridCanvas.Children.Add(line);
        }

        // Draw horizontal grid lines (speed)
        for (int speed = SpeedMin; speed <= SpeedMax; speed += SnapSpeed)
        {
            var y = SpeedToY(speed);
            var line = new Line
            {
                X1 = PaddingLeft,
                Y1 = y,
                X2 = PaddingLeft + graphWidth,
                Y2 = y,
                Stroke = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                StrokeThickness = 1,
            };
            GridCanvas.Children.Add(line);
        }
    }

    private void DrawAxes()
    {
        var graphWidth = this.Width - PaddingLeft - PaddingRight;
        var graphHeight = this.Height - PaddingTop - PaddingBottom;

        // Draw border
        var border = new Rectangle
        {
            Width = graphWidth,
            Height = graphHeight,
            Stroke = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
            StrokeThickness = 1,
        };
        Canvas.SetLeft(border, PaddingLeft);
        Canvas.SetTop(border, PaddingTop);
        GridCanvas.Children.Add(border);

        // X-axis label
        var xLabel = new TextBlock
        {
            Text = Loc.Label_TemperatureAxis,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
        };
        xLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(xLabel, PaddingLeft + (graphWidth - xLabel.DesiredSize.Width) / 2);
        Canvas.SetTop(xLabel, this.Height - 15);
        GridCanvas.Children.Add(xLabel);

        // Y-axis label (vertical text, one character per line)
        var yLabelText = Loc.Label_FanAxis;
        var yLabel = new TextBlock
        {
            Text = yLabelText,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            TextAlignment = TextAlignment.Center,
        };
        yLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(yLabel, -10);
        Canvas.SetTop(yLabel, PaddingTop + (graphHeight - yLabel.DesiredSize.Height) / 2);
        GridCanvas.Children.Add(yLabel);

        // X-axis tick labels
        for (int temp = TempMin; temp <= TempMax; temp += 20)
        {
            var x = TempToX(temp);
            var tickLabel = new TextBlock
            {
                Text = temp.ToString(),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            };
            tickLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tickLabel, x - tickLabel.DesiredSize.Width / 2);
            Canvas.SetTop(tickLabel, PaddingTop + graphHeight + 5);
            GridCanvas.Children.Add(tickLabel);
        }

        // Y-axis tick labels
        for (int speed = SpeedMin; speed <= SpeedMax; speed += 20)
        {
            var y = SpeedToY(speed);
            var tickLabel = new TextBlock
            {
                Text = speed.ToString(),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            };
            tickLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tickLabel, PaddingLeft - tickLabel.DesiredSize.Width - 5);
            Canvas.SetTop(tickLabel, y - tickLabel.DesiredSize.Height / 2);
            GridCanvas.Children.Add(tickLabel);
        }
    }

    private void DrawPoints()
    {
        // Clear only the points layer and cache
        PointsCanvas.Children.Clear();
        _pointEllipses.Clear();
        _polyline = null;
        _firstExtensionLine = null;
        _lastExtensionLine = null;

        if (Points == null || Points.Count == 0) return;

        var sortedPoints = Points.OrderBy(p => p.TempC).ToList();

        // Create polyline for connecting lines
        if (sortedPoints.Count > 1)
        {
            _polyline = new Polyline
            {
                Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                StrokeThickness = 2,
            };

            foreach (var point in sortedPoints)
            {
                _polyline.Points.Add(new Point(TempToX(point.TempC), SpeedToY(point.SpeedPercent)));
            }

            PointsCanvas.Children.Add(_polyline);
        }

        // Horizontal extension lines from first/last points to graph edges
        if (sortedPoints.Count > 0)
        {
            var accentBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

            var first = sortedPoints[0];
            _firstExtensionLine = new Line
            {
                X1 = TempToX(TempMin),
                Y1 = SpeedToY(first.SpeedPercent),
                X2 = TempToX(first.TempC),
                Y2 = SpeedToY(first.SpeedPercent),
                Stroke = accentBrush,
                StrokeThickness = 2,
            };
            PointsCanvas.Children.Add(_firstExtensionLine);

            var last = sortedPoints[^1];
            _lastExtensionLine = new Line
            {
                X1 = TempToX(last.TempC),
                Y1 = SpeedToY(last.SpeedPercent),
                X2 = TempToX(TempMax),
                Y2 = SpeedToY(last.SpeedPercent),
                Stroke = accentBrush,
                StrokeThickness = 2,
            };
            PointsCanvas.Children.Add(_lastExtensionLine);
        }

        // Create ellipses for each point
        foreach (var point in sortedPoints)
        {
            var x = TempToX(point.TempC);
            var y = SpeedToY(point.SpeedPercent);

            var ellipse = new Ellipse
            {
                Width = PointRadius * 2,
                Height = PointRadius * 2,
                Fill = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = PointBorderThickness,
                Tag = point,
            };

            Canvas.SetLeft(ellipse, x - PointRadius);
            Canvas.SetTop(ellipse, y - PointRadius);

            // Add pointer events for dragging and hover
            ellipse.PointerPressed += OnPointPointerPressed;
            ellipse.PointerMoved += OnPointPointerMoved;
            ellipse.PointerReleased += OnPointPointerReleased;
            ellipse.PointerEntered += OnPointPointerEntered;
            ellipse.PointerExited += OnPointPointerExited;
            ellipse.RightTapped += OnPointRightTapped;

            PointsCanvas.Children.Add(ellipse);
            _pointEllipses[point] = ellipse;
        }
    }

    /// <summary>
    /// Updates only the position of a dragged point and its polyline segment (efficient for drag operations).
    /// </summary>
    private void UpdatePointPosition(FanCurvePoint point)
    {
        if (!_pointEllipses.TryGetValue(point, out var ellipse)) return;

        var x = TempToX(point.TempC);
        var y = SpeedToY(point.SpeedPercent);

        // Update ellipse position
        Canvas.SetLeft(ellipse, x - PointRadius);
        Canvas.SetTop(ellipse, y - PointRadius);

        if (Points == null) return;

        var sortedPoints = Points.OrderBy(p => p.TempC).ToList();

        // Update polyline if it exists
        if (_polyline != null)
        {
            _polyline.Points.Clear();
            foreach (var p in sortedPoints)
            {
                _polyline.Points.Add(new Point(TempToX(p.TempC), SpeedToY(p.SpeedPercent)));
            }
        }

        // Update extension lines if this is the first or last point
        if (sortedPoints.Count > 0)
        {
            if (_firstExtensionLine != null)
            {
                var first = sortedPoints[0];
                _firstExtensionLine.X1 = TempToX(TempMin);
                _firstExtensionLine.Y1 = SpeedToY(first.SpeedPercent);
                _firstExtensionLine.X2 = TempToX(first.TempC);
                _firstExtensionLine.Y2 = SpeedToY(first.SpeedPercent);
            }

            if (_lastExtensionLine != null)
            {
                var last = sortedPoints[^1];
                _lastExtensionLine.X1 = TempToX(last.TempC);
                _lastExtensionLine.Y1 = SpeedToY(last.SpeedPercent);
                _lastExtensionLine.X2 = TempToX(TempMax);
                _lastExtensionLine.Y2 = SpeedToY(last.SpeedPercent);
            }
        }
    }

    #region Coordinate Transformation

    /// <summary>
    /// Converts a temperature value to canvas X coordinate.
    /// </summary>
    private double TempToX(int temp)
    {
        var graphWidth = this.Width - PaddingLeft - PaddingRight;
        var ratio = (double)(temp - TempMin) / (TempMax - TempMin);
        return PaddingLeft + ratio * graphWidth;
    }

    /// <summary>
    /// Converts a speed value to canvas Y coordinate (inverted: 0% at bottom).
    /// </summary>
    private double SpeedToY(int speed)
    {
        var graphHeight = this.Height - PaddingTop - PaddingBottom;
        var ratio = (double)(speed - SpeedMin) / (SpeedMax - SpeedMin);
        return PaddingTop + graphHeight - ratio * graphHeight;
    }

    /// <summary>
    /// Converts canvas X coordinate to temperature value with snapping.
    /// </summary>
    private int XToTemp(double x)
    {
        var graphWidth = this.Width - PaddingLeft - PaddingRight;
        var ratio = (x - PaddingLeft) / graphWidth;
        var temp = TempMin + ratio * (TempMax - TempMin);
        return SnapTemperature((int)temp);
    }

    /// <summary>
    /// Converts canvas Y coordinate to speed value with snapping.
    /// </summary>
    private int YToSpeed(double y)
    {
        var graphHeight = this.Height - PaddingTop - PaddingBottom;
        var ratio = (PaddingTop + graphHeight - y) / graphHeight;
        var speed = SpeedMin + ratio * (SpeedMax - SpeedMin);
        return SnapSpeedValue((int)speed);
    }

    /// <summary>
    /// Snaps temperature to nearest SnapTemp increment.
    /// </summary>
    private int SnapTemperature(int temp)
    {
        return (int)(Math.Round((double)temp / SnapTemp) * SnapTemp);
    }

    /// <summary>
    /// Snaps speed to nearest SnapSpeed increment.
    /// </summary>
    private int SnapSpeedValue(int speed)
    {
        return (int)(Math.Round((double)speed / SnapSpeed) * SnapSpeed);
    }

    #endregion

    #region Drag Handling

    private void OnPointPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Ellipse ellipse && ellipse.Tag is FanCurvePoint point)
        {
            _draggingPoint = point;
            _draggingEllipse = ellipse;

            // Calculate offset from point center to cursor
            var position = e.GetCurrentPoint(PointsCanvas).Position;
            var centerX = Canvas.GetLeft(ellipse) + PointRadius;
            var centerY = Canvas.GetTop(ellipse) + PointRadius;
            _dragOffset = new Point(centerX - position.X, centerY - position.Y);

            // Capture pointer
            ellipse.CapturePointer(e.Pointer);

            // Show tooltip during drag
            ShowTooltip(point);

            e.Handled = true;
        }
    }

    private void OnPointPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingPoint != null && _draggingEllipse != null)
        {
            var position = e.GetCurrentPoint(PointsCanvas).Position;

            // Apply offset and convert to temp/speed
            var adjustedX = position.X + _dragOffset.X;
            var adjustedY = position.Y + _dragOffset.Y;

            var temp = XToTemp(adjustedX);
            var speed = YToSpeed(adjustedY);

            // Clamp to valid range
            temp = Math.Max(TempMin, Math.Min(TempMax, temp));
            speed = Math.Max(SpeedMin, Math.Min(SpeedMax, speed));

            // Don't allow moving to a position occupied by another point
            if (Points != null && Points.Any(p => !ReferenceEquals(p, _draggingPoint) && p.TempC == temp))
                return;

            // Update point
            _draggingPoint.TempC = temp;
            _draggingPoint.SpeedPercent = speed;

            // Efficiently update only the dragged point's position
            UpdatePointPosition(_draggingPoint);

            // Update tooltip position and text
            UpdateTooltip(_draggingPoint);

            // Raise event
            PointChanged?.Invoke(this, _draggingPoint);

            e.Handled = true;
        }
    }

    private void OnPointPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingEllipse != null)
        {
            _draggingEllipse.ReleasePointerCapture(e.Pointer);
        }

        _draggingPoint = null;
        _draggingEllipse = null;

        // Hide tooltip when drag ends
        HideTooltip();

        e.Handled = true;
    }

    #endregion

    #region Tooltip

    private void OnPointPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Ellipse ellipse && ellipse.Tag is FanCurvePoint point && _draggingPoint == null)
        {
            _hoveredPoint = point;
            ShowTooltip(point);
        }
    }

    private void OnPointPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingPoint == null)
        {
            _hoveredPoint = null;
            HideTooltip();
        }
    }

    private void OnPointRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is Ellipse ellipse && ellipse.Tag is FanCurvePoint point)
        {
            _selectedPointForDeletion = point;

            var flyout = new Flyout();

            // Create FlyoutPresenterStyle to remove padding and min-width
            var presenterStyle = new Style(typeof(FlyoutPresenter));
            presenterStyle.Setters.Add(new Setter(FlyoutPresenter.MinWidthProperty, 0.0));
            presenterStyle.Setters.Add(new Setter(FlyoutPresenter.MinHeightProperty, 0.0));
            presenterStyle.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(0)));
            flyout.FlyoutPresenterStyle = presenterStyle;

            var deleteButton = new Button
            {
                Content = new FontIcon
                {
                    Glyph = "\uE74D",
                    Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                },
                Padding = new Thickness(8, 4, 8, 4),
                BorderThickness = new Thickness(0),
            };
            deleteButton.Click += OnDeleteButtonClicked;

            flyout.Content = deleteButton;
            flyout.ShowAt(ellipse);
            
            e.Handled = true;
        }
    }

    private void OnDeleteButtonClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedPointForDeletion != null && Points != null && Points.Count > MinPoints)
        {
            Points.Remove(_selectedPointForDeletion);
            _selectedPointForDeletion = null;
        }
    }

    /// <summary>
    /// Validates a click position and returns a new point if it's a valid location to add.
    /// Returns null if the position is outside the graph area, matches an existing point,
    /// or is not between two existing points.
    /// </summary>
    private FanCurvePoint? TryGetValidAddPoint(double x, double y)
    {
        if (Points == null || Points.Count >= MaxPoints) return null;

        // Check if within graph area (not in padding)
        var graphWidth = this.Width - PaddingLeft - PaddingRight;
        var graphHeight = this.Height - PaddingTop - PaddingBottom;

        if (x < PaddingLeft || x > PaddingLeft + graphWidth ||
            y < PaddingTop || y > PaddingTop + graphHeight)
            return null;

        // Snap to grid
        var temp = XToTemp(x);
        var speed = YToSpeed(y);

        // Clamp to valid range
        temp = Math.Max(TempMin, Math.Min(TempMax, temp));
        speed = Math.Max(SpeedMin, Math.Min(SpeedMax, speed));

        // Must be between two existing points on the x-axis
        var sortedPoints = Points.OrderBy(p => p.TempC).ToList();

        // Don't add if a point already exists at this x value
        if (sortedPoints.Any(p => p.TempC == temp))
            return null;

        // Must have a neighbor on both sides (point must be between two existing points)
        var hasLeftNeighbor = sortedPoints.Any(p => p.TempC < temp);
        var hasRightNeighbor = sortedPoints.Any(p => p.TempC > temp);

        if (!hasLeftNeighbor || !hasRightNeighbor)
            return null;

        return new FanCurvePoint { TempC = temp, SpeedPercent = speed };
    }

    private void OnGraphRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var position = e.GetPosition(RootGrid);
        var newPoint = TryGetValidAddPoint(position.X, position.Y);
        if (newPoint == null) return;

        // Store pending point and show add button
        _pendingAddPoint = newPoint;

        var flyout = new Flyout();

        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(FlyoutPresenter.MinWidthProperty, 0.0));
        presenterStyle.Setters.Add(new Setter(FlyoutPresenter.MinHeightProperty, 0.0));
        presenterStyle.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(0)));
        flyout.FlyoutPresenterStyle = presenterStyle;

        var addButton = new Button
        {
            Content = new FontIcon
            {
                Glyph = "\uE710",
                Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"],
            },
            Padding = new Thickness(8, 4, 8, 4),
            BorderThickness = new Thickness(0),
        };
        addButton.Click += OnAddButtonClicked;

        flyout.Content = addButton;
        _addFlyout = flyout;

        // Position flyout at click using a temporary anchor
        var anchor = new Border { Width = 1, Height = 1 };
        Canvas.SetLeft(anchor, position.X);
        Canvas.SetTop(anchor, position.Y);
        TooltipCanvas.Children.Add(anchor);
        flyout.Closed += (_, _) => TooltipCanvas.Children.Remove(anchor);
        flyout.ShowAt(anchor);

        e.Handled = true;
    }

    private void OnAddButtonClicked(object sender, RoutedEventArgs e)
    {
        if (_pendingAddPoint != null && Points != null && Points.Count < MaxPoints)
        {
            Points.Add(_pendingAddPoint);
            _pendingAddPoint = null;
        }

        _addFlyout?.Hide();
        _addFlyout = null;
    }

    private void OnGraphDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var position = e.GetPosition(RootGrid);
        var newPoint = TryGetValidAddPoint(position.X, position.Y);
        if (newPoint == null) return;

        Points?.Add(newPoint);
        e.Handled = true;
    }

    private void ShowTooltip(FanCurvePoint point)
    {
        UpdateTooltipText(point);
        TooltipBorder.Visibility = Visibility.Visible;
        // Force layout update so we can measure the tooltip size
        TooltipBorder.UpdateLayout();
        UpdateTooltipPosition(point);
    }

    private void UpdateTooltip(FanCurvePoint point)
    {
        UpdateTooltipText(point);
        UpdateTooltipPosition(point);
    }

    private void HideTooltip()
    {
        TooltipBorder.Visibility = Visibility.Collapsed;
    }

    private void UpdateTooltipText(FanCurvePoint point)
    {
        TooltipText.Text = Loc.F("format.fan_curve_point", point.TempC, point.SpeedPercent);
    }

    private void UpdateTooltipPosition(FanCurvePoint point)
    {
        // Calculate position directly from point data (don't rely on ellipse cache)
        var x = TempToX(point.TempC);
        var y = SpeedToY(point.SpeedPercent);

        // Position tooltip to the top-left of the point
        TooltipBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var tooltipWidth = TooltipBorder.DesiredSize.Width;
        var tooltipHeight = TooltipBorder.DesiredSize.Height;

        var tooltipX = x - tooltipWidth - 10;  // 10px to the left
        var tooltipY = y - tooltipHeight - 10; // 10px above the point

        // Clamp to control bounds
        tooltipX = Math.Max(0, Math.Min(this.Width - tooltipWidth, tooltipX));
        tooltipY = Math.Max(0, tooltipY);

        Canvas.SetLeft(TooltipBorder, tooltipX);
        Canvas.SetTop(TooltipBorder, tooltipY);
    }

    #endregion
}
