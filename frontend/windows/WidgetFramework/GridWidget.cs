namespace XmaX.WidgetFramework;

/// <summary>
/// Minimal widget model for the v2 widget framework.
/// Defines a widget's identity, size, and content for grid layout.
/// </summary>
public class GridWidget
{
    /// <summary>Unique widget identifier.</summary>
    public string Id { get; }

    /// <summary>Minimum number of columns this widget can span.</summary>
    public int MinColumnSpan { get; set; } = 1;

    /// <summary>Maximum number of columns this widget can span.</summary>
    public int MaxColumnSpan { get; set; } = 1;

    /// <summary>Minimum number of rows this widget can span.</summary>
    public int MinRowSpan { get; set; } = 1;

    /// <summary>Maximum number of rows this widget can span.</summary>
    public int MaxRowSpan { get; set; } = 1;

    private int _columnSpan = 1;
    private int _rowSpan = 1;

    /// <summary>Number of columns this widget spans (clamped to min/max).</summary>
    public int ColumnSpan
    {
        get => _columnSpan;
        set => _columnSpan = Math.Clamp(value, MinColumnSpan, MaxColumnSpan);
    }

    /// <summary>Number of rows this widget spans (clamped to min/max).</summary>
    public int RowSpan
    {
        get => _rowSpan;
        set => _rowSpan = Math.Clamp(value, MinRowSpan, MaxRowSpan);
    }

    /// <summary>
    /// If true, the widget always spans the full row width (ColumnSpan is forced to grid column count).
    /// All column span properties are ignored when this is true.
    /// </summary>
    public bool AlwaysFillRow { get; }

    /// <summary>
    /// Whether this widget can be resized (has different min/max for width or height).
    /// </summary>
    public bool IsResizable => MinColumnSpan != MaxColumnSpan || MinRowSpan != MaxRowSpan;

    /// <summary>The UI element to display. Set by the page/host.</summary>
    public object Content { get; set; } = null!;

    public GridWidget(string id, int columnSpan = 1, int rowSpan = 1, bool alwaysFillRow = false)
    {
        Id = id;
        AlwaysFillRow = alwaysFillRow;
        _columnSpan = columnSpan;
        _rowSpan = rowSpan;
    }

    /// <summary>
    /// Get the effective column span for a given grid width.
    /// AlwaysFillRow widgets span all columns; others are clamped to min/max and grid width.
    /// </summary>
    public int GetEffectiveColumnSpan(int gridColumns)
    {
        if (AlwaysFillRow) return gridColumns;
        return Math.Clamp(ColumnSpan, MinColumnSpan, Math.Min(MaxColumnSpan, gridColumns));
    }
}
