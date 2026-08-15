using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XmaX.Services;
using XmaX.WidgetFramework;
using XmaX.Widgets;

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

                // Register as IHomeWidget (backward compat for SetPage)
                // Content may be the widget directly or wrapped in a ScrollViewer
                var content = gridWidget.Content;
                if (content is ScrollViewer sv)
                    content = sv.Content;
                if (content is IHomeWidget homeWidget)
                {
                    _widgetService.Register(homeWidget);
                }
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

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Ensure critical widgets are always visible
        _widgetService.SetVisible("power", true);
        _widgetService.SetVisible("charge_limit", true);
    }

    /// <summary>
    /// Build the widget list from stored GridWidgets, applying config-driven sizes.
    /// Reuses the same widget instances created in the constructor (not new ones).
    /// </summary>
    private void SetupWidgets()
    {
        bool hasConfig = _widgetService.VisibleWidgets.Count > 0;

        var gridWidgets = new List<GridWidget>();

        if (hasConfig)
        {
            // Use visible widgets from config (order set by WidgetService)
            foreach (var homeWidget in _widgetService.VisibleWidgets)
            {
                if (_gridWidgets.TryGetValue(homeWidget.WidgetId, out var gw))
                {
                    // Apply config-driven sizes
                    var (colSpan, rowSpan) = _widgetService.GetWidgetSpan(homeWidget.WidgetId);
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
