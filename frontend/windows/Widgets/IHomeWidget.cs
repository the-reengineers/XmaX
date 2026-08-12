namespace XmaX.Widgets;

/// <summary>
/// Interface for home page widgets.
/// Each widget has a unique ID, configuration, and provides a UI control for display.
/// </summary>
public interface IHomeWidget
{
    /// <summary>Unique widget identifier (e.g., "cpu", "profiles", "adaptive").</summary>
    string WidgetId { get; }

    /// <summary>Widget configuration (column bounds, background style).</summary>
    WidgetConfig Config { get; }

    /// <summary>The UI control to display. Typically a UserControl.</summary>
    object Control { get; }
}

/// <summary>
/// Configuration for a home page widget.
/// </summary>
/// <param name="MinColumns">Minimum column span (1-4).</param>
/// <param name="MaxColumns">Maximum column span (1-4, must be >= MinColumns).</param>
/// <param name="IsInteractiveCard">
/// If true, widget has card background (for clickable/toggleable widgets).
/// If false, widget has transparent background (for widgets containing controls).
/// </param>
/// <param name="AlwaysFillRow">
/// If true, widget always spans the full row width (ignores MaxColumns).
/// If false, widget spans up to MaxColumns (clamped to grid column count).
/// </param>
public sealed class WidgetConfig
{
    public int MinColumns { get; }
    public int MaxColumns { get; }
    public bool IsInteractiveCard { get; }
    public bool AlwaysFillRow { get; }

    public WidgetConfig(int minColumns, int maxColumns, bool isInteractiveCard, bool alwaysFillRow = false)
    {
        MinColumns = System.Math.Clamp(minColumns, 1, 4);
        MaxColumns = System.Math.Clamp(maxColumns, MinColumns, 4);
        IsInteractiveCard = isInteractiveCard;
        AlwaysFillRow = alwaysFillRow;
    }

    /// <summary>Preset for 1x1 transparent tile (e.g., metric tiles).</summary>
    public static WidgetConfig TransparentTile => new(1, 1, false);

    /// <summary>Preset for 1x1 card tile (e.g., charge limit).</summary>
    public static WidgetConfig CardTile => new(1, 1, true);

    /// <summary>Preset for flexible transparent container (e.g., profiles).</summary>
    public static WidgetConfig FlexibleTransparent(int minColumns = 1, int maxColumns = 4, bool alwaysFillRow = false)
        => new(minColumns, maxColumns, false, alwaysFillRow);

    /// <summary>Preset for fixed-width transparent container (e.g., adaptive, power).</summary>
    public static WidgetConfig FixedTransparent(int minColumns, int maxColumns, bool alwaysFillRow = false)
        => new(minColumns, maxColumns, false, alwaysFillRow);
}
