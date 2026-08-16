using Microsoft.UI.Xaml.Controls;
using XmaX.Widgets;

namespace XmaX.WidgetFramework;

/// <summary>
/// Factory that maps widget IDs to GridWidget instances with default sizes and content.
/// Used by HomePage to construct home widgets for the v2 widget framework.
/// </summary>
public static class HomeWidgetFactory
{
    /// <summary>Default widget order (matches old HomePage registration order).</summary>
    public static readonly string[] DefaultOrder =
    {
        "cpu", "gpu", "ram", "vram", "profiles", "adaptive", "charge_limit", "power",
    };

    /// <summary>Default widget definitions: ID → (colSpan, rowSpan, alwaysFillRow).</summary>
    private static readonly Dictionary<string, (int colSpan, int rowSpan, bool alwaysFillRow)> Defaults = new()
    {
        ["cpu"]          = (1, 1, false),
        ["gpu"]          = (1, 1, false),
        ["ram"]          = (1, 1, false),
        ["vram"]         = (1, 1, false),
        ["profiles"]     = (3, 2, true),
        ["adaptive"]     = (2, 1, false),
        ["charge_limit"] = (1, 1, false),
        ["power"]        = (1, 1, false),
    };

    /// <summary>
    /// Create a GridWidget for the given ID with default size and content.
    /// Returns null if the ID is unknown.
    /// </summary>
    public static GridWidget? CreateWidget(string id)
    {
        if (!Defaults.TryGetValue(id, out var def))
            return null;

        var widget = new GridWidget(id, def.colSpan, def.rowSpan, def.alwaysFillRow);
        ApplySizeConstraints(widget, id);
        widget.Content = CreateContent(id);
        return widget;
    }

    /// <summary>
    /// Create a GridWidget for the given ID with overridden size.
    /// Returns null if the ID is unknown.
    /// </summary>
    public static GridWidget? CreateWidget(string id, int colSpan, int rowSpan)
    {
        if (!Defaults.TryGetValue(id, out var def))
            return null;

        var widget = new GridWidget(id, colSpan, rowSpan, def.alwaysFillRow);
        ApplySizeConstraints(widget, id);
        widget.Content = CreateContent(id);
        return widget;
    }

    /// <summary>
    /// Apply min/max size constraints for each widget type.
    /// Default: 1x1 single cell tile.
    /// Profiles: AlwaysFillRow=true, MinRowSpan=1, MaxRowSpan=int.MaxValue.
    /// </summary>
    private static void ApplySizeConstraints(GridWidget widget, string id)
    {
        // Default: all widgets are 1x1 single cell tiles
        widget.MinColumnSpan = 1;
        widget.MaxColumnSpan = 1;
        widget.MinRowSpan = 1;
        widget.MaxRowSpan = 1;

        // Profiles widget: AlwaysFillRow, can expand vertically
        if (id == "profiles")
        {
            widget.MinRowSpan = 1;
            widget.MaxRowSpan = int.MaxValue;
        }
    }

    /// <summary>
    /// Get all default home widgets in their default order.
    /// </summary>
    public static List<GridWidget> GetDefaultWidgets()
    {
        var widgets = new List<GridWidget>();
        foreach (var id in Defaults.Keys)
        {
            var w = CreateWidget(id);
            if (w != null) widgets.Add(w);
        }
        return widgets;
    }

    /// <summary>
    /// Create the UI content for a widget ID.
    /// </summary>
    private static object CreateContent(string id)
    {
        return id switch
        {
            "cpu" => new CpuTile(),
            "gpu" => new GpuTile(),
            "ram" => new RamTile(),
            "vram" => new VramTile(),
            "power" => new PowerWidget(),
            "charge_limit" => new ChargeLimitWidget(),
            "adaptive" => new AdaptiveWidget(),
            "profiles" => CreateProfilesContent(),
            _ => throw new ArgumentException($"Unknown widget ID: {id}"),
        };
    }

    /// <summary>
    /// Create profiles widget wrapped in a ScrollViewer.
    /// In the v2 framework, profiles uses a fixed row span with internal scrolling
    /// instead of auto-expanding rows.
    /// </summary>
    private static object CreateProfilesContent()
    {
        var profilesWidget = new ProfilesWidget();
        return new ScrollViewer
        {
            Content = profilesWidget,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }
}
