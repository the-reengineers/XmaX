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
    // Column count bounds (matching PROJECT.md Step 21/23)
    public const int MinColumns = 3;
    public const int MaxColumns = 5;

    private readonly PipeClient _pipe;

    // Registered widgets keyed by WidgetId
    private readonly Dictionary<string, IHomeWidget> _widgets = new();

    // Current layout state
    private List<string> _widgetOrder = new();
    private readonly Dictionary<string, bool> _visibility = new();
    private int _columns = MinColumns;

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

    /// <summary>Current column count (3–5).</summary>
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
    /// Move a widget one position earlier in the display order.
    /// No-op if already first or not registered.
    /// </summary>
    public void MoveUp(string widgetId)
    {
        var index = _widgetOrder.IndexOf(widgetId);
        if (index <= 0) return; // -1 (not found) or 0 (already first)

        _widgetOrder.RemoveAt(index);
        _widgetOrder.Insert(index - 1, widgetId);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetOrder)));
        RefreshVisibleList();
    }

    /// <summary>
    /// Move a widget one position later in the display order.
    /// No-op if already last or not registered.
    /// </summary>
    public void MoveDown(string widgetId)
    {
        var index = _widgetOrder.IndexOf(widgetId);
        if (index < 0 || index >= _widgetOrder.Count - 1) return;

        _widgetOrder.RemoveAt(index);
        _widgetOrder.Insert(index + 1, widgetId);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetOrder)));
        RefreshVisibleList();
    }

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
    /// Load layout from an AppConfig's HomeLayout.
    /// Unknown widget IDs in the config are ignored.
    /// Widgets not in the config retain their default visibility (true).
    /// </summary>
    public void LoadLayout(HomeLayout layout)
    {
        if (layout == null) return;

        // Apply column count
        Columns = layout.Columns;

        // Apply widget order (only registered widgets)
        if (layout.WidgetOrder.Count > 0)
        {
            SetOrder(layout.WidgetOrder);
        }

        // Apply visibility
        foreach (var kvp in layout.WidgetVisibility)
        {
            if (_widgets.ContainsKey(kvp.Key))
            {
                _visibility[kvp.Key] = kvp.Value;
            }
        }

        RefreshVisibleList();
    }

    /// <summary>
    /// Build the current layout as a HomeLayout for serialization.
    /// </summary>
    public HomeLayout GetLayout()
    {
        return new HomeLayout
        {
            WidgetOrder = new List<string>(_widgetOrder),
            WidgetVisibility = new Dictionary<string, bool>(_visibility),
            Columns = _columns,
        };
    }

    /// <summary>
    /// Save the current layout to config.json via set_config.
    /// Only sends the home_layout field (partial update).
    /// </summary>
    public async Task SaveLayoutAsync()
    {
        var layout = GetLayout();

        var widgetOrderArray = new JsonArray();
        foreach (var id in layout.WidgetOrder)
        {
            widgetOrderArray.Add(id);
        }

        var visibilityObj = new JsonObject();
        foreach (var kvp in layout.WidgetVisibility)
        {
            visibilityObj[kvp.Key] = kvp.Value;
        }

        var homeLayoutObj = new JsonObject
        {
            ["widget_order"] = widgetOrderArray,
            ["widget_visibility"] = visibilityObj,
            ["columns"] = layout.Columns,
        };

        var payload = new JsonObject
        {
            ["home_layout"] = homeLayoutObj,
        };

        await _pipe.SendCommandAsync("set_config", payload).ConfigureAwait(false);
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
