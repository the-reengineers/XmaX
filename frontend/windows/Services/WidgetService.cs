using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using XmaX.Models;
using XmaX.Widgets;

namespace XmaX.Services;

/// <summary>
/// Manages home page widget registration, display order, visibility, and column count.
/// Persists layout to config.json via set_config on the backend.
/// </summary>
/// <remarks>
/// Thread safety: public methods should be called from the UI thread.
/// PropertyChanged is raised on the calling thread.
/// </remarks>
public sealed class WidgetService : INotifyPropertyChanged
{
    // Column count bounds (3-4 only)
    public const int MinColumns = 3;
    public const int MaxColumns = 4;
    public const int DefaultColumnWidth = 140;
    public const int DefaultWindowHeight = 600;

    private readonly PipeClient _pipe;

    // Registered widgets keyed by WidgetId
    private readonly Dictionary<string, IHomeWidget> _widgets = new();

    // Current layout state
    private List<string> _widgetOrder = new();
    private readonly Dictionary<string, bool> _visibility = new();
    private readonly Dictionary<string, (int colSpan, int rowSpan)> _widgetSpans = new();
    private int _columns = MinColumns;
    private int _columnWidth = DefaultColumnWidth;
    private int _windowHeight = DefaultWindowHeight;

    // Observable list of visible widgets in display order (for UI binding)
    private ObservableCollection<IHomeWidget> _visibleWidgets = new();

    public WidgetService(PipeClient pipe)
    {
        _pipe = pipe;
    }

    // ===== Observable properties =====

