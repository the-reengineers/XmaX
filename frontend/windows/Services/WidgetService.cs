using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using XmaX.Models;

namespace XmaX.Services;

/// <summary>
/// Manages home page widget layout: column count, widget sizes, and config persistence.
/// The v2 widget framework (WidgetGridHost) handles rendering and drag-reflow directly.
/// </summary>
public sealed class WidgetService : INotifyPropertyChanged
{
    public const int MinColumns = 3;
    public const int MaxColumns = 4;
    public const int DefaultColumnWidth = 140;
    public const int DefaultWindowHeight = 600;

    private readonly PipeClient _pipe;

    // Layout state populated from config or drag/resize operations
    private readonly Dictionary<string, (int colSpan, int rowSpan)> _widgetSpans = new();
    private List<string> _configWidgetIds = new();
    private List<string> _hiddenWidgetIds = new();
    private int _columns = MinColumns;
    private int _columnWidth = DefaultColumnWidth;
    private int _windowHeight = DefaultWindowHeight;

    public WidgetService(PipeClient pipe)
    {
        _pipe = pipe;
    }

    // ===== Properties =====

    /// <summary>
    /// Ordered widget IDs from config. Empty if no config loaded (first run).
    /// Set by MainWindow during config load via LoadWidgetSpans.
    /// </summary>
    public IReadOnlyList<string> ConfigWidgetIds => _configWidgetIds.AsReadOnly();

    /// <summary>
    /// Hidden widget IDs (shown in editor window, not on home page).
    /// </summary>
    public IReadOnlyList<string> HiddenWidgetIds => _hiddenWidgetIds.AsReadOnly();

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

    // ===== Config loading =====

    /// <summary>
    /// Load widget span data from config (called by MainWindow during config load).
    /// Stores col_span/row_span for each widget and the ordered widget ID list.
    /// </summary>
    public void LoadWidgetSpans(IEnumerable<WidgetEntry> widgets, IEnumerable<string>? hiddenWidgets = null)
    {
        _widgetSpans.Clear();
        _configWidgetIds.Clear();
        foreach (var w in widgets)
        {
            _widgetSpans[w.Id] = (w.ColSpan, w.RowSpan);
            _configWidgetIds.Add(w.Id);
        }

        _hiddenWidgetIds.Clear();
        if (hiddenWidgets != null)
        {
            _hiddenWidgetIds.AddRange(hiddenWidgets);
        }
    }

    /// <summary>Get the stored col_span/row_span for a widget, or (1,1) if not stored.</summary>
    public (int colSpan, int rowSpan) GetWidgetSpan(string widgetId) =>
        _widgetSpans.GetValueOrDefault(widgetId, (1, 1));

    // ===== Layout persistence =====

    /// <summary>
    /// Update widget order and spans from the current GridWidget list.
    /// Called by HomePage after drag/resize to sync state before saving.
    /// </summary>
    public void UpdateLayoutFromGridWidgets(IEnumerable<(string id, int colSpan, int rowSpan)> widgets)
    {
        _widgetSpans.Clear();
        _configWidgetIds.Clear();
        foreach (var (id, colSpan, rowSpan) in widgets)
        {
            _configWidgetIds.Add(id);
            _widgetSpans[id] = (colSpan, rowSpan);
        }
    }

    /// <summary>
    /// Hide a widget (move from visible to hidden list).
    /// </summary>
    public void HideWidget(string widgetId)
    {
        // Remove from visible widgets
        _configWidgetIds.Remove(widgetId);
        _widgetSpans.Remove(widgetId);

        // Add to hidden widgets if not already there
        if (!_hiddenWidgetIds.Contains(widgetId))
        {
            _hiddenWidgetIds.Add(widgetId);
        }
    }

    /// <summary>
    /// Show a hidden widget (move from hidden to visible list with default size).
    /// </summary>
    public void ShowWidget(string widgetId)
    {
        // Remove from hidden widgets
        _hiddenWidgetIds.Remove(widgetId);

        // Add to visible widgets with default size (1x1)
        if (!_configWidgetIds.Contains(widgetId))
        {
            _configWidgetIds.Add(widgetId);
            _widgetSpans[widgetId] = (1, 1);
        }
    }

    /// <summary>
    /// Save the current layout to config.json via set_config.
    /// Only sends the home_layout field (partial update).
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
            foreach (var id in _configWidgetIds)
            {
                var (colSpan, rowSpan) = _widgetSpans.GetValueOrDefault(id, (1, 1));
                widgetsArray.Add(new JsonObject
                {
                    ["id"] = id,
                    ["col_span"] = colSpan,
                    ["row_span"] = rowSpan,
                });
            }

            var hiddenWidgetsArray = new JsonArray();
            foreach (var id in _hiddenWidgetIds)
            {
                hiddenWidgetsArray.Add(id);
            }

            var homeLayoutObj = new JsonObject
            {
                ["widgets"] = widgetsArray,
                ["hidden_widgets"] = hiddenWidgetsArray,
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
}
