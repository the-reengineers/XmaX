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
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Rebuild grid when navigating to this page (ensures layout is current after async config load)
        BuildGrid();
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
        // Recalculate row heights when page width changes
        DispatcherQueue.TryEnqueue(UpdateRowHeights);
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
    /// Update all row definitions to have the same calculated height.
    /// </summary>
    private void UpdateRowHeights()
    {
        var height = CalculateWidgetHeight();
        foreach (var rowDef in WidgetGrid.RowDefinitions)
        {
            rowDef.Height = new GridLength(height);
        }
    }

    /// <summary>
    /// Rebuild the widget grid based on current VisibleWidgets and column count.
    /// Each widget's Config determines its column span and background style.
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

            if (widgets.Count == 0) return;

            // Create column definitions
            for (int c = 0; c < columns; c++)
            {
                WidgetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            // Calculate widget height = page width / columns
            var widgetHeight = CalculateWidgetHeight();

            // Calculate rows needed
            int currentRow = 0;
            int currentCol = 0;

            foreach (var widget in widgets)
            {
                var config = widget.Config;

                // Determine column span for this widget
                // If AlwaysFillRow is true, span the full row width
                // Otherwise, use MaxColumns (clamped to grid column count)
                var columnSpan = config.AlwaysFillRow ? columns : Math.Min(config.MaxColumns, columns);

                // Check if widget fits in current row
                if (currentCol + columnSpan > columns)
                {
                    // Move to next row
                    currentRow++;
                    currentCol = 0;
                }

                EnsureRowDefinition(currentRow, widgetHeight);

                var control = widget.Control as FrameworkElement;
                if (control != null)
                {
                    // Remove from any existing parent before adding
                    if (control.Parent is Panel parentPanel)
                    {
                        parentPanel.Children.Remove(control);
                    }

                    // Apply background based on IsInteractiveCard
                    var container = CreateWidgetContainer(control, config);

                    Grid.SetRow(container, currentRow);
                    Grid.SetColumn(container, currentCol);
                    Grid.SetColumnSpan(container, columnSpan);
                    WidgetGrid.Children.Add(container);
                }

                currentCol += columnSpan;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomePage] BuildGrid failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a container for the widget with appropriate background style.
    /// </summary>
    private FrameworkElement CreateWidgetContainer(FrameworkElement content, WidgetConfig config)
    {
        if (config.IsInteractiveCard)
        {
            // Card style: Border with background
            var border = new Border
            {
                Background = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Child = content
            };
            return border;
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
