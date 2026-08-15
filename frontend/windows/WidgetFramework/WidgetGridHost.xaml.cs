using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace XmaX.WidgetFramework;

/// <summary>
/// Canvas-based widget host. Renders widgets at computed positions and supports
/// animated transitions when positions change (for drag-reflow).
///
/// Each widget is wrapped in a Border with a CompositeTransform for positioning.
/// Canvas.Left/Top are kept at 0; actual position is set via TranslateX/TranslateY
/// so Storyboard animations work reliably.
/// </summary>
public sealed partial class WidgetGridHost : UserControl
{
    internal const double Spacing = 8.0;
    private const double GridPadding = 12.0;
    private const double WidgetCornerRadius = 8.0;
    private const int AnimationDurationMs = 200;

    private readonly List<GridWidget> _widgets = new();
    private readonly Dictionary<string, Border> _containers = new();
    private List<WidgetPosition> _currentPositions = new();
    private DragReflowController? _dragController;

    public int Columns { get; set; } = 3;

    /// <summary>Computed cell width based on canvas width and column count.</summary>
    public double CellWidth => ComputeCellWidth();

    /// <summary>Cell height equals cell width (square cells).</summary>
    public double CellHeight => CellWidth;

    public WidgetGridHost()
    {
        this.InitializeComponent();
        this.SizeChanged += OnSizeChanged;
    }

    /// <summary>
    /// Set the ordered list of widgets. Creates containers and performs initial layout.
    /// </summary>
    public void SetWidgets(IEnumerable<GridWidget> widgets)
    {
        _widgets.Clear();
        _widgets.AddRange(widgets);

        // Create containers for all widgets
        HostCanvas.Children.Clear();
        _containers.Clear();

        foreach (var widget in _widgets)
        {
            var container = CreateContainer(widget);
            _containers[widget.Id] = container;
            HostCanvas.Children.Add(container);
        }

        // Defer layout if canvas has no size yet (ScrollViewer SizeChanged will trigger it)
        if (HostCanvas.ActualWidth > 0)
        {
            LayoutWidgets(animate: false);
        }
    }

    /// <summary>
    /// Get the ordered widget list (mutable — drag controller can reorder).
    /// </summary>
    public List<GridWidget> Widgets => _widgets;

    /// <summary>
    /// Get the container Border for a widget (for drag controller to manipulate).
    /// </summary>
    public Border? GetContainer(string widgetId)
    {
        _containers.TryGetValue(widgetId, out var container);
        return container;
    }

    /// <summary>
    /// Get the widget ID for a container (hit-testing during drag).
    /// </summary>
    public string? GetWidgetIdForContainer(Border container)
    {
        foreach (var kvp in _containers)
        {
            if (kvp.Value == container) return kvp.Key;
        }
        return null;
    }

    /// <summary>
    /// Pack and arrange all widgets. Call after widget order changes.
    /// </summary>
    public void LayoutWidgets(bool animate = true)
    {
        var positions = GridLayoutEngine.Pack(_widgets, Columns);
        ApplyPositions(positions, animate);
    }

    /// <summary>
    /// Apply a specific set of positions (used by drag controller during reflow).
    /// </summary>
    public void ApplyPositions(List<WidgetPosition> positions, bool animate = true)
    {
        _currentPositions = positions;

        // Calculate required canvas height
        int maxRow = 0;
        int maxRowSpan = 1;
        foreach (var pos in positions)
        {
            if (pos.Row > maxRow) maxRow = pos.Row;
            if (pos.Row == maxRow && pos.RowSpan > maxRowSpan) maxRowSpan = pos.RowSpan;
        }

        var totalRows = maxRow + maxRowSpan;
        var canvasHeight = GridPadding * 2 + (totalRows * CellHeight) + ((totalRows - 1) * Spacing);
        HostCanvas.Height = Math.Max(canvasHeight, this.ActualHeight);

        if (animate)
        {
            AnimateToPositions(positions);
        }
        else
        {
            SnapToPositions(positions);
        }
    }

    /// <summary>
    /// Convert pixel coordinates to grid coordinates (for drag controller).
    /// </summary>
    public (double gridCol, double gridRow) PixelToGrid(double pixelX, double pixelY)
    {
        var cellW = CellWidth;
        var cellH = CellHeight;
        if (cellW <= 0 || cellH <= 0) return (0, 0);

        var gridCol = (pixelX - GridPadding) / cellW;
        var gridRow = (pixelY - GridPadding) / cellH;
        return (gridCol, gridRow);
    }