    /// <summary>Visible widgets in display order. Updates when order or visibility changes.</summary>
    public ObservableCollection<IHomeWidget> VisibleWidgets
    {
        get => _visibleWidgets;
        private set
        {
            if (ReferenceEquals(_visibleWidgets, value)) return;
            _visibleWidgets = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VisibleWidgets)));
        }
    }

    /// <summary>All registered widget IDs in display order (including hidden).</summary>
    public IReadOnlyList<string> WidgetOrder => _widgetOrder.AsReadOnly();

    /// <summary>Current column count (3-4).</summary>
    public int Columns
    {
        get => _columns;
        set
        {
            var clamped = Math.Clamp(value, MinColumns, MaxColumns);
            if (_columns == clamped) return;
            _columns = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Columns)));
        }
    }

    /// <summary>Base column width in pixels (at 100% DPI).</summary>
    public int ColumnWidth
    {
        get => _columnWidth;
        set
        {
            if (value <= 0) return;
            if (_columnWidth == value) return;
            _columnWidth = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColumnWidth)));
        }
    }

    /// <summary>Window height in pixels (at 100% DPI).</summary>
    public int WindowHeight
    {
        get => _windowHeight;
        set
        {
            if (value <= 0) return;
            if (_windowHeight == value) return;
            _windowHeight = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WindowHeight)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ===== Registration =====

    /// <summary>
    /// Register a widget. If not already in the order list, it is appended at the end.
    /// New widgets default to visible.
    /// </summary>
    public void Register(IHomeWidget widget)
    {
        if (widget == null) throw new ArgumentNullException(nameof(widget));
        if (string.IsNullOrEmpty(widget.WidgetId))
            throw new ArgumentException("Widget ID cannot be empty", nameof(widget));

        _widgets[widget.WidgetId] = widget;

        // Append to order if not already present
        if (!_widgetOrder.Contains(widget.WidgetId))
        {
            _widgetOrder.Add(widget.WidgetId);
        }

        // Default to visible if not explicitly set
        if (!_visibility.ContainsKey(widget.WidgetId))
        {
            _visibility[widget.WidgetId] = true;
        }

        RefreshVisibleList();
    }

    /// <summary>
    /// Unregister a widget. Removes it from order and visibility tracking.
    /// </summary>
    public void Unregister(string widgetId)
    {
        if (string.IsNullOrEmpty(widgetId)) return;

        _widgets.Remove(widgetId);
        _widgetOrder.Remove(widgetId);
        _visibility.Remove(widgetId);

        RefreshVisibleList();
    }

    /// <summary>Whether a widget is registered.</summary>
    public bool IsRegistered(string widgetId) => _widgets.ContainsKey(widgetId);

    /// <summary>Get a registered widget by ID, or null if not found.</summary>
    public IHomeWidget? GetWidget(string widgetId) =>
        _widgets.TryGetValue(widgetId, out var w) ? w : null;

    /// <summary>Get the stored col_span/row_span for a widget, or (1,1) if not stored.</summary>
    public (int colSpan, int rowSpan) GetWidgetSpan(string widgetId) =>
        _widgetSpans.GetValueOrDefault(widgetId, (1, 1));

    /// <summary>
    /// Update widget order and spans from a list of GridWidgets.
    /// Called by HomePage after drag/resize to sync state before saving.
    /// </summary>
    public void UpdateLayoutFromGridWidgets(IEnumerable<(string id, int colSpan, int rowSpan)> widgets)
    {
        _widgetOrder.Clear();
        _widgetSpans.Clear();
        foreach (var (id, colSpan, rowSpan) in widgets)
        {
            _widgetOrder.Add(id);
            _widgetSpans[id] = (colSpan, rowSpan);
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetOrder)));
        RefreshVisibleList();
    }

    // ===== Visibility =====

    /// <summary>Whether a widget is visible.</summary>
    public bool IsVisible(string widgetId) =>
        _visibility.TryGetValue(widgetId, out var v) && v;

    /// <summary>Set widget visibility. Hidden widgets are excluded from VisibleWidgets.</summary>
    public void SetVisible(string widgetId, bool visible)
    {
        if (!_widgets.ContainsKey(widgetId)) return;

        if (_visibility.TryGetValue(widgetId, out var current) && current == visible)
            return;

        _visibility[widgetId] = visible;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs($"Visibility[{widgetId}]"));
        RefreshVisibleList();
    }

    /// <summary>Toggle a widget's visibility.</summary>
    public void ToggleVisible(string widgetId)
    {
        if (!_widgets.ContainsKey(widgetId)) return;
        SetVisible(widgetId, !IsVisible(widgetId));
    }

    // ===== Reordering =====

    /// <summary>
    /// Set the full widget order at once.
    /// Widgets not in the list are appended at the end.
    /// </summary>
    public void SetOrder(IEnumerable<string> orderedIds)
    {
        var newOrder = new List<string>();
        var seen = new HashSet<string>();

        // Add IDs in the specified order (only if registered)
        foreach (var id in orderedIds)
        {
            if (string.IsNullOrEmpty(id) || !_widgets.ContainsKey(id)) continue;
            if (seen.Add(id))
            {
                newOrder.Add(id);
            }
        }

        // Append any registered widgets not in the list
        foreach (var id in _widgetOrder)
        {
            if (seen.Add(id))
            {
                newOrder.Add(id);
            }
        }

        _widgetOrder = newOrder;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetOrder)));
        RefreshVisibleList();
    }

    // ===== Layout persistence =====

    /// <summary>
    /// Load widget span data from config (called by MainWindow during config load).
    /// Stores col_span/row_span for each widget so GetWidgetSpan returns saved sizes.
    /// </summary>
    public void LoadWidgetSpans(IEnumerable<WidgetEntry> widgets)
    {
        _widgetSpans.Clear();
        foreach (var w in widgets)
        {
            _widgetSpans[w.Id] = (w.ColSpan, w.RowSpan);
        }
    }

    /// <summary>
    /// Save the current layout to config.json via set_config.
    /// Only sends the home_layout field (partial update).
    /// Uses internal _widgetOrder + _widgetSpans (populated by UpdateLayoutFromGridWidgets).
    /// </summary>
    public async Task SaveLayoutAsync()
    {
        try
        {
            if (!_pipe.IsConnected)
            {
                System.Diagnostics.Debug.WriteLine("[WidgetService] SaveLayoutAsync skipped: pipe not connected");
                return;
            }

            var widgetsArray = new JsonArray();
            foreach (var id in _widgetOrder)
            {
                var (colSpan, rowSpan) = _widgetSpans.GetValueOrDefault(id, (1, 1));
                widgetsArray.Add(new JsonObject
                {
                    ["id"] = id,
                    ["col_span"] = colSpan,
                    ["row_span"] = rowSpan,
                });
            }

            var homeLayoutObj = new JsonObject
            {
                ["widgets"] = widgetsArray,
                ["columns"] = _columns,
                ["column_width"] = _columnWidth,
                ["window_height"] = _windowHeight,
            };

            var payload = new JsonObject
            {
                ["home_layout"] = homeLayoutObj,
            };

            await _pipe.SendCommandAsync("set_config", payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WidgetService] SaveLayoutAsync failed: {ex.Message}");
        }
    }

    // ===== Internal =====

    /// <summary>
    /// Rebuild the VisibleWidgets list from current order + visibility.
    /// </summary>
    private void RefreshVisibleList()
    {
        var visible = new ObservableCollection<IHomeWidget>();
        foreach (var id in _widgetOrder)
        {
            if (IsVisible(id) && _widgets.TryGetValue(id, out var widget))
            {
                visible.Add(widget);
            }
        }
        VisibleWidgets = visible;
    }
}
