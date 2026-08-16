using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XmaX.Services;
using XmaX.WidgetFramework;

namespace XmaX.Pages;

/// <summary>
/// Home page using the v2 widget framework with drag-reflow and resize.
/// Widgets are created via HomeWidgetFactory and displayed in WidgetGridHost.
/// </summary>
public sealed partial class HomePage : Page
{
    private readonly WidgetService _widgetService;
    private readonly Dictionary<string, GridWidget> _gridWidgets = new();
    private DragReflowController? _dragController;

    public HomePage()
    {
        this.InitializeComponent();

        _widgetService = App.WidgetService;

        // Create widgets via factory — one instance each, stored for reuse
        foreach (var id in HomeWidgetFactory.DefaultOrder)
        {
            var gridWidget = HomeWidgetFactory.CreateWidget(id);
            if (gridWidget != null)
            {
                _gridWidgets[id] = gridWidget;
            }
        }

        // Listen for column count changes from WidgetService
        _widgetService.PropertyChanged += OnWidgetServiceChanged;

        // Defer initial setup until page is fully loaded
        this.Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        SetupWidgets();
    }

    /// <summary>
    /// Build the widget list from stored GridWidgets, applying config-driven sizes.
    /// Uses config widget IDs if available, otherwise shows all defaults.
    /// </summary>
    private void SetupWidgets()
    {
        var configIds = _widgetService.ConfigWidgetIds;
        var gridWidgets = new List<GridWidget>();

        if (configIds.Count > 0)
        {
            // Use widget order and sizes from config
            foreach (var id in configIds)
            {
                if (_gridWidgets.TryGetValue(id, out var gw))
                {
                    var (colSpan, rowSpan) = _widgetService.GetWidgetSpan(id);
                    gw.ColumnSpan = colSpan;
                    gw.RowSpan = rowSpan;
                    gridWidgets.Add(gw);
                }
            }
        }
        else
        {
            // First run or no config — show all widgets with default sizes
            foreach (var id in HomeWidgetFactory.DefaultOrder)
            {
                if (_gridWidgets.TryGetValue(id, out var gw))
                {
                    gridWidgets.Add(gw);
                }
            }
        }

        GridHost.Columns = _widgetService.Columns;
        GridHost.SetWidgets(gridWidgets);

        // Attach drag controller
        _dragController = new DragReflowController(GridHost);
        _dragController.LayoutChanged += OnLayoutChanged;
        GridHost.SetDragController(_dragController);
    }

    private void OnWidgetServiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WidgetService.Columns))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                GridHost.Columns = _widgetService.Columns;
                GridHost.LayoutWidgets(animate: false);
            });
        }
    }

    /// <summary>
    /// Called after a drag or resize completes. Syncs widget order+sizes to WidgetService
    /// and persists to config.
    /// </summary>
    private async void OnLayoutChanged()
    {
        var widgetData = GridHost.Widgets
            .Select(w => (w.Id, w.ColumnSpan, w.RowSpan))
            .ToList();

        _widgetService.UpdateLayoutFromGridWidgets(widgetData);
        await _widgetService.SaveLayoutAsync();
    }
}
