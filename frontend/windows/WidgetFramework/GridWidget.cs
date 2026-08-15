namespace XmaX.WidgetFramework;

/// <summary>
/// Minimal widget model for the v2 widget framework.
/// Defines a widget's identity, size, and content for grid layout.
/// </summary>
public class GridWidget
{
    /// <summary>Unique widget identifier.</summary>
    public string Id { get; }

    /// <summary>Number of columns this widget spans (mutable for resize).</summary>
    public int ColumnSpan { get; set; }

    /// <summary>Number of rows this widget spans (mutable for resize).</summary>
    public int RowSpan { get; set; }

    /// <summary>
    /// If true, the widget always spans the full row width (ColumnSpan is forced to grid column count).
    /// </summary>
    public bool AlwaysFillRow { get; }

    /// <summary>The UI element to display. Set by the page/host.</summary>
    public object Content { get; set; } = null!;

    public GridWidget(string id, int columnSpan, int rowSpan, bool alwaysFillRow = false)
    {
        Id = id;
        ColumnSpan = Math.Max(1, columnSpan);
        RowSpan = Math.Max(1, rowSpan);
        AlwaysFillRow = alwaysFillRow;
    }

    /// <summary>
    /// Get the effective column span for a given grid width.
    /// AlwaysFillRow widgets span all columns; others are clamped.
    /// </summary>
    public int GetEffectiveColumnSpan(int gridColumns)
    {
        if (AlwaysFillRow) return gridColumns;
        return Math.Min(ColumnSpan, gridColumns);
    }
}
