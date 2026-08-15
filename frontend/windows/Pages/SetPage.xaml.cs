using System.Collections.Specialized;
using System.ComponentModel;
using System.Security.Principal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using XmaX.Services;
using XmaX.Widgets;

namespace XmaX.Pages;

/// <summary>
/// Set page for widget management. Allows users to add/remove/reorder widgets.
/// </summary>
public sealed partial class SetPage : Page
{
    private readonly WidgetService _widgetService;
    private readonly bool _isElevated;
    private readonly string _logPath;

    // Container cache: reuses Border containers to avoid WinUI 3 re-parenting issues
    private readonly System.Collections.Generic.Dictionary<string, Border> _containers = new();

    // Pointer-based drag state (replaces standard DragStarting API for interactive displacement)
    private string? _draggingWidgetId;
    private string? _dragSourcePanel;
    private Grid? _dragSourceGrid;
    private Windows.Foundation.Point _pointerOffset;
    private int _currentTargetRow = -1;
    private int _currentTargetCol = -1;
    private bool _isDragging;

    // Swap-based reorder: tracks the dragged widget's current logical grid position.
    // Updated on each swap so the cursor-relative transform stays consistent.
    private int _dragLogicalRow;
    private int _dragLogicalCol;

    // Position overrides: explicit (row, col) for widgets involved in swaps.
    // The packing algorithm honors these, ensuring swapped widgets land at their
    // expected positions even when sizes differ (e.g. 1x1 <-> 3x2).
    private readonly System.Collections.Generic.Dictionary<string, (int row, int col)> _positionOverrides = new();

    // Prevents re-swapping with the same widget when the cursor stays within its footprint.
    private string? _lastSwapTargetId;

    // Grid layout constants (must match HomePage.xaml)
    private const int GridPadding = 12;
    private const int ColumnSpacing = 8;
    private const int AvailableColumns = 3;  // Left panel always shows 3 columns

    public SetPage()
    {
        this.InitializeComponent();

        _widgetService = App.WidgetService;
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "xmax", "frontend_crash.log");

        // Check if running elevated (drag-and-drop won't work if elevated)
        _isElevated = IsRunningElevated();

        // Calculate left panel width based on column_width
        var columnWidth = _widgetService.ColumnWidth;
        var leftPanelWidth = (AvailableColumns * columnWidth)
                           + ((AvailableColumns - 1) * ColumnSpacing)
                           + (2 * GridPadding);

        LeftPanelColumn.Width = new GridLength(leftPanelWidth);

        // Listen for layout changes
        _widgetService.PropertyChanged += OnWidgetServiceChanged;
        SubscribeToVisibleWidgets();

