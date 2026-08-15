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

    /// <summary>
    /// Optional title displayed at the top of the widget.
    /// Only applicable for widgets with AlwaysFillRow=true.
    /// Null means no title.
    /// </summary>
    string? Title { get; }

    /// <summary>
    /// Calculate the number of content rows this widget requires based on available columns.
    /// Used by HomePage layout to determine row spans.
    /// Does not include title row - layout adds title height separately if Title is not null.
    /// </summary>
    /// <param name="availableColumns">Number of columns available to this widget.</param>
    /// <returns>Number of content rows needed (minimum 1).</returns>
    int GetRequiredRows(int availableColumns);
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
/// <param name="Rows">Fixed number of rows this widget occupies (default 1).</param>
public sealed class WidgetConfig
{
    /// <summary>Standard height for widget titles in pixels (based on system font size).</summary>
    public const double TitleHeight = 24.0;

    public int MinColumns { get; }
    public int MaxColumns { get; }
    public bool IsInteractiveCard { get; }
    public bool AlwaysFillRow { get; }
    public int Rows { get; }

    public WidgetConfig(
        int minColumns,
        int maxColumns,
        bool isInteractiveCard,
        bool alwaysFillRow = false,
        int rows = 1)
    {
        MinColumns = System.Math.Clamp(minColumns, 1, 4);
        MaxColumns = System.Math.Clamp(maxColumns, MinColumns, 4);
        IsInteractiveCard = isInteractiveCard;
        AlwaysFillRow = alwaysFillRow;
        Rows = System.Math.Max(1, rows);
    }

    /// <summary>Preset for 1x1 transparent tile (e.g., metric tiles).</summary>
    public static WidgetConfig TransparentTile => new(1, 1, false);

    /// <summary>Preset for 1x1 card tile (e.g., charge limit).</summary>
    public static WidgetConfig CardTile => new(1, 1, true);

    /// <summary>Preset for flexible transparent container (e.g., profiles).</summary>
    public static WidgetConfig FlexibleTransparent(
        int minColumns = 1,
        int maxColumns = 4,
        bool alwaysFillRow = false,
        int rows = 1)
        => new(minColumns, maxColumns, false, alwaysFillRow, rows);

    /// <summary>Preset for fixed-width transparent container (e.g., adaptive, power).</summary>
    public static WidgetConfig FixedTransparent(
        int minColumns,
        int maxColumns,
        bool alwaysFillRow = false,
        int rows = 1)
        => new(minColumns, maxColumns, false, alwaysFillRow, rows);
}
