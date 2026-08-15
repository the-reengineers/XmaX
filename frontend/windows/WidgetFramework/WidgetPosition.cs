namespace XmaX.WidgetFramework;

/// <summary>
/// Computed position for a widget within the grid.
/// </summary>
/// <param name="Id">Widget ID (matches GridWidget.Id).</param>
/// <param name="Row">Row index (0-based).</param>
/// <param name="Column">Column index (0-based).</param>
/// <param name="ColumnSpan">Number of columns spanned.</param>
/// <param name="RowSpan">Number of rows spanned.</param>
public record WidgetPosition(string Id, int Row, int Column, int ColumnSpan, int RowSpan);