        // Build panels after page is loaded
        this.Loaded += OnPageLoaded;
    }

    private void Log(string message)
    {
        try
        {
            File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { }
    }

    private bool IsRunningElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            var isElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
            Log($"[SetPage] Running elevated: {isElevated}");
            return isElevated;
        }
        catch (Exception ex)
        {
            Log($"[SetPage] IsRunningElevated error: {ex.Message}");
            return false;
        }
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Log($"[SetPage] OnPageLoaded - elevated: {_isElevated}");
        BuildLeftPanel();
        BuildRightPanel();
    }

    private void SubscribeToVisibleWidgets()
    {
        _widgetService.VisibleWidgets.CollectionChanged -= OnVisibleWidgetsChanged;
        _widgetService.VisibleWidgets.CollectionChanged += OnVisibleWidgetsChanged;
    }

    private void OnWidgetServiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WidgetService.VisibleWidgets))
        {
            SubscribeToVisibleWidgets();
            // Skip rebuild during drag — PointerReleased handles the final rebuild
            if (_isDragging) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                DetachAllWidgets();
                BuildLeftPanel();
                BuildRightPanel();
            });
        }
    }

    private void OnVisibleWidgetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Skip rebuild during drag — PointerReleased handles the final rebuild
        if (_isDragging) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            // Detach all widget controls from both panels before rebuilding
            DetachAllWidgets();
            BuildLeftPanel();
            BuildRightPanel();
        });
    }

    private void DetachAllWidgets()
    {
        // No longer needed — containers are updated in-place.
    }

    /// <summary>
    /// Build left panel with hidden widgets (available to add).
    /// Updates containers in-place to avoid re-parenting issues.
    /// </summary>
    private void BuildLeftPanel()
    {
        try
        {
            // Collect widget IDs that should be in this panel
            var visibleIds = new System.Collections.Generic.HashSet<string>();

            // Get hidden widgets
            var hiddenWidgets = new System.Collections.Generic.List<IHomeWidget>();
            foreach (var widgetId in _widgetService.WidgetOrder)
            {
                if (!_widgetService.IsVisible(widgetId))
                {
                    var widget = _widgetService.GetWidget(widgetId);
                    if (widget != null)
                    {
                        hiddenWidgets.Add(widget);
                        visibleIds.Add(widgetId);
                    }
                }
            }

            Log($"[SetPage] BuildLeftPanel - {hiddenWidgets.Count} hidden widgets to display");

            // Remove containers that don't belong in this panel
            var toRemove = new System.Collections.Generic.List<UIElement>();
            foreach (var child in AvailableWidgetsGrid.Children)
            {
                if (child is Border border && border.Tag is string tag)
                {
                    var id = tag.Split('|')[0];
                    if (!visibleIds.Contains(id))
                    {
                        toRemove.Add(child);
                    }
                }
            }
            foreach (var child in toRemove)
            {
                AvailableWidgetsGrid.Children.Remove(child);
            }

            // Reset layout
            AvailableWidgetsGrid.ColumnDefinitions.Clear();
            AvailableWidgetsGrid.RowDefinitions.Clear();

            // Create 3 columns for left panel
            for (int c = 0; c < AvailableColumns; c++)
            {
                AvailableWidgetsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            if (hiddenWidgets.Count == 0) return;

            // Calculate widget height based on left panel width
            var leftPanelContentWidth = LeftPanelColumn.Width.Value - (2 * GridPadding);
            var widgetHeight = (leftPanelContentWidth - ((AvailableColumns - 1) * ColumnSpacing)) / AvailableColumns;

            // Track occupied cells
            var occupiedCells = new System.Collections.Generic.HashSet<(int row, int col)>();

            foreach (var widget in hiddenWidgets)
            {
                var config = widget.Config;
                var columnSpan = config.AlwaysFillRow ? AvailableColumns : System.Math.Min(config.MaxColumns, AvailableColumns);
                var rowSpan = widget.GetRequiredRows(columnSpan);

                // Find next available position
                int placeRow = 0, placeCol = 0;
                bool found = false;
                for (int r = 0; r < 100 && !found; r++)
                {
                    for (int c = 0; c <= AvailableColumns - columnSpan; c++)
                    {
                        bool fits = true;
                        for (int dr = 0; dr < rowSpan && fits; dr++)
                            for (int dc = 0; dc < columnSpan && fits; dc++)
                                if (occupiedCells.Contains((r + dr, c + dc))) fits = false;
                        if (fits) { placeRow = r; placeCol = c; found = true; break; }
                    }
                }
                if (!found) continue;

                for (int dr = 0; dr < rowSpan; dr++)
                    for (int dc = 0; dc < columnSpan; dc++)
                        occupiedCells.Add((placeRow + dr, placeCol + dc));

                for (int r = 0; r < rowSpan; r++)
                    EnsureRowDefinition(AvailableWidgetsGrid, placeRow + r, widgetHeight);

                var control = widget.Control as FrameworkElement;
                if (control != null)
                {
                    // Check cache first — only detach from parent when creating a new container.
                    // On subsequent builds the control is already inside the cached Border;
                    // calling DetachFromParent would rip it out and leave the Border empty.
                    if (!_containers.ContainsKey(widget.WidgetId))
                    {
                        DetachFromParent(control);
                    }
                    var container = GetOrCreateContainer(control, config, widget.WidgetId, isLeftPanel: true);
                    Grid.SetRow(container, placeRow);
                    Grid.SetColumn(container, placeCol);
                    Grid.SetColumnSpan(container, columnSpan);
                    Grid.SetRowSpan(container, rowSpan);

                    // Add to grid only if not already a child
                    if (!AvailableWidgetsGrid.Children.Contains(container))
                    {
                        AvailableWidgetsGrid.Children.Add(container);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[SetPage] BuildLeftPanel error: {ex.Message}");
        }
    }

    /// <summary>
    /// Build right panel with visible widgets (home preview).
    /// Updates containers in-place to avoid re-parenting issues.
    /// </summary>
    private void BuildRightPanel()
    {
        try
        {
            var columns = _widgetService.Columns;
            var widgets = _widgetService.VisibleWidgets;

            Log($"[SetPage] BuildRightPanel - {widgets.Count} visible widgets, columns: {columns}");

            // Collect widget IDs that should be in this panel
            var visibleIds = new System.Collections.Generic.HashSet<string>();
            foreach (var w in widgets) visibleIds.Add(w.WidgetId);

            // Remove containers that don't belong in this panel
            var toRemove = new System.Collections.Generic.List<UIElement>();
            foreach (var child in HomePreviewGrid.Children)
            {
                if (child is Border border && border.Tag is string tag)
                {
                    var id = tag.Split('|')[0];
                    if (!visibleIds.Contains(id))
                    {
                        toRemove.Add(child);
                    }
                }
            }
            foreach (var child in toRemove)
            {
                HomePreviewGrid.Children.Remove(child);
            }

            // Reset layout
            HomePreviewGrid.ColumnDefinitions.Clear();
            HomePreviewGrid.RowDefinitions.Clear();

            if (widgets.Count == 0) return;

            // Create column definitions
            for (int c = 0; c < columns; c++)
            {
                HomePreviewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            // Calculate widget height
            var rightPanelWidth = HomePreviewGrid.ActualWidth;
            if (rightPanelWidth <= 0) rightPanelWidth = columns * _widgetService.ColumnWidth;
            var widgetHeight = rightPanelWidth / columns;

            // Track occupied cells
            var occupiedCells = new System.Collections.Generic.HashSet<(int row, int col)>();

            foreach (var widget in widgets)
            {
                var config = widget.Config;
                var hasTitle = !string.IsNullOrEmpty(widget.Title);
                var columnSpan = config.AlwaysFillRow ? columns : System.Math.Min(config.MaxColumns, columns);
                var contentRows = widget.GetRequiredRows(columnSpan);
                var totalRowSpan = contentRows + (hasTitle ? 1 : 0);

                // Find next available position
                int placeRow = 0, placeCol = 0;
                bool found = false;
                for (int r = 0; r < 100 && !found; r++)
                {
                    for (int c = 0; c <= columns - columnSpan; c++)
                    {
                        bool fits = true;
                        for (int dr = 0; dr < totalRowSpan && fits; dr++)
                            for (int dc = 0; dc < columnSpan && fits; dc++)
                                if (occupiedCells.Contains((r + dr, c + dc))) fits = false;
                        if (fits) { placeRow = r; placeCol = c; found = true; break; }
                    }
                }
                if (!found) continue;

                for (int dr = 0; dr < totalRowSpan; dr++)
                    for (int dc = 0; dc < columnSpan; dc++)
                        occupiedCells.Add((placeRow + dr, placeCol + dc));

                for (int r = 0; r < totalRowSpan; r++)
                {
                    var height = (r == 0 && hasTitle) ? WidgetConfig.TitleHeight : widgetHeight;
                    EnsureRowDefinition(HomePreviewGrid, placeRow + r, height);
                }

                var control = widget.Control as FrameworkElement;
                if (control != null)
                {
                    control.IsHitTestVisible = false;

                    var isCached = _containers.ContainsKey(widget.WidgetId);
                    Log($"[SetPage] BuildRightPanel - widget {widget.WidgetId}, cached: {isCached}, control parent: {control.Parent?.GetType().Name ?? "null"}");

                    // Check cache first — only detach from parent when creating a new container.
                    // On subsequent builds the control is already inside the cached Border;
                    // calling DetachFromParent would rip it out and leave the Border empty.
                    if (!isCached)
                    {
                        DetachFromParent(control);
                    }
                    var container = GetOrCreateContainer(control, config, widget.WidgetId, isLeftPanel: false);

                    Log($"[SetPage] BuildRightPanel - widget {widget.WidgetId}, container.Child: {container.Child?.GetType().Name ?? "null"}");

                    Grid.SetRow(container, placeRow);
                    Grid.SetColumn(container, placeCol);
                    Grid.SetColumnSpan(container, columnSpan);
                    Grid.SetRowSpan(container, totalRowSpan);

                    // Add to grid only if not already a child
                    if (!HomePreviewGrid.Children.Contains(container))
                    {
                        HomePreviewGrid.Children.Add(container);
                    }
                }
            }

            // Diagnostic: verify all containers still have content after build
            foreach (var widget in widgets)
            {
                if (_containers.TryGetValue(widget.WidgetId, out var container))
                {
                    if (container.Child == null)
                    {
                        Log($"[SetPage] BuildRightPanel POST-CHECK: {widget.WidgetId} container has null Child!");
                    }
                    else if (container.Child is Grid grid && grid.Children.Count == 0)
                    {
                        Log($"[SetPage] BuildRightPanel POST-CHECK: {widget.WidgetId} container has empty Grid!");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[SetPage] BuildRightPanel error: {ex.Message}");
        }
    }

    private void EnsureRowDefinition(Grid grid, int rowIndex, double height)
    {
        if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0)
        {
            Log($"[SetPage] EnsureRowDefinition - invalid height: {height} for rowIndex: {rowIndex}, using fallback 100");
            height = 100;
        }

        // Add rows if needed
        while (grid.RowDefinitions.Count <= rowIndex)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });
        }

        // Update height only if changed (minimizes layout invalidation during drag)
        var existingHeight = grid.RowDefinitions[rowIndex].Height.Value;
        if (System.Math.Abs(existingHeight - height) > 0.1)
        {
            grid.RowDefinitions[rowIndex].Height = new GridLength(height);
        }
    }

    /// <summary>
    /// Remove excess row definitions beyond the specified count.
    /// </summary>
    private static void TrimRowDefinitions(Grid grid, int maxRows)
    {
        while (grid.RowDefinitions.Count > maxRows)
        {
            grid.RowDefinitions.RemoveAt(grid.RowDefinitions.Count - 1);
        }
    }

    /// <summary>
    /// Gets or creates a cached container for a widget.
    /// Containers are reused across panel rebuilds to avoid WinUI 3 re-parenting issues.
    /// </summary>
    private Border GetOrCreateContainer(FrameworkElement content, WidgetConfig config, string widgetId, bool isLeftPanel)
    {
        // Return cached container if it exists (just update the Tag for current panel)
        if (_containers.TryGetValue(widgetId, out var existing))
        {
            existing.Tag = $"{widgetId}|{(isLeftPanel ? "left" : "right")}";

            // Diagnostic: verify the cached container still has its content
            if (existing.Child == null)
            {
                Log($"[SetPage] WARNING: Cached container for {widgetId} has null Child - RESTORING!");

                // Re-attach the content to the Border
                if (_isElevated)
                {
                    var grid = new Grid();
                    grid.Children.Add(content);

                    var button = new Button
                    {
                        Content = isLeftPanel ? "+" : "X",
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Top,
                        Width = 32,
                        Height = 32,
                        Padding = new Thickness(0),
                        Tag = widgetId,
                    };
                    button.Click += OnWidgetButtonClick;
                    Grid.SetRow(button, 0);
                    Grid.SetColumn(button, 0);
                    button.Margin = new Thickness(0, -8, -8, 0);
                    grid.Children.Add(button);
                    existing.Child = grid;
                }
                else
                {
                    existing.Child = content;
                }
            }
            else if (existing.Child is Grid grid && grid.Children.Count == 0)
            {
                Log($"[SetPage] WARNING: Cached container for {widgetId} has empty Grid - RESTORING!");
                grid.Children.Add(content);
            }

            return existing;
        }

        // First time: create new container. The caller has already detached content
        // from its previous parent (e.g., HomePage's grid) before calling this method.
        Log($"[SetPage] GetOrCreateContainer - creating NEW container for {widgetId}");

        var border = new Border
        {
            Background = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Tag = $"{widgetId}|{(isLeftPanel ? "left" : "right")}",
        };

        if (_isElevated)
        {
            var grid = new Grid();
            grid.Children.Add(content);

            var button = new Button
            {
                Content = isLeftPanel ? "+" : "X",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                Tag = widgetId,
            };
            button.Click += OnWidgetButtonClick;
            Grid.SetRow(button, 0);
            Grid.SetColumn(button, 0);
            button.Margin = new Thickness(0, -8, -8, 0);
            grid.Children.Add(button);
            border.Child = grid;
            Log($"[SetPage] GetOrCreateContainer - created elevated container for {widgetId}, grid children: {grid.Children.Count}");
        }
        else
        {
            border.Child = content;
            border.PointerPressed += OnWidgetPointerPressed;
            border.PointerMoved += OnWidgetPointerMoved;
            border.PointerReleased += OnWidgetPointerReleased;
            Log($"[SetPage] GetOrCreateContainer - created non-elevated container for {widgetId}, child: {content.GetType().Name}");
        }

        _containers[widgetId] = border;
        return border;
    }

    /// <summary>
    /// Completely detach a FrameworkElement from its parent hierarchy.
    /// </summary>
    private void DetachFromParent(FrameworkElement element)
    {
        var current = element;
        Log($"[SetPage] DetachFromParent - element: {element.GetType().Name}, parent: {element.Parent?.GetType().Name ?? "null"}");
        while (current != null)
        {
            if (current.Parent is Panel parentPanel)
            {
                Log($"[SetPage] DetachFromParent - removing from Panel ({parentPanel.GetType().Name})");
                parentPanel.Children.Remove(current);
                break;
            }
            else if (current.Parent is Border parentBorder)
            {
                Log($"[SetPage] DetachFromParent - clearing Border.Child");
                parentBorder.Child = null;
                break;
            }
            else if (current.Parent is ContentControl contentControl)
            {
                Log($"[SetPage] DetachFromParent - clearing ContentControl.Content");
                contentControl.Content = null;
                break;
            }
            else if (current.Parent is FrameworkElement parentElement)
            {
                current = parentElement;
            }
            else
            {
                break;
            }
        }
    }

    private async void OnWidgetButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string widgetId)
        {
            try
            {
                // Determine which panel the button is in
                var border = button.Parent as FrameworkElement;
                while (border != null && border.Parent != null)
                {
                    if (border == AvailableWidgetsGrid)
                    {
                        // Button in left panel: add widget to home
                        _widgetService.SetVisible(widgetId, true);
                        break;
                    }
                    else if (border == HomePreviewGrid)
                    {
                        // Button in right panel: remove widget from home
                        _widgetService.SetVisible(widgetId, false);
                        break;
                    }
                    border = border.Parent as FrameworkElement;
                }

                await _widgetService.SaveLayoutAsync();
            }
            catch (Exception ex)
            {
                Log($"[SetPage] OnWidgetButtonClick error: {ex.Message}");
            }
        }
    }

    // ===== Custom Pointer-Based Drag (Interactive Displacement) =====

    private void OnWidgetPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border || border.Tag is not string tag) return;

        var parts = tag.Split('|');
        if (parts.Length != 2) return;

        var widgetId = parts[0];
        var sourcePanel = parts[1];
        var sourceGrid = sourcePanel == "left" ? AvailableWidgetsGrid : HomePreviewGrid;

        // Capture pointer for mouse, touch, and pen
        border.CapturePointer(e.Pointer);

        var rootPoint = e.GetCurrentPoint(this).Position;
        var borderPoint = e.GetCurrentPoint(border).Position;

        _draggingWidgetId = widgetId;
        _dragSourcePanel = sourcePanel;
        _dragSourceGrid = sourceGrid;
        _pointerOffset = new Windows.Foundation.Point(borderPoint.X, borderPoint.Y);
        _isDragging = false; // Will activate on first move

        // Record logical grid position (used for swap detection during drag)
        _dragLogicalRow = Grid.GetRow(border);
        _dragLogicalCol = Grid.GetColumn(border);
        _lastSwapTargetId = null;

        Log($"[SetPage] PointerPressed - widget: {widgetId}, panel: {sourcePanel}, row: {_dragLogicalRow}, col: {_dragLogicalCol}");

        e.Handled = true;
    }

    private void OnWidgetPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingWidgetId == null || sender is not Border border) return;

        var point = e.GetCurrentPoint(this).Position;

        // Activate drag on first move
        if (!_isDragging)
        {
            _isDragging = true;
            Log($"[SetPage] Drag activated for widget: {_draggingWidgetId}");
        }

        // --- Visual: dragged widget follows the cursor via TranslateTransform ---
        var transform = border.RenderTransform as TranslateTransform ?? new TranslateTransform();
        transform.X = point.X - _pointerOffset.X;
        transform.Y = point.Y - _pointerOffset.Y;
        border.RenderTransform = transform;

        // --- Swap detection: when the dragged widget's center enters another widget's cell ---
        var sourceGrid = _dragSourceGrid;
        if (sourceGrid == null) { e.Handled = true; return; }

        // Compute the dragged widget's center in page coordinates
        var borderTransform = border.TransformToVisual(this);
        var borderBounds = borderTransform.TransformBounds(
            new Windows.Foundation.Rect(0, 0, border.ActualWidth, border.ActualHeight));
        var centerPage = new Windows.Foundation.Point(
            borderBounds.X + borderBounds.Width / 2,
            borderBounds.Y + borderBounds.Height / 2);

        // Convert to grid-local coordinates
        var gridToPage = sourceGrid.TransformToVisual(this);
        var pageToGrid = gridToPage.Inverse;
        var centerGrid = pageToGrid.TransformPoint(centerPage);

        var columns = sourceGrid == HomePreviewGrid ? _widgetService.Columns : AvailableColumns;
        var cellWidth = sourceGrid.ActualWidth / columns;
        var cellHeight = cellWidth; // Square cells

        if (cellWidth <= 0 || cellHeight <= 0) { e.Handled = true; return; }

        var targetCol = System.Math.Max(0, System.Math.Min((int)(centerGrid.X / cellWidth), columns - 1));
        var targetRow = System.Math.Max(0, (int)(centerGrid.Y / cellHeight));

        // Check if the center has entered a different widget's cell
        if (targetRow != _dragLogicalRow || targetCol != _dragLogicalCol)
        {
            _currentTargetRow = targetRow;
            _currentTargetCol = targetCol;

            // Find which widget currently occupies the target cell
            var widgets = GetWidgetsForGrid(sourceGrid);
            var targetWidget = FindWidgetAtCell(widgets, _dragLogicalRow, _dragLogicalCol,
                                                 targetRow, targetCol, columns);

            if (targetWidget != null)
            {
                Log($"[SetPage] Swap: {_draggingWidgetId} ({_dragLogicalRow},{_dragLogicalCol}) <-> {targetWidget} ({targetRow},{targetCol})");
                PerformSwap(targetWidget, sourceGrid, point);
            }
            else
            {
                // Target cell is empty — clear the last-swap guard so a future
                // move can swap again if needed.
                _lastSwapTargetId = null;
            }
        }

        e.Handled = true;
    }

    private async void OnWidgetPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border) return;

        border.ReleasePointerCapture(e.Pointer);

        if (!_isDragging || _draggingWidgetId == null)
        {
            // Was a click, not a drag
            border.RenderTransform = null;
            _draggingWidgetId = null;
            _isDragging = false;
            return;
        }

        try
        {
            var widgetId = _draggingWidgetId;
            var sourcePanel = _dragSourcePanel;
            var targetGrid = _currentTargetRow >= 0 ? GetGridAtPoint(e.GetCurrentPoint(this).Position) : null;

            Log($"[SetPage] PointerReleased - widget: {widgetId}, targetGrid: {(targetGrid == HomePreviewGrid ? "right" : targetGrid == AvailableWidgetsGrid ? "left" : "none")}");

            if (targetGrid == HomePreviewGrid && _currentTargetRow >= 0)
            {
                if (sourcePanel == "right")
                {
                    // Swaps during drag already maintain the correct order. Nothing to do.
                }
                else if (sourcePanel == "left")
                {
                    // Cross-panel: make widget visible and insert at the cursor's position
                    // in the right panel (NOT _currentTargetRow/Col, which is relative to
                    // the left panel's grid).
                    _widgetService.SetVisible(widgetId, true);
                    var cursorPoint = e.GetCurrentPoint(this).Position;
                    var gridPoint = e.GetCurrentPoint(targetGrid).Position;
                    var columns = _widgetService.Columns;
                    var cellWidth = targetGrid.ActualWidth / columns;
                    var cellHeight = cellWidth;
                    var dropCol = System.Math.Max(0, System.Math.Min((int)(gridPoint.X / cellWidth), columns - 1));
                    var dropRow = System.Math.Max(0, (int)(gridPoint.Y / cellHeight));

                    var targetOrder = BuildTargetOrder(targetGrid, widgetId, dropRow, dropCol);
                    if (targetOrder != null)
                    {
                        _widgetService.SetOrder(targetOrder);
                    }
                }
            }
            else if (targetGrid == AvailableWidgetsGrid && _currentTargetRow >= 0)
            {
                if (sourcePanel == "right")
                {
                    _widgetService.SetVisible(widgetId, false);
                }
            }

            await _widgetService.SaveLayoutAsync();
        }
        catch (Exception ex)
        {
            Log($"[SetPage] PointerReleased error: {ex.Message}");
        }
        finally
        {
            // Reset visual state
            border.RenderTransform = null;
            _draggingWidgetId = null;
            _dragSourcePanel = null;
            _dragSourceGrid = null;
            _isDragging = false;
            _currentTargetRow = -1;
            _currentTargetCol = -1;
            _lastSwapTargetId = null;
            _positionOverrides.Clear();

            // Rebuild panels to final state (uses standard packing — no overrides)
            BuildLeftPanel();
            BuildRightPanel();
        }

        e.Handled = true;
    }

    /// <summary>
    /// Find which grid (left or right panel) contains the given point.
    /// </summary>
    private Grid? GetGridAtPoint(Windows.Foundation.Point point)
    {
        // Check left panel bounds
        var leftTransform = AvailableWidgetsGrid.TransformToVisual(this);
        var leftBounds = new Windows.Foundation.Rect(0, 0, AvailableWidgetsGrid.ActualWidth, AvailableWidgetsGrid.ActualHeight);
        var leftRect = leftTransform.TransformBounds(leftBounds);
        if (leftRect.Contains(point)) return AvailableWidgetsGrid;

        // Check right panel bounds
        var rightTransform = HomePreviewGrid.TransformToVisual(this);
        var rightBounds = new Windows.Foundation.Rect(0, 0, HomePreviewGrid.ActualWidth, HomePreviewGrid.ActualHeight);
        var rightRect = rightTransform.TransformBounds(rightBounds);
        if (rightRect.Contains(point)) return HomePreviewGrid;

        return null;
    }

    /// <summary>
    /// Find which widget currently occupies the target cell, excluding the dragged widget.
    /// Returns null if the target cell is empty or is the dragged widget's own cell.
    /// </summary>
    private string? FindWidgetAtCell(
        System.Collections.Generic.List<IHomeWidget> widgets,
        int oldRow, int oldCol, int targetRow, int targetCol, int columns)
    {
        // Skip if target equals the dragged widget's logical position
        if (targetRow == oldRow && targetCol == oldCol) return null;

        var occupied = new System.Collections.Generic.HashSet<(int, int)>();

        // Pass 1: check widgets with position overrides first (they have explicit positions)
        foreach (var widget in widgets)
        {
            if (widget.WidgetId == _draggingWidgetId) continue;
            if (widget.WidgetId == _lastSwapTargetId) continue;  // prevent re-swap
            if (!_positionOverrides.TryGetValue(widget.WidgetId, out var pos)) continue;

            var colSpan = widget.Config.AlwaysFillRow ? columns
                            : System.Math.Min(widget.Config.MaxColumns, columns);
            var rowSpan = widget.GetRequiredRows(colSpan);

            for (int dr = 0; dr < rowSpan; dr++)
                for (int dc = 0; dc < colSpan; dc++)
                    occupied.Add((pos.row + dr, pos.col + dc));

            // Check if the target cell falls within this widget's footprint
            for (int dr = 0; dr < rowSpan; dr++)
                for (int dc = 0; dc < colSpan; dc++)
                    if (pos.row + dr == targetRow && pos.col + dc == targetCol)
                        return widget.WidgetId;
        }

        // Pass 2: pack remaining widgets (no override) using first-fit
        foreach (var widget in widgets)
        {
            if (widget.WidgetId == _draggingWidgetId) continue;
            if (widget.WidgetId == _lastSwapTargetId) continue;
            if (_positionOverrides.ContainsKey(widget.WidgetId)) continue;  // handled above

            var colSpan = widget.Config.AlwaysFillRow ? columns
                            : System.Math.Min(widget.Config.MaxColumns, columns);
            var rowSpan = widget.GetRequiredRows(colSpan);

            int placeRow = 0, placeCol = 0;
            bool found = false;
            for (int r = 0; r < 100 && !found; r++)
            {
                for (int c = 0; c <= columns - colSpan; c++)
                {
                    bool fits = true;
                    for (int dr = 0; dr < rowSpan && fits; dr++)
                        for (int dc = 0; dc < colSpan && fits; dc++)
                            if (occupied.Contains((r + dr, c + dc))) fits = false;
                    if (fits) { placeRow = r; placeCol = c; found = true; break; }
                }
            }
            if (!found) continue;

            for (int dr = 0; dr < rowSpan; dr++)
                for (int dc = 0; dc < colSpan; dc++)
                    occupied.Add((placeRow + dr, placeCol + dc));

            for (int dr = 0; dr < rowSpan; dr++)
                for (int dc = 0; dc < colSpan; dc++)
                    if (placeRow + dr == targetRow && placeCol + dc == targetCol)
                        return widget.WidgetId;
        }

        return null;
    }

    /// <summary>
    /// Swap the dragged widget with the target widget in WidgetOrder.
    /// Only updates logical state (order, position overrides, logical position).
    /// Grid positions are NOT updated during drag to avoid WinUI 3 rendering bugs.
    /// The final layout is applied in OnWidgetPointerReleased's finally block.
    /// </summary>
    private void PerformSwap(string targetWidgetId, Grid sourceGrid, Windows.Foundation.Point cursorPage)
    {
        if (_draggingWidgetId == null) return;

        // Swap the two widget IDs in the widget order
        var order = new System.Collections.Generic.List<string>(_widgetService.WidgetOrder);
        var dragIdx = order.IndexOf(_draggingWidgetId);
        var targetIdx = order.IndexOf(targetWidgetId);
        if (dragIdx < 0 || targetIdx < 0) return;

        (order[dragIdx], order[targetIdx]) = (order[targetIdx], order[dragIdx]);
        _widgetService.SetOrder(order);

        // Set explicit position overrides so both widgets land at the expected
        // positions regardless of size differences. The packing algorithm honors
        // these and reserves their cells before placing other widgets.
        _positionOverrides[_draggingWidgetId] = (_currentTargetRow, _currentTargetCol);
        _positionOverrides[targetWidgetId] = (_dragLogicalRow, _dragLogicalCol);

        // Update logical position to the override position (not the cursor cell).
        // This ensures FindWidgetAtCell correctly detects when the cursor leaves
        // the dragged widget's new footprint.
        _dragLogicalRow = _currentTargetRow;
        _dragLogicalCol = _currentTargetCol;

        // Remember the target to prevent re-swap when cursor stays in its footprint
        _lastSwapTargetId = targetWidgetId;

        // Skip grid rebuild during drag — only update logical state.
        // The dragged widget's TranslateTransform (set by OnWidgetPointerMoved)
        // continues to track the cursor using the original grid position.
        // Other widgets stay in place visually. The full rebuild in
        // OnWidgetPointerReleased's finally block applies the final layout
        // using position overrides.
        //
        // This avoids a WinUI 3 rendering bug where changing Grid.Row/Col on
        // sibling containers while one has an active RenderTransform causes
        // ALL container content to become transparent.
    }

    /// <summary>
    /// Set Grid.Row/Column/RowSpan/ColumnSpan on a container only if the values
    /// actually changed. This minimizes layout invalidations during drag — the
    /// WinUI 3 rendering pipeline fails to redraw sibling content when too many
    /// Grid.SetRow/Col calls fire in the same pass as a RenderTransform change.
    /// </summary>
    private static void SetGridPositionIfChanged(Border container, int row, int col, int rowSpan, int colSpan)
    {
        if (Grid.GetRow(container) != row) Grid.SetRow(container, row);
        if (Grid.GetColumn(container) != col) Grid.SetColumn(container, col);
        if (Grid.GetRowSpan(container) != rowSpan) Grid.SetRowSpan(container, rowSpan);
        if (Grid.GetColumnSpan(container) != colSpan) Grid.SetColumnSpan(container, colSpan);
    }

    /// <summary>
    /// Recompute Grid.Row/Column for all right-panel containers in-place,
    /// without removing/adding children (preserves pointer capture during drag).
    /// </summary>
    private void RebuildRightPanelInPlace(int columns)
    {
        var widgets = _widgetService.VisibleWidgets;
        if (widgets.Count == 0) return;

        // Only update column definitions if count changed (minimizes layout invalidation)
        if (HomePreviewGrid.ColumnDefinitions.Count != columns)
        {
            HomePreviewGrid.ColumnDefinitions.Clear();
            for (int c = 0; c < columns; c++)
                HomePreviewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var rightPanelWidth = HomePreviewGrid.ActualWidth;
        if (rightPanelWidth <= 0) rightPanelWidth = columns * _widgetService.ColumnWidth;
        var widgetHeight = rightPanelWidth / columns;

        var occupied = new System.Collections.Generic.HashSet<(int, int)>();
        int maxRowUsed = -1;

        // Pass 1: Place widgets with explicit position overrides first.
        // Their cells are reserved so pass-2 widgets pack around them.
        foreach (var widget in widgets)
        {
            if (!_positionOverrides.TryGetValue(widget.WidgetId, out var pos)) continue;

            var config = widget.Config;
            var hasTitle = !string.IsNullOrEmpty(widget.Title);
            var colSpan = config.AlwaysFillRow ? columns : System.Math.Min(config.MaxColumns, columns);
            var contentRows = widget.GetRequiredRows(colSpan);
            var totalRowSpan = contentRows + (hasTitle ? 1 : 0);

            for (int dr = 0; dr < totalRowSpan; dr++)
                for (int dc = 0; dc < colSpan; dc++)
                    occupied.Add((pos.row + dr, pos.col + dc));

            for (int r = 0; r < totalRowSpan; r++)
            {
                var height = (r == 0 && hasTitle) ? WidgetConfig.TitleHeight : widgetHeight;
                EnsureRowDefinition(HomePreviewGrid, pos.row + r, height);
                maxRowUsed = System.Math.Max(maxRowUsed, pos.row + r);
            }

            if (_containers.TryGetValue(widget.WidgetId, out var container))
            {
                SetGridPositionIfChanged(container, pos.row, pos.col, totalRowSpan, colSpan);
            }
        }

        // Pass 2: Pack remaining widgets (no override) using first-fit
        foreach (var widget in widgets)
        {
            if (_positionOverrides.ContainsKey(widget.WidgetId)) continue;

            var config = widget.Config;
            var hasTitle = !string.IsNullOrEmpty(widget.Title);
            var colSpan = config.AlwaysFillRow ? columns : System.Math.Min(config.MaxColumns, columns);
            var contentRows = widget.GetRequiredRows(colSpan);
            var totalRowSpan = contentRows + (hasTitle ? 1 : 0);

            int placeRow = 0, placeCol = 0;
            bool found = false;
            for (int r = 0; r < 100 && !found; r++)
            {
                for (int c = 0; c <= columns - colSpan; c++)
                {
                    bool fits = true;
                    for (int dr = 0; dr < totalRowSpan && fits; dr++)
                        for (int dc = 0; dc < colSpan && fits; dc++)
                            if (occupied.Contains((r + dr, c + dc))) fits = false;
                    if (fits) { placeRow = r; placeCol = c; found = true; break; }
                }
            }
            if (!found) continue;

            for (int dr = 0; dr < totalRowSpan; dr++)
                for (int dc = 0; dc < colSpan; dc++)
                    occupied.Add((placeRow + dr, placeCol + dc));

            for (int r = 0; r < totalRowSpan; r++)
            {
                var height = (r == 0 && hasTitle) ? WidgetConfig.TitleHeight : widgetHeight;
                EnsureRowDefinition(HomePreviewGrid, placeRow + r, height);
                maxRowUsed = System.Math.Max(maxRowUsed, placeRow + r);
            }

            if (_containers.TryGetValue(widget.WidgetId, out var container))
            {
                SetGridPositionIfChanged(container, placeRow, placeCol, totalRowSpan, colSpan);
            }
        }

        // Trim excess row definitions (prevents stale rows from previous layouts)
        if (maxRowUsed >= 0)
            TrimRowDefinitions(HomePreviewGrid, maxRowUsed + 1);
    }

    /// <summary>
    /// Recompute Grid.Row/Column for all left-panel containers in-place.
    /// </summary>
    private void RebuildLeftPanelInPlace()
    {
        var hiddenWidgets = new System.Collections.Generic.List<IHomeWidget>();
        foreach (var widgetId in _widgetService.WidgetOrder)
        {
            if (!_widgetService.IsVisible(widgetId))
            {
                var widget = _widgetService.GetWidget(widgetId);
                if (widget != null) hiddenWidgets.Add(widget);
            }
        }
        if (hiddenWidgets.Count == 0) return;

        // Only update column definitions if count changed (minimizes layout invalidation)
        if (AvailableWidgetsGrid.ColumnDefinitions.Count != AvailableColumns)
        {
            AvailableWidgetsGrid.ColumnDefinitions.Clear();
            for (int c = 0; c < AvailableColumns; c++)
                AvailableWidgetsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var leftPanelContentWidth = LeftPanelColumn.Width.Value - (2 * GridPadding);
        var widgetHeight = (leftPanelContentWidth - ((AvailableColumns - 1) * ColumnSpacing)) / AvailableColumns;

        var occupied = new System.Collections.Generic.HashSet<(int, int)>();
        int maxRowUsed = -1;

        // Pass 1: Place widgets with position overrides first
        foreach (var widget in hiddenWidgets)
        {
            if (!_positionOverrides.TryGetValue(widget.WidgetId, out var pos)) continue;

            var config = widget.Config;
            var colSpan = config.AlwaysFillRow ? AvailableColumns : System.Math.Min(config.MaxColumns, AvailableColumns);
            var rowSpan = widget.GetRequiredRows(colSpan);

            for (int dr = 0; dr < rowSpan; dr++)
                for (int dc = 0; dc < colSpan; dc++)
                    occupied.Add((pos.row + dr, pos.col + dc));

            for (int r = 0; r < rowSpan; r++)
            {
                EnsureRowDefinition(AvailableWidgetsGrid, pos.row + r, widgetHeight);
                maxRowUsed = System.Math.Max(maxRowUsed, pos.row + r);
            }

            if (_containers.TryGetValue(widget.WidgetId, out var container))
            {
                SetGridPositionIfChanged(container, pos.row, pos.col, rowSpan, colSpan);
            }
        }

        // Pass 2: Pack remaining widgets using first-fit
        foreach (var widget in hiddenWidgets)
        {
            if (_positionOverrides.ContainsKey(widget.WidgetId)) continue;

            var config = widget.Config;
            var colSpan = config.AlwaysFillRow ? AvailableColumns : System.Math.Min(config.MaxColumns, AvailableColumns);
            var rowSpan = widget.GetRequiredRows(colSpan);

            int placeRow = 0, placeCol = 0;
            bool found = false;
            for (int r = 0; r < 100 && !found; r++)
            {
                for (int c = 0; c <= AvailableColumns - colSpan; c++)
                {
                    bool fits = true;
                    for (int dr = 0; dr < rowSpan && fits; dr++)
                        for (int dc = 0; dc < colSpan && fits; dc++)
                            if (occupied.Contains((r + dr, c + dc))) fits = false;
                    if (fits) { placeRow = r; placeCol = c; found = true; break; }
                }
            }
            if (!found) continue;

            for (int dr = 0; dr < rowSpan; dr++)
                for (int dc = 0; dc < colSpan; dc++)
                    occupied.Add((placeRow + dr, placeCol + dc));

            for (int r = 0; r < rowSpan; r++)
            {
                EnsureRowDefinition(AvailableWidgetsGrid, placeRow + r, widgetHeight);
                maxRowUsed = System.Math.Max(maxRowUsed, placeRow + r);
            }

            if (_containers.TryGetValue(widget.WidgetId, out var container))
            {
                SetGridPositionIfChanged(container, placeRow, placeCol, rowSpan, colSpan);
            }
        }

        // Trim excess row definitions (prevents stale rows from previous layouts)
        if (maxRowUsed >= 0)
            TrimRowDefinitions(AvailableWidgetsGrid, maxRowUsed + 1);
    }

    /// <summary>
    /// After a swap, the dragged widget's Border container has moved to a new logical
    /// Grid position. Recompute the TranslateTransform so the widget continues to
    /// appear at the cursor position (compensating for the layout shift).
    /// </summary>
    private void FixDragTransform(Border border, Grid sourceGrid, Windows.Foundation.Point cursorPage)
    {
        var gridToPage = sourceGrid.TransformToVisual(this);
        var containerOrigin = gridToPage.TransformPoint(new Windows.Foundation.Point(0, 0));

        double rowOffset = 0;
        for (int r = 0; r < _dragLogicalRow && r < sourceGrid.RowDefinitions.Count; r++)
            rowOffset += sourceGrid.RowDefinitions[r].ActualHeight;

        double colOffset = 0;
        for (int c = 0; c < _dragLogicalCol && c < sourceGrid.ColumnDefinitions.Count; c++)
            colOffset += sourceGrid.ColumnDefinitions[c].ActualWidth;

        var newLayoutX = containerOrigin.X + colOffset + sourceGrid.Padding.Left;
        var newLayoutY = containerOrigin.Y + rowOffset + sourceGrid.Padding.Top;

        // intended border top-left = cursor - pointerOffset
        var intendedX = cursorPage.X - _pointerOffset.X;
        var intendedY = cursorPage.Y - _pointerOffset.Y;

        var newTransform = new TranslateTransform
        {
            X = intendedX - newLayoutX,
            Y = intendedY - newLayoutY,
        };
        border.RenderTransform = newTransform;
    }

    /// <summary>
    /// Apply displacement layout: reposition all widgets in the target grid to make room
    /// at the specified row/col. The dragged widget is NOT repositioned (it follows the cursor).
    /// Uses occupied-cells approach with target reservation to correctly handle variable-size widgets.
    /// </summary>
    private void ApplyDisplacementLayout(Grid targetGrid, int targetRow, int targetCol)
    {
        var columns = targetGrid == HomePreviewGrid ? _widgetService.Columns : AvailableColumns;
        var widgets = GetWidgetsForGrid(targetGrid);

        // Get the dragged widget's size to reserve target cells
        var draggedWidget = _draggingWidgetId != null
            ? widgets.Find(w => w.WidgetId == _draggingWidgetId)
            : null;
        var dragColSpan = draggedWidget != null
            ? (draggedWidget.Config.AlwaysFillRow ? columns : System.Math.Min(draggedWidget.Config.MaxColumns, columns))
            : 1;
        var dragRowSpan = draggedWidget != null ? draggedWidget.GetRequiredRows(dragColSpan) : 1;

        // Mark target cells as occupied (reserved for where the dragged widget will land)
        var occupiedCells = new System.Collections.Generic.HashSet<(int row, int col)>();
        for (int dr = 0; dr < dragRowSpan; dr++)
        {
            for (int dc = 0; dc < dragColSpan; dc++)
            {
                occupiedCells.Add((targetRow + dr, targetCol + dc));
            }
        }

        // Place each non-dragged widget in the next available position
        foreach (var widget in widgets)
        {
            if (widget.WidgetId == _draggingWidgetId) continue;

            if (!_containers.TryGetValue(widget.WidgetId, out var container)) continue;

            var columnSpan = widget.Config.AlwaysFillRow ? columns : System.Math.Min(widget.Config.MaxColumns, columns);
            var rowSpan = widget.GetRequiredRows(columnSpan);

            // Find next available position (same algorithm as BuildRightPanel)
            int placeRow = 0, placeCol = 0;
            bool found = false;
            for (int r = 0; r < 100 && !found; r++)
            {
                for (int c = 0; c <= columns - columnSpan; c++)
                {
                    bool fits = true;
                    for (int dr = 0; dr < rowSpan && fits; dr++)
                    {
                        for (int dc = 0; dc < columnSpan && fits; dc++)
                        {
                            if (occupiedCells.Contains((r + dr, c + dc)))
                                fits = false;
                        }
                    }
                    if (fits)
                    {
                        placeRow = r;
                        placeCol = c;
                        found = true;
                        break;
                    }
                }
            }
            if (!found) continue;

            // Mark cells as occupied
            for (int dr = 0; dr < rowSpan; dr++)
                for (int dc = 0; dc < columnSpan; dc++)
                    occupiedCells.Add((placeRow + dr, placeCol + dc));

            // Ensure row definitions exist for proper sizing
            for (int dr = 0; dr < rowSpan; dr++)
            {
                var cellHeight = targetGrid.ActualWidth / columns;
                EnsureRowDefinition(targetGrid, placeRow + dr, cellHeight);
            }

            Grid.SetRow(container, placeRow);
            Grid.SetColumn(container, placeCol);
        }
    }

    /// <summary>
    /// Get the list of widgets currently in the specified grid.
    /// </summary>
    private System.Collections.Generic.List<IHomeWidget> GetWidgetsForGrid(Grid targetGrid)
    {
        var widgets = new System.Collections.Generic.List<IHomeWidget>();
        if (targetGrid == HomePreviewGrid)
        {
            foreach (var widget in _widgetService.VisibleWidgets)
            {
                widgets.Add(widget);
            }
        }
        else if (targetGrid == AvailableWidgetsGrid)
        {
            foreach (var widgetId in _widgetService.WidgetOrder)
            {
                if (!_widgetService.IsVisible(widgetId))
                {
                    var widget = _widgetService.GetWidget(widgetId);
                    if (widget != null) widgets.Add(widget);
                }
            }
        }
        return widgets;
    }

    /// <summary>
    /// Compute the visual order of widgets if the dragged widget were placed at the target position.
    /// Uses occupied-cells layout to handle variable-size widgets correctly.
    /// </summary>
    private System.Collections.Generic.List<IHomeWidget> ComputeDisplayOrder(
        System.Collections.Generic.List<IHomeWidget> widgets,
        int targetRow, int targetCol, int columns)
    {
        // Find the dragged widget
        IHomeWidget? draggedWidget = null;
        if (_draggingWidgetId != null)
        {
            foreach (var w in widgets)
            {
                if (w.WidgetId == _draggingWidgetId)
                {
                    draggedWidget = w;
                    break;
                }
            }
        }

        // Reserve target cells for the dragged widget
        var occupiedCells = new System.Collections.Generic.HashSet<(int row, int col)>();
        int dragColSpan = 1, dragRowSpan = 1;
        if (draggedWidget != null)
        {
            dragColSpan = draggedWidget.Config.AlwaysFillRow ? columns : System.Math.Min(draggedWidget.Config.MaxColumns, columns);
            dragRowSpan = draggedWidget.GetRequiredRows(dragColSpan);
            for (int dr = 0; dr < dragRowSpan; dr++)
                for (int dc = 0; dc < dragColSpan; dc++)
                    occupiedCells.Add((targetRow + dr, targetCol + dc));
        }

        // Place each non-dragged widget, tracking their visual order
        var placedWidgets = new System.Collections.Generic.List<IHomeWidget>();
        var placedPositions = new System.Collections.Generic.Dictionary<IHomeWidget, (int row, int col)>();

        foreach (var widget in widgets)
        {
            if (widget.WidgetId == _draggingWidgetId) continue;

            var columnSpan = widget.Config.AlwaysFillRow ? columns : System.Math.Min(widget.Config.MaxColumns, columns);
            var rowSpan = widget.GetRequiredRows(columnSpan);

            int placeRow = 0, placeCol = 0;
            bool found = false;
            for (int r = 0; r < 100 && !found; r++)
            {
                for (int c = 0; c <= columns - columnSpan; c++)
                {
                    bool fits = true;
                    for (int dr = 0; dr < rowSpan && fits; dr++)
                        for (int dc = 0; dc < columnSpan && fits; dc++)
                            if (occupiedCells.Contains((r + dr, c + dc))) fits = false;
                    if (fits) { placeRow = r; placeCol = c; found = true; break; }
                }
            }
            if (!found) continue;

            for (int dr = 0; dr < rowSpan; dr++)
                for (int dc = 0; dc < columnSpan; dc++)
                    occupiedCells.Add((placeRow + dr, placeCol + dc));

            placedWidgets.Add(widget);
            placedPositions[widget] = (placeRow, placeCol);
        }

        // Build result: scan positions in row-major order, inserting dragged widget at target
        var result = new System.Collections.Generic.List<IHomeWidget>();
        var added = new System.Collections.Generic.HashSet<string>();

        for (int r = 0; r < 100; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                // Insert dragged widget when we reach its target position
                if (r == targetRow && c == targetCol && draggedWidget != null && !added.Contains(draggedWidget.WidgetId))
                {
                    result.Add(draggedWidget);
                    added.Add(draggedWidget.WidgetId);
                }

                // Add any widget placed at this position
                foreach (var widget in placedWidgets)
                {
                    if (!added.Contains(widget.WidgetId) && placedPositions[widget] == (r, c))
                    {
                        result.Add(widget);
                        added.Add(widget.WidgetId);
                    }
                }
            }
        }

        // If dragged widget wasn't added yet (target beyond all placed widgets), append it
        if (draggedWidget != null && !added.Contains(draggedWidget.WidgetId))
        {
            result.Add(draggedWidget);
        }

        return result;
    }

    /// <summary>
    /// Find the Border container for a widget by searching both grids.
    /// </summary>
    private Border? FindWidgetContainer(string widgetId)
    {
        foreach (var child in AvailableWidgetsGrid.Children)
        {
            if (child is Border border && border.Tag is string tag && tag.StartsWith(widgetId + "|"))
            {
                return border;
            }
        }
        foreach (var child in HomePreviewGrid.Children)
        {
            if (child is Border border && border.Tag is string tag && tag.StartsWith(widgetId + "|"))
            {
                return border;
            }
        }
        return null;
    }

    /// <summary>
    /// Build the target order for SetOrder() based on the drop position.
    /// </summary>
    private System.Collections.Generic.List<string>? BuildTargetOrder(Grid targetGrid, string widgetId, int targetRow, int targetCol)
    {
        var columns = targetGrid == HomePreviewGrid ? _widgetService.Columns : AvailableColumns;
        var widgets = GetWidgetsForGrid(targetGrid);
        var displayOrder = ComputeDisplayOrder(widgets, targetRow, targetCol, columns);

        var order = new System.Collections.Generic.List<string>();
        foreach (var widget in displayOrder)
        {
            order.Add(widget.WidgetId);
        }
        return order;
    }
}
