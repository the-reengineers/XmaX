using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XmaX.Services;
using XmaX.Widgets;

namespace XmaX.Pages;

/// <summary>
/// Home page with configurable widget grid layout.
/// Widgets are registered with WidgetService and displayed in a grid.
/// PowerWidget spans the full row; other widgets fill cells left-to-right, top-to-bottom.
/// </summary>
public sealed partial class HomePage : Page
{
    private readonly WidgetService _widgetService;

    // Widget instances (created once, registered with WidgetService)
    private readonly ProfilesWidget _profilesWidget;
    private readonly MetricsWidget _metricsWidget;
    private readonly AdaptiveWidget _adaptiveWidget;
    private readonly ChargeLimitWidget _chargeLimitWidget;
    private readonly PowerWidget _powerWidget;

    /// <summary>Widgets that span the full row (all columns).</summary>
    private static readonly HashSet<string> FullRowWidgets = new() { "power" };

    public HomePage()
    {
        this.InitializeComponent();

        _widgetService = App.WidgetService;

        // Create widgets
        _profilesWidget = new ProfilesWidget();
        _metricsWidget = new MetricsWidget();
        _adaptiveWidget = new AdaptiveWidget();
        _chargeLimitWidget = new ChargeLimitWidget();
        _powerWidget = new PowerWidget();

        // Register with WidgetService (default order)
        _widgetService.Register(_profilesWidget);
        _widgetService.Register(_metricsWidget);
        _widgetService.Register(_adaptiveWidget);
        _widgetService.Register(_chargeLimitWidget);
        _widgetService.Register(_powerWidget);

        // Listen for layout changes
        _widgetService.PropertyChanged += OnWidgetServiceChanged;
        _widgetService.VisibleWidgets.CollectionChanged += OnVisibleWidgetsChanged;

        // Build initial layout
        BuildGrid();
    }

    private void OnWidgetServiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WidgetService.Columns))
        {
            DispatcherQueue.TryEnqueue(BuildGrid);
        }
    }

    private void OnVisibleWidgetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(BuildGrid);
    }

    /// <summary>
    /// Rebuild the widget grid based on current VisibleWidgets and column count.
    /// Full-row widgets (power) span all columns.
    /// Other widgets fill cells left-to-right, wrapping to new rows.
    /// </summary>
    private void BuildGrid()
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

        // Calculate rows needed
        int currentRow = 0;
        int currentCol = 0;

        foreach (var widget in widgets)
        {
            var isFullRow = FullRowWidgets.Contains(widget.WidgetId);

            if (isFullRow)
            {
                // Full-row widget: move to next row, span all columns
                if (currentCol > 0)
                {
                    currentRow++;
                    currentCol = 0;
                }

                // Ensure row definition exists
                EnsureRowDefinition(currentRow);

                var control = widget.Control as FrameworkElement;
                if (control != null)
                {
                    Grid.SetRow(control, currentRow);
                    Grid.SetColumn(control, 0);
                    Grid.SetColumnSpan(control, columns);
                    WidgetGrid.Children.Add(control);
                }

                currentRow++;
                currentCol = 0;
            }
            else
            {
                // Normal widget: place in current cell, wrap to next row if needed
                if (currentCol >= columns)
                {
                    currentRow++;
                    currentCol = 0;
                }

                EnsureRowDefinition(currentRow);

                var control = widget.Control as FrameworkElement;
                if (control != null)
                {
                    Grid.SetRow(control, currentRow);
                    Grid.SetColumn(control, currentCol);
                    Grid.SetColumnSpan(control, 1);
                    WidgetGrid.Children.Add(control);
                }

                currentCol++;
            }
        }
    }

    private void EnsureRowDefinition(int rowIndex)
    {
        while (WidgetGrid.RowDefinitions.Count <= rowIndex)
        {
            WidgetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
    }
}