    /// <summary>
    /// Get the pixel position for a grid coordinate.
    /// </summary>
    public (double pixelX, double pixelY) GridToPixel(double gridCol, double gridRow)
    {
        var cellW = CellWidth;
        var cellH = CellHeight;
        var pixelX = GridPadding + gridCol * (cellW + Spacing);
        var pixelY = GridPadding + gridRow * (cellH + Spacing);
        return (pixelX, pixelY);
    }

    /// <summary>
    /// Attach a drag controller to handle pointer-based drag reflow.
    /// </summary>
    public void SetDragController(DragReflowController controller)
    {
        _dragController = controller;
    }

    // ===== Pointer event handlers (delegated to DragReflowController) =====

    private void OnCanvasPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _dragController?.OnPointerPressed(sender, e);
    }

    private void OnCanvasPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _dragController?.OnPointerMoved(sender, e);
    }

    private void OnCanvasPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _dragController?.OnPointerReleased(sender, e);
    }

    // ===== Private methods =====

    private double ComputeCellWidth()
    {
        var availableWidth = HostCanvas.ActualWidth - (2 * GridPadding);
        if (availableWidth <= 0 || Columns <= 0) return 100; // Fallback
        return (availableWidth - ((Columns - 1) * Spacing)) / Columns;
    }

    private double ComputePixelX(int column)
    {
        return GridPadding + column * (CellWidth + Spacing);
    }

    private double ComputePixelY(int row)
    {
        return GridPadding + row * (CellHeight + Spacing);
    }

    private double ComputePixelWidth(int columnSpan)
    {
        return columnSpan * CellWidth + (columnSpan - 1) * Spacing;
    }

    private double ComputePixelHeight(int rowSpan)
    {
        return rowSpan * CellHeight + (rowSpan - 1) * Spacing;
    }

    private Border CreateContainer(GridWidget widget)
    {
        var content = widget.Content as FrameworkElement;

        var border = new Border
        {
            CornerRadius = new CornerRadius(WidgetCornerRadius),
            Child = content,
            RenderTransform = new CompositeTransform(),
        };

        return border;
    }

    private void SnapToPositions(List<WidgetPosition> positions)
    {
        foreach (var pos in positions)
        {
            if (!_containers.TryGetValue(pos.Id, out var container)) continue;

            var x = ComputePixelX(pos.Column);
            var y = ComputePixelY(pos.Row);
            var w = ComputePixelWidth(pos.ColumnSpan);
            var h = ComputePixelHeight(pos.RowSpan);

            container.Width = w;
            container.Height = h;

            if (container.RenderTransform is CompositeTransform transform)
            {
                transform.TranslateX = x;
                transform.TranslateY = y;
            }
        }
    }

    private void AnimateToPositions(List<WidgetPosition> positions)
    {
        var storyboard = new Storyboard();

        foreach (var pos in positions)
        {
            if (!_containers.TryGetValue(pos.Id, out var container)) continue;

            var w = ComputePixelWidth(pos.ColumnSpan);
            var h = ComputePixelHeight(pos.RowSpan);
            container.Width = w;
            container.Height = h;

            if (container.RenderTransform is not CompositeTransform transform) continue;

            var targetX = ComputePixelX(pos.Column);
            var targetY = ComputePixelY(pos.Row);

            var animX = new DoubleAnimation
            {
                To = targetX,
                Duration = new Duration(TimeSpan.FromMilliseconds(AnimationDurationMs)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animX, transform);
            Storyboard.SetTargetProperty(animX, "TranslateX");
            storyboard.Children.Add(animX);

            var animY = new DoubleAnimation
            {
                To = targetY,
                Duration = new Duration(TimeSpan.FromMilliseconds(AnimationDurationMs)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animY, transform);
            Storyboard.SetTargetProperty(animY, "TranslateY");
            storyboard.Children.Add(animY);
        }

        storyboard.Begin();
    }

    private void OnScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Canvas inside ScrollViewer doesn't stretch — manually set its width
        HostCanvas.Width = HostScrollViewer.ViewportWidth;

        // Re-layout when size changes (recalculates cell dimensions)
        if (_widgets.Count > 0)
        {
            LayoutWidgets(animate: false);
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Re-layout when UserControl size changes
        if (_widgets.Count > 0)
        {
            LayoutWidgets(animate: false);
        }
    }
}
