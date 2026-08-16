using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace XmaX.WidgetFramework;

/// <summary>
/// Which edge(s) of a widget the pointer is near, for resize hit-testing.
/// </summary>
[Flags]
public enum ResizeEdge
{
    None = 0,
    Left = 1,
    Right = 2,
    Top = 4,
    Bottom = 8,
}

/// <summary>
/// Handles pointer-based drag and resize with live reflow for WidgetGridHost.
/// - Drag: widget follows cursor; other widgets reflow when center crosses boundaries.
/// - Resize: dragging from an edge (within EdgeThreshold px) grows/shrinks the widget
///   by whole cells; other widgets reflow around the size change.
/// </summary>
public class DragReflowController
{
    private const double ShadowDepth = 16.0;
    private const double DragScale = 1.05;
    private const double DragThreshold = 5.0;   // pixels before a press becomes a drag
    private const double EdgeThreshold = 16.0;    // pixels from edge to trigger resize

    private readonly WidgetGridHost _host;
    private Border? _draggedContainer;
    private GridWidget? _draggedWidget;
    private int _originalIndex;
    private Point _dragOffset;
    private Point _pressPosition;
    private int _currentInsertionIndex;
    private WidgetPosition? _originalPosition;
    private bool _isDragging;
    private bool _isPendingDrag;

    // Resize state
    private bool _isResizing;
    private ResizeEdge _resizeEdge;
    private int _originalColSpan;
    private int _originalRowSpan;
    private int _originalColumn;
    private int _originalRow;
    private double _resizeStartPixelX;
    private double _resizeStartPixelY;

    public DragReflowController(WidgetGridHost host)
    {
        _host = host;
    }

    /// <summary>Whether drag and resize operations are allowed. Must be true for interactions to work.</summary>
    public bool IsEditMode { get; set; }

    /// <summary>Whether a drag or resize operation is in progress.</summary>
    public bool IsDragging => _isDragging || _isResizing;

    /// <summary>Fired after a drag or resize operation completes (not on plain clicks).</summary>
    public event Action? LayoutChanged;

    // ===== Pointer handlers =====

