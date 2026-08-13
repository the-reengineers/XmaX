using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XmaX.Services;
using XmaX.Widgets;

namespace XmaX.Pages;

/// <summary>
/// Home page with configurable widget grid layout.
/// Widgets are registered with WidgetService and displayed in a grid.
/// Each widget has a Config that determines its column span and background style.
/// Widget height = page width / columns (all widgets same height).
/// </summary>
public sealed partial class HomePage : Page
{
    private readonly WidgetService _widgetService;

    // Widget instances (created once, registered with WidgetService)
    private readonly ProfilesWidget _profilesWidget;
    private readonly CpuTile _cpuTile;
    private readonly GpuTile _gpuTile;
    private readonly RamTile _ramTile;
    private readonly VramTile _vramTile;
    private readonly AdaptiveWidget _adaptiveWidget;
    private readonly ChargeLimitWidget _chargeLimitWidget;
    private readonly PowerWidget _powerWidget;

    public HomePage()
    {
        this.InitializeComponent();

        _widgetService = App.WidgetService;

        // Create widgets
        _cpuTile = new CpuTile();
        _gpuTile = new GpuTile();
        _ramTile = new RamTile();
        _vramTile = new VramTile();
        _profilesWidget = new ProfilesWidget();
        _adaptiveWidget = new AdaptiveWidget();
        _chargeLimitWidget = new ChargeLimitWidget();
        _powerWidget = new PowerWidget();

        // Register with WidgetService (default order)
        _widgetService.Register(_cpuTile);
        _widgetService.Register(_gpuTile);
        _widgetService.Register(_ramTile);
        _widgetService.Register(_vramTile);
        _widgetService.Register(_profilesWidget);
        _widgetService.Register(_adaptiveWidget);
        _widgetService.Register(_chargeLimitWidget);
        _widgetService.Register(_powerWidget);

        // Listen for layout changes
        _widgetService.PropertyChanged += OnWidgetServiceChanged;
        SubscribeToVisibleWidgets();

        // Listen for page size changes to recalculate widget heights
        this.SizeChanged += OnPageSizeChanged;

        // Defer initial grid build until page is fully loaded
        this.Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // Build grid after all widgets are fully loaded
        BuildGrid();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Ensure critical widgets are visible (override config if needed)
        _widgetService.SetVisible("power", true);
        _widgetService.SetVisible("charge_limit", true);
        // Grid will be built when page loads (see OnPageLoaded)
    }

    private void SubscribeToVisibleWidgets()
    {
        _widgetService.VisibleWidgets.CollectionChanged -= OnVisibleWidgetsChanged;
        _widgetService.VisibleWidgets.CollectionChanged += OnVisibleWidgetsChanged;
    }

