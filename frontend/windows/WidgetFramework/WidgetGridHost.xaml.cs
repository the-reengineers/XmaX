using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

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

    private const double CloseButtonSize = 28.0;
    private const double CloseButtonMargin = 6.0;
    private const double ResizeButtonSize = 28.0;
    private const double ResizeButtonMargin = 6.0;

    // Font size as a ratio of button size (scales with button size and system DPI)
    private const double IconFontSizeRatio = 0.55;

    private readonly List<GridWidget> _widgets = new();
    private readonly Dictionary<string, Border> _containers = new();
    private readonly Dictionary<string, Button> _closeButtons = new();
    private readonly Dictionary<string, Button> _resizeButtons = new();
    private List<WidgetPosition> _currentPositions = new();
    private DragReflowController? _dragController;
    private bool _isSnapping;
    private double _scrollStartOffset = -1;

    public int Columns { get; set; } = 3;

    /// <summary>Fired when the user clicks the close button on a widget (edit mode only).</summary>
    public event Action<string>? WidgetCloseClicked;

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
    /// Remove a widget from the grid (both data and visual container).
    /// </summary>
    public void RemoveWidget(string widgetId)
    {
        // Remove from internal list
        var widget = _widgets.FirstOrDefault(w => w.Id == widgetId);
        if (widget != null)
        {
            _widgets.Remove(widget);
        }

        // Remove visual container from canvas
        if (_containers.TryGetValue(widgetId, out var container))
        {
            HostCanvas.Children.Remove(container);
            _containers.Remove(widgetId);
        }
    }

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
    /// Check if a pointer position is over the resize button for a widget.
    /// Returns true if the pointer is within the resize button area.
    /// </summary>
    public bool IsPointerOverResizeButton(string widgetId, Point pointerPos)
    {
        if (!_containers.TryGetValue(widgetId, out var container)) return false;
        if (!_resizeButtons.TryGetValue(widgetId, out var resizeBtn)) return false;
        if (resizeBtn.Visibility != Visibility.Visible) return false;

        // Get widget position and size
        var x = container.RenderTransform is CompositeTransform t ? t.TranslateX : 0;
        var y = container.RenderTransform is CompositeTransform t2 ? t2.TranslateY : 0;
        var w = container.Width;
        var h = container.Height;

        // Resize button is in bottom-right corner
        var btnX = x + w - ResizeButtonMargin - ResizeButtonSize;
        var btnY = y + h - ResizeButtonMargin - ResizeButtonSize;

        // Check if pointer is within button bounds
        return pointerPos.X >= btnX && pointerPos.X <= btnX + ResizeButtonSize
            && pointerPos.Y >= btnY && pointerPos.Y <= btnY + ResizeButtonSize;
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

    /// <summary>
    /// Toggle edit mode: show/hide close buttons on all widgets, and resize buttons on resizable widgets.
    /// </summary>
    public void SetEditMode(bool isEditMode)
    {
        var visibility = isEditMode ? Visibility.Visible : Visibility.Collapsed;
        foreach (var btn in _closeButtons.Values)
        {
            btn.Visibility = visibility;
        }

        // Show resize buttons only for resizable widgets
        foreach (var widget in _widgets)
        {
            if (_resizeButtons.TryGetValue(widget.Id, out var resizeBtn))
            {
                resizeBtn.Visibility = isEditMode && widget.IsResizable ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    /// <summary>
    /// Scroll to the top of the widget grid.
    /// </summary>
    public void ScrollToTop()
    {
        HostScrollViewer.ChangeView(0, 0, null, true);
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
        // Use ViewportWidth instead of HostCanvas.ActualWidth because ActualWidth
        // is stale during SizeChanged handlers — it only updates after layout completes.
        // ViewportWidth reflects the current scroll viewer size immediately on resize.
        var availableWidth = HostScrollViewer.ViewportWidth - (2 * GridPadding);
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

        // Close button (hidden by default, shown in edit mode)
        // Uses accent color background with white foreground for contrast
        var accentBrush = Application.Current.Resources["SystemControlHighlightAccentBrush"] as Microsoft.UI.Xaml.Media.Brush;
        var closeButton = new Button
        {
            Width = CloseButtonSize,
            Height = CloseButtonSize,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(CloseButtonSize / 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, CloseButtonMargin, CloseButtonMargin, 0),
            Visibility = Visibility.Collapsed,
            Background = accentBrush,
            Content = new FontIcon
            {
                Glyph = "",
                FontSize = CloseButtonSize * IconFontSizeRatio,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
            },
        };
        closeButton.Click += (_, _) => WidgetCloseClicked?.Invoke(widget.Id);

        // Resize button (hidden by default, shown in edit mode for resizable widgets)
        // Uses accent color background with white foreground for contrast
        var resizeButton = new Button
        {
            Width = ResizeButtonSize,
            Height = ResizeButtonSize,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, ResizeButtonMargin, ResizeButtonMargin),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Background = accentBrush,
            Content = new FontIcon
            {
                Glyph = "",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("/Assets/tabler-icons-300.ttf#tabler-icons"),
                FontSize = ResizeButtonSize * IconFontSizeRatio,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
            },
        };

        // Wrap content + buttons in a Grid
        var containerGrid = new Grid
        {
            Children = { content!, closeButton, resizeButton },
        };

        // For AlwaysFillRow widgets, add top and bottom borders using system spacer brush
        UIElement containerChild = containerGrid;
        if (widget.AlwaysFillRow)
        {
            var spacerBrush = Application.Current.Resources["DividerStrokeColorDefaultBrush"] as Microsoft.UI.Xaml.Media.Brush;
            var borderGrid = new Grid();
            borderGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            borderGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            borderGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            borderGrid.Children.Add(containerGrid);

            var topBorder = new Border
            {
                Height = 1,
                Background = spacerBrush,
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetRow(topBorder, 0);
            borderGrid.Children.Add(topBorder);

            Grid.SetRow(containerGrid, 1);

            var bottomBorder = new Border
            {
                Height = 1,
                Background = spacerBrush,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            Grid.SetRow(bottomBorder, 2);
            borderGrid.Children.Add(bottomBorder);

            containerChild = borderGrid;
        }

        var border = new Border
        {
            CornerRadius = widget.AlwaysFillRow ? new CornerRadius(0) : new CornerRadius(WidgetCornerRadius),
            Child = containerChild,
            RenderTransform = new CompositeTransform(),
        };

        _closeButtons[widget.Id] = closeButton;
        _resizeButtons[widget.Id] = resizeButton;
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

    // ===== Scroll snapping to row multiples =====
    // Canvas-based layout means XAML VerticalSnapPointsType only snaps to the
    // canvas bounds, not individual rows. We handle snapping manually via
    // ViewChanged + ChangeView for smooth animation to row-aligned offsets.

    /// <summary>
    /// <summary>
    /// Scroll snap to row multiples. Snaps after scroll settles in the direction of scroll.
    /// Works for both touch and mousewheel input.
    /// </summary>
    private void OnScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_isSnapping)
        {
            if (!e.IsIntermediate) _isSnapping = false;
            return;
        }

        var rowUnit = CellHeight + Spacing;
        if (rowUnit <= 0) return;

        if (e.IsIntermediate)
        {
            // Track scroll start position during scrolling
            var currentOffset = HostScrollViewer.VerticalOffset;
            if (_scrollStartOffset < 0)
                _scrollStartOffset = currentOffset;
            return;
        }

        // Final event (scroll settled) — snap to nearest row in scroll direction
        var finalOffset = HostScrollViewer.VerticalOffset;
        var startOffset = _scrollStartOffset >= 0 ? _scrollStartOffset : finalOffset;
        _scrollStartOffset = -1;

        // Only snap if there's been meaningful scroll movement
        var scrollDistance = Math.Abs(finalOffset - startOffset);
        if (scrollDistance < 1.0)
            return;

        // Calculate direction and snap target
        var direction = finalOffset - startOffset;
        var snap = ComputeSnapTarget(finalOffset, direction, rowUnit);

        var maxOff = Math.Max(0, HostScrollViewer.ExtentHeight - HostScrollViewer.ViewportHeight);
        snap = Math.Clamp(snap, 0, maxOff);

        // Snap to target with smooth animation
        if (Math.Abs(finalOffset - snap) > 1.0)
        {
            _isSnapping = true;
            HostScrollViewer.ChangeView(null, snap, null, disableAnimation: false);
        }
    }

    /// <summary>
    /// Compute the snap target for a given offset and scroll direction.
    /// Steps in the scroll direction before rounding to ensure momentum-based snapping.
    /// </summary>
    private static double ComputeSnapTarget(double currentOffset, double direction, double rowUnit)
    {
        var norm = currentOffset / rowUnit;
        var rounded = Math.Round(norm);

        // If on an exact boundary (within 2% tolerance), step one row in the
        // scroll direction first — then ceiling/floor lands on the next boundary.
        if (Math.Abs(norm - rounded) < 0.02)
        {
            if (direction < -0.5)
                return (rounded - 1) * rowUnit;
            if (direction > 0.5)
                return (rounded + 1) * rowUnit;
            return rounded * rowUnit; // No meaningful direction — stay put
        }

        // Not on boundary — use ceiling/floor based on scroll direction
        // This ensures we snap in the direction of momentum
        if (direction > 0)
            return Math.Ceiling(norm) * rowUnit;
        if (direction < 0)
            return Math.Floor(norm) * rowUnit;

        // No direction — snap to nearest
        return rounded * rowUnit;
    }

    /// <summary>
    /// Override default inertia to snap immediately in the direction of user's scroll.
    /// This makes the snap part of the deceleration, not a separate movement.
    /// </summary>
    private void OnScrollViewerManipulationInertiaStarting(
        object? sender,
        Microsoft.UI.Xaml.Input.ManipulationInertiaStartingRoutedEventArgs e)
    {
        if (_isSnapping) return;

        var currentOffset = HostScrollViewer.VerticalOffset;
        var rowUnit = CellHeight + Spacing;
        if (rowUnit <= 0) return;

        // Get direction from velocity
        var velocity = e.Velocities.Linear.Y;
        if (Math.Abs(velocity) < 0.01)
        {
            // No velocity, use tracked direction
            var startOffset = _scrollStartOffset >= 0 ? _scrollStartOffset : currentOffset;
            velocity = currentOffset - startOffset;
        }

        // Compute snap target in the direction of velocity
        var snapOffset = ComputeSnapTarget(currentOffset, velocity, rowUnit);

        var maxOffset = Math.Max(0, HostScrollViewer.ExtentHeight - HostScrollViewer.ViewportHeight);
        snapOffset = Math.Clamp(snapOffset, 0, maxOffset);

        // Snap immediately if we're not already at the target
        if (Math.Abs(currentOffset - snapOffset) > 1.0)
        {
            _isSnapping = true;
            _scrollStartOffset = -1;

            // Cancel default inertia and start our own animated scroll
            e.Handled = true;
            HostScrollViewer.ChangeView(null, snapOffset, null, disableAnimation: false);
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
