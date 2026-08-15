using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace XmaX.WidgetFramework;

/// <summary>
/// Handles pointer-based drag with live reflow for WidgetGridHost.
/// The dragged widget follows the cursor; other widgets reflow to make space
/// when the dragged widget's center crosses their boundaries.
/// </summary>
public class DragReflowController
{
    private const double ShadowDepth = 16.0;
    private const double DragScale = 1.05;
    private const double DragThreshold = 5.0; // pixels before a press becomes a drag

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

    public DragReflowController(WidgetGridHost host)
    {
        _host = host;
    }

    /// <summary>Whether a drag operation is in progress.</summary>
    public bool IsDragging => _isDragging;

    /// <summary>
    /// Called when a pointer is pressed on the host canvas.
    /// Initiates a drag if the press is on a widget.
    /// </summary>
    public void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging || _isPendingDrag) return;

        var canvas = sender as UIElement;
        if (canvas == null) return;

        var position = e.GetCurrentPoint(canvas).Position;

        // Hit-test: find which widget container the pointer is over
        var widgetId = HitTest(position);
        if (widgetId == null) return;

        var widget = _host.Widgets.Find(w => w.Id == widgetId);
        var container = _host.GetContainer(widgetId);
        if (widget == null || container == null) return;

        _draggedWidget = widget;
        _draggedContainer = container;
        _originalIndex = _host.Widgets.IndexOf(widget);
        _pressPosition = position;
        _isPendingDrag = true;

        // Store the widget's current packed position (before drag) for boundary checking
        var positions = GridLayoutEngine.Pack(_host.Widgets, _host.Columns);
        _originalPosition = positions.Find(p => p.Id == widgetId);

        // Calculate offset from widget top-left to pointer position
        var widgetX = GetTranslateX(container);
        var widgetY = GetTranslateY(container);
        _dragOffset = new Point(position.X - widgetX, position.Y - widgetY);

        // Capture pointer so we receive events even outside the canvas
        canvas.CapturePointer(e.Pointer);
    }

    /// <summary>
    /// Called when the pointer moves on the host canvas.
    /// Moves the dragged widget and triggers reflow if the insertion index changes.
    /// </summary>
    public void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_draggedContainer == null || _draggedWidget == null) return;

        var canvas = sender as UIElement;
        if (canvas == null) return;

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

        // Move the dragged widget to follow the cursor
        var newX = position.X - _dragOffset.X;
        var newY = position.Y - _dragOffset.Y;
        SetTranslate(_draggedContainer, newX, newY);

        // Compute the dragged widget's center in grid coordinates
        var centerX = newX + _draggedContainer.Width / 2.0;
        var centerY = newY + _draggedContainer.Height / 2.0;
        var (gridCol, gridRow) = _host.PixelToGrid(centerX, centerY);

        // Determine insertion index based on center position
        var remaining = _host.Widgets.Where(w => w.Id != _draggedWidget.Id).ToList();
        var newIndex = GridLayoutEngine.ComputeInsertionIndex(
            remaining, _draggedWidget, gridRow, gridCol, _host.Columns,
            _originalPosition, _originalIndex);

        if (newIndex != _currentInsertionIndex)
        {
            _currentInsertionIndex = newIndex;

            // Compute new positions for remaining widgets (excluding dragged)
            var newPositions = GridLayoutEngine.PackWithInserted(
                remaining, _draggedWidget, newIndex, _host.Columns);

            // Animate non-dragged widgets to new positions
            _host.ApplyPositions(newPositions, animate: true);
        }
    }

    /// <summary>
    /// Called when the pointer is released on the host canvas.
    /// Finalizes the widget order and snaps the dragged widget into its gap.
    /// </summary>
    public void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging && !_isPendingDrag) return;

        var canvas = sender as UIElement;

        if (_isDragging)
        {
            // Real drag — finalize the widget order
            _host.Widgets.Remove(_draggedWidget!);
            var clampedIndex = Math.Clamp(_currentInsertionIndex, 0, _host.Widgets.Count);
            _host.Widgets.Insert(clampedIndex, _draggedWidget!);

            ResetContainer(_draggedContainer!);
            _host.LayoutWidgets(animate: false);
        }
        // If pending (click without threshold), do nothing — widget stays in place

        _isDragging = false;
        _isPendingDrag = false;

        // Release pointer capture
        if (canvas is UIElement element)
        {
            element.ReleasePointerCaptures();
        }

        _draggedWidget = null;
        _draggedContainer = null;
    }

    // ===== Private helpers =====

    /// <summary>
    /// Hit-test: find which widget the pointer is over by checking bounding boxes.
    /// </summary>
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