    private void OnWidgetServiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WidgetService.Columns))
        {
            DispatcherQueue.TryEnqueue(BuildGrid);
        }
        else if (e.PropertyName == nameof(WidgetService.VisibleWidgets))
        {
            // Re-subscribe to the new collection's CollectionChanged event
            SubscribeToVisibleWidgets();
            DispatcherQueue.TryEnqueue(BuildGrid);
        }
    }

    private void OnVisibleWidgetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(BuildGrid);
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Rebuild grid when page width changes (recalculates all row heights including title rows)
        DispatcherQueue.TryEnqueue(BuildGrid);
    }

    /// <summary>
    /// Calculate widget height = page width / columns.
    /// All widgets have the same height.
    /// </summary>
    private double CalculateWidgetHeight()
    {
        var columns = _widgetService.Columns;
        // Use the grid's actual width (accounts for padding)
        var gridWidth = WidgetGrid.ActualWidth - WidgetGrid.Padding.Left - WidgetGrid.Padding.Right;
        if (gridWidth <= 0) return 100; // Fallback if not yet rendered
        return gridWidth / columns;
    }

    /// <summary>
    /// Rebuild the widget grid based on current VisibleWidgets and column count.
    /// Each widget's Config determines its column span and background style.
    /// Widgets can span multiple rows using GetRequiredRows().
    /// Full-width widgets (AlwaysFillRow=true) can have titles, adding extra height.
    /// </summary>
    private void BuildGrid()
    {
        try
        {
            WidgetGrid.Children.Clear();
            WidgetGrid.ColumnDefinitions.Clear();
            WidgetGrid.RowDefinitions.Clear();

            var columns = _widgetService.Columns;
            var widgets = _widgetService.VisibleWidgets;

            // Open log file for this build
            var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xmax_homepage.log");
            System.IO.File.WriteAllText(logPath, $"[HomePage] BuildGrid: {widgets.Count} visible widgets, {columns} columns\n");
            foreach (var w in widgets)
            {
                var colSpan = w.Config.AlwaysFillRow ? columns : Math.Min(w.Config.MaxColumns, columns);
                var rowSpan = w.GetRequiredRows(colSpan) + (!string.IsNullOrEmpty(w.Title) ? 1 : 0);
                System.IO.File.AppendAllText(logPath, $"  - {w.WidgetId}: colSpan={colSpan}, rowSpan={rowSpan}, title={w.Title ?? "null"}\n");
            }

            if (widgets.Count == 0) return;

            // Create column definitions
            for (int c = 0; c < columns; c++)
            {
                WidgetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            // Calculate standard row height = page width / columns
            var rowHeight = CalculateWidgetHeight();

            // Track occupied cells for multi-row widget layout
            var occupiedCells = new HashSet<(int row, int col)>();

            foreach (var widget in widgets)
            {
                var config = widget.Config;
                var hasTitle = !string.IsNullOrEmpty(widget.Title);

                // Determine column span for this widget
                var columnSpan = config.AlwaysFillRow ? columns : Math.Min(config.MaxColumns, columns);

                // Determine content row span for this widget
                var availableColumns = config.AlwaysFillRow ? columns : Math.Min(config.MaxColumns, columns);
                var contentRows = widget.GetRequiredRows(availableColumns);

                // Calculate total row span (including title row if present)
                var totalRowSpan = contentRows + (hasTitle ? 1 : 0);

                // Find the next available position where this widget fits
                int placeRow = 0;
                int placeCol = 0;
                bool found = false;
                int maxRows = 100; // Safety limit to prevent infinite loops

                System.IO.File.AppendAllText(logPath, $"  Searching for position for {widget.WidgetId} (rowSpan={totalRowSpan}, colSpan={columnSpan})...\n");

                while (!found && placeRow < maxRows)
                {
                    for (int c = 0; c <= columns - columnSpan; c++)
                    {
                        // Check if all cells for this widget are free
                        bool fits = true;
                        for (int dr = 0; dr < totalRowSpan && fits; dr++)
                        {
                            for (int dc = 0; dc < columnSpan && fits; dc++)
                            {
                                if (occupiedCells.Contains((placeRow + dr, c + dc)))
                                {
                                    fits = false;
                                }
                            }
                        }

                        if (fits)
                        {
                            placeCol = c;
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        placeRow++;
                    }
                }

                if (!found)
                {
                    System.IO.File.AppendAllText(logPath, $"  ERROR: Could not find position for {widget.WidgetId}\n");
                    continue; // Skip this widget
                }

                // Mark cells as occupied
                for (int dr = 0; dr < totalRowSpan; dr++)
                {
                    for (int dc = 0; dc < columnSpan; dc++)
                    {
                        occupiedCells.Add((placeRow + dr, placeCol + dc));
                    }
                }

                // Create row definitions with appropriate heights
                // If widget has title, first row is title height, rest are standard height
                for (int r = 0; r < totalRowSpan; r++)
                {
                    var rowIndex = placeRow + r;
                    var height = (r == 0 && hasTitle) ? WidgetConfig.TitleHeight : rowHeight;
                    EnsureRowDefinition(rowIndex, height);
                }

                System.IO.File.AppendAllText(logPath, $"  Placed {widget.WidgetId} at ({placeRow}, {placeCol}) with rowSpan={totalRowSpan}, colSpan={columnSpan}\n");

                var control = widget.Control as FrameworkElement;
                if (control != null)
                {
                    // Remove from any existing parent before adding
                    if (control.Parent is Panel parentPanel)
                    {
                        parentPanel.Children.Remove(control);
                    }
                    else if (control.Parent is Border parentBorder)
                    {
                        parentBorder.Child = null;
                    }

                    // Create container with title (if present) and card background
                    var container = CreateWidgetContainer(control, config, widget.Title);

                    Grid.SetRow(container, placeRow);
                    Grid.SetColumn(container, placeCol);
                    Grid.SetColumnSpan(container, columnSpan);
                    Grid.SetRowSpan(container, totalRowSpan);

                    try
                    {
                        WidgetGrid.Children.Add(container);
                    }
                    catch (Exception ex)
                    {
                        System.IO.File.AppendAllText(logPath, $"  WARNING: Could not add {widget.WidgetId} to grid: {ex.Message}\n");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xmax_homepage.log");
            System.IO.File.AppendAllText(logPath, $"[HomePage] BuildGrid EXCEPTION: {ex.Message}\n  Stack: {ex.StackTrace}\n");
        }
    }

    /// <summary>
    /// Creates a container for the widget with appropriate background style.
    /// Widgets handle their own titles internally.
    /// </summary>
    private FrameworkElement CreateWidgetContainer(FrameworkElement content, WidgetConfig config, string? title)
    {
        // Apply card background if interactive
        if (config.IsInteractiveCard)
        {
            // Check if content is already wrapped in a Border
            if (content.Parent is Border existingBorder)
            {
                // Reuse the existing Border
                return existingBorder;
            }

            // Ensure content is not already a child of another parent
            if (content.Parent is Panel parentPanel)
            {
                parentPanel.Children.Remove(content);
            }

            try
            {
                var border = new Border
                {
                    Background = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10),
                    Child = content
                };
                return border;
            }
            catch (Exception ex)
            {
                // If wrapping fails, just return the content without the Border
                var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xmax_homepage.log");
                System.IO.File.AppendAllText(logPath, $"  WARNING: Could not wrap {content.GetType().Name} in Border: {ex.Message}\n");
                return content;
            }
        }
        else
        {
            // Transparent: just return the content
            return content;
        }
    }

    private void EnsureRowDefinition(int rowIndex, double height)
    {
        while (WidgetGrid.RowDefinitions.Count <= rowIndex)
        {
            WidgetGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });
        }
    }
}