    public void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsEditMode) return;
        if (_isDragging || _isPendingDrag || _isResizing) return;

        var canvas = sender as UIElement;
        if (canvas == null) return;

        var position = e.GetCurrentPoint(canvas).Position;

        // Hit-test: find which widget container the pointer is over
        var widgetId = HitTest(position);
        if (widgetId == null) return;

        var widget = _host.Widgets.Find(w => w.Id == widgetId);
        var container = _host.GetContainer(widgetId);
        if (widget == null || container == null) return;

        // Check if pointer is near an edge → resize mode
        var edge = DetectResizeEdge(position, container, widget);
        if (edge != ResizeEdge.None)
        {
            StartResize(widget, container, position, edge);
            canvas.CapturePointer(e.Pointer);
            return;
        }

        // Otherwise → pending drag
        _draggedWidget = widget;
        _draggedContainer = container;
        _originalIndex = _host.Widgets.IndexOf(widget);
        _pressPosition = position;
        _isPendingDrag = true;

        // Store the widget's current packed position for boundary checking
        var positions = GridLayoutEngine.Pack(_host.Widgets, _host.Columns);
        _originalPosition = positions.Find(p => p.Id == widgetId);

        // Calculate offset from widget top-left to pointer position
        var widgetX = GetTranslateX(container);
        var widgetY = GetTranslateY(container);
        _dragOffset = new Point(position.X - widgetX, position.Y - widgetY);

        canvas.CapturePointer(e.Pointer);
    }

    public void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var canvas = sender as UIElement;
        if (canvas == null) return;

        if (_isResizing)
        {
            HandleResizeMove(e.GetCurrentPoint(canvas).Position);
            return;
        }

        if (_draggedContainer == null || _draggedWidget == null) return;

        var position = e.GetCurrentPoint(canvas).Position;

        // Check drag threshold before activating drag
        if (_isPendingDrag && !_isDragging)
        {
            var dx = position.X - _pressPosition.X;
            var dy = position.Y - _pressPosition.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < DragThreshold) return;

            // Threshold crossed — activate drag
            _isDragging = true;
            _isPendingDrag = false;
            _currentInsertionIndex = _originalIndex;
            ElevateContainer(_draggedContainer);
        }

        if (!_isDragging) return;

        HandleDragMove(position);
    }

    public void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var canvas = sender as UIElement;
        bool layoutChanged = false;

        if (_isResizing)
        {
            EndResize();
            layoutChanged = true;
        }
        else if (_isDragging)
        {
            // Real drag — finalize the widget order
            _host.Widgets.Remove(_draggedWidget!);
            var clampedIndex = Math.Clamp(_currentInsertionIndex, 0, _host.Widgets.Count);
            _host.Widgets.Insert(clampedIndex, _draggedWidget!);

            ResetContainer(_draggedContainer!);
            _host.LayoutWidgets(animate: false);
            layoutChanged = true;
        }
        // If pending (click without threshold), do nothing

        _isDragging = false;
        _isPendingDrag = false;
        _isResizing = false;
        _resizeEdge = ResizeEdge.None;

        if (canvas is UIElement element)
        {
            element.ReleasePointerCaptures();
        }

        _draggedWidget = null;
        _draggedContainer = null;

        if (layoutChanged)
        {
            LayoutChanged?.Invoke();
        }
    }

    // ===== Drag logic =====

    private void HandleDragMove(Point position)
    {
        // Move the dragged widget to follow the cursor
        var newX = position.X - _dragOffset.X;
        var newY = position.Y - _dragOffset.Y;
        SetTranslate(_draggedContainer!, newX, newY);

        // Compute the dragged widget's center in grid coordinates
        var centerX = newX + _draggedContainer!.Width / 2.0;
        var centerY = newY + _draggedContainer.Height / 2.0;
        var (gridCol, gridRow) = _host.PixelToGrid(centerX, centerY);

        // Determine insertion index based on center position
        var remaining = _host.Widgets.Where(w => w.Id != _draggedWidget!.Id).ToList();
        var newIndex = GridLayoutEngine.ComputeInsertionIndex(
            remaining, _draggedWidget!, gridRow, gridCol, _host.Columns,
            _originalPosition, _originalIndex);

        if (newIndex != _currentInsertionIndex)
        {
            _currentInsertionIndex = newIndex;

            var newPositions = GridLayoutEngine.PackWithInserted(
                remaining, _draggedWidget!, newIndex, _host.Columns);

            _host.ApplyPositions(newPositions, animate: true);
        }
    }

    // ===== Resize logic =====

    private void StartResize(GridWidget widget, Border container, Point position, ResizeEdge edge)
    {
        _draggedWidget = widget;
        _draggedContainer = container;
        _originalIndex = _host.Widgets.IndexOf(widget);
        _isResizing = true;
        _resizeEdge = edge;
        _originalColSpan = widget.ColumnSpan;
        _originalRowSpan = widget.RowSpan;
        _resizeStartPixelX = position.X;
        _resizeStartPixelY = position.Y;

        // Store the widget's current grid position
        var positions = GridLayoutEngine.Pack(_host.Widgets, _host.Columns);
        var pos = positions.Find(p => p.Id == widget.Id);
        _originalColumn = pos?.Column ?? 0;
        _originalRow = pos?.Row ?? 0;

        ElevateContainer(container);
    }

    private void HandleResizeMove(Point position)
    {
        if (_draggedWidget == null || _draggedContainer == null) return;

        var cellW = _host.CellWidth;
        var cellH = _host.CellHeight;
        var spacing = WidgetGridHost.Spacing;
        if (cellW <= 0 || cellH <= 0) return;

        int newColSpan = _originalColSpan;
        int newRowSpan = _originalRowSpan;
        int newColumn = _originalColumn;
        int newRow = _originalRow;

        // Horizontal resize
        if (_resizeEdge.HasFlag(ResizeEdge.Right))
        {
            var deltaCells = (int)Math.Round((position.X - _resizeStartPixelX) / (cellW + spacing));
            newColSpan = Math.Max(1, Math.Min(_host.Columns, _originalColSpan + deltaCells));
        }
        else if (_resizeEdge.HasFlag(ResizeEdge.Left))
        {
            var deltaCells = (int)Math.Round((_resizeStartPixelX - position.X) / (cellW + spacing));
            var maxExpand = Math.Min(_originalColumn, _originalColSpan - 1);
            var expand = Math.Max(0, Math.Min(deltaCells, maxExpand + (_originalColSpan - 1)));
            newColSpan = Math.Max(1, _originalColSpan + expand);
            newColumn = _originalColumn - (newColSpan - _originalColSpan);
            if (newColumn < 0) { newColSpan += newColumn; newColumn = 0; }
        }

        // Vertical resize
        if (_resizeEdge.HasFlag(ResizeEdge.Bottom))
        {
            var deltaCells = (int)Math.Round((position.Y - _resizeStartPixelY) / (cellH + spacing));
            newRowSpan = Math.Max(1, _originalRowSpan + deltaCells);
        }
        else if (_resizeEdge.HasFlag(ResizeEdge.Top))
        {
            var deltaCells = (int)Math.Round((_resizeStartPixelY - position.Y) / (cellH + spacing));
            var maxExpand = _originalRow;
            var expand = Math.Max(0, Math.Min(deltaCells, maxExpand));
            newRowSpan = Math.Max(1, _originalRowSpan + expand);
            newRow = _originalRow - (newRowSpan - _originalRowSpan);
            if (newRow < 0) { newRowSpan += newRow; newRow = 0; }
        }

        // AlwaysFillRow widgets: lock column span to grid width
        if (_draggedWidget.AlwaysFillRow)
        {
            newColSpan = _host.Columns;
            newColumn = 0;
        }

        // Apply size change only if something changed
        if (newColSpan == _draggedWidget.ColumnSpan && newRowSpan == _draggedWidget.RowSpan)
            return;

        _draggedWidget.ColumnSpan = newColSpan;
        _draggedWidget.RowSpan = newRowSpan;

        // Reflow: remove resizing widget, pack remaining, re-insert at same index
        var remaining = _host.Widgets.Where(w => w.Id != _draggedWidget.Id).ToList();
        var newPositions = GridLayoutEngine.PackWithInserted(
            remaining, _draggedWidget, _originalIndex, _host.Columns);

        _host.ApplyPositions(newPositions, animate: true);
    }

    private void EndResize()
    {
        if (_draggedContainer != null)
        {
            ResetContainer(_draggedContainer);
        }
        _host.LayoutWidgets(animate: false);
    }

    // ===== Hit-testing =====

    /// <summary>
    /// Detect which edge(s) of a container the pointer is near.
    /// Returns ResizeEdge.None if the pointer is not within EdgeThreshold of any edge.
    /// </summary>
    private ResizeEdge DetectResizeEdge(Point pointerPos, Border container, GridWidget widget)
    {
        var x = GetTranslateX(container);
        var y = GetTranslateY(container);
        var w = container.Width;
        var h = container.Height;

        // Must be inside the widget bounds
        if (pointerPos.X < x || pointerPos.X > x + w
            || pointerPos.Y < y || pointerPos.Y > y + h)
            return ResizeEdge.None;

        var edge = ResizeEdge.None;

        if (pointerPos.X >= x + w - EdgeThreshold)
            edge |= ResizeEdge.Right;
        else if (pointerPos.X <= x + EdgeThreshold && !widget.AlwaysFillRow)
            edge |= ResizeEdge.Left;

        if (pointerPos.Y >= y + h - EdgeThreshold)
            edge |= ResizeEdge.Bottom;
        else if (pointerPos.Y <= y + EdgeThreshold)
            edge |= ResizeEdge.Top;

        return edge;
    }

    private string? HitTest(Point pointerPos)
    {
        foreach (var widget in _host.Widgets)
        {
            var container = _host.GetContainer(widget.Id);
            if (container == null) continue;

            var x = GetTranslateX(container);
            var y = GetTranslateY(container);
            var w = container.Width;
            var h = container.Height;

            if (pointerPos.X >= x && pointerPos.X <= x + w
                && pointerPos.Y >= y && pointerPos.Y <= y + h)
            {
                return widget.Id;
            }
        }
        return null;
    }

    // ===== Container helpers =====

    private void ElevateContainer(Border container)
    {
        Canvas.SetZIndex(container, 1000);
        container.Shadow = new ThemeShadow { };

        if (container.RenderTransform is CompositeTransform transform)
        {
            transform.ScaleX = DragScale;
            transform.ScaleY = DragScale;
        }
    }

    private void ResetContainer(Border container)
    {
        Canvas.SetZIndex(container, 0);
        container.Shadow = null;

        if (container.RenderTransform is CompositeTransform transform)
        {
            transform.ScaleX = 1.0;
            transform.ScaleY = 1.0;
        }
    }

    private static double GetTranslateX(Border container)
    {
        return container.RenderTransform is CompositeTransform t ? t.TranslateX : 0;
    }

    private static double GetTranslateY(Border container)
    {
        return container.RenderTransform is CompositeTransform t ? t.TranslateY : 0;
    }

    private static void SetTranslate(Border container, double x, double y)
    {
        if (container.RenderTransform is CompositeTransform t)
        {
            t.TranslateX = x;
            t.TranslateY = y;
        }
    }
}
