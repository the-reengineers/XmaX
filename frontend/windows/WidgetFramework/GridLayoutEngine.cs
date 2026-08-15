namespace XmaX.WidgetFramework;

/// <summary>
/// Pure grid layout engine. Computes widget positions using first-fit row-major packing.
/// No UI dependencies — all inputs and outputs are plain data.
/// </summary>
public static class GridLayoutEngine
{
    /// <summary>
    /// Pack widgets into a grid using first-fit row-major placement.
    /// Widgets are placed in list order, scanning left-to-right, top-to-bottom.
    /// </summary>
    public static List<WidgetPosition> Pack(IReadOnlyList<GridWidget> widgets, int columns)
    {
        var positions = new List<WidgetPosition>(widgets.Count);
        var occupied = new HashSet<(int row, int col)>();

        foreach (var widget in widgets)
        {
            var colSpan = widget.GetEffectiveColumnSpan(columns);
            var rowSpan = widget.RowSpan;

            for (int row = 0; row < 1000; row++)
            {
                for (int col = 0; col <= columns - colSpan; col++)
                {
                    if (AllCellsFree(occupied, row, col, rowSpan, colSpan))
                    {
                        positions.Add(new WidgetPosition(widget.Id, row, col, colSpan, rowSpan));
                        MarkOccupied(occupied, row, col, rowSpan, colSpan);
                        goto NextWidget;
                    }
                }
            }

            NextWidget:;
        }

        return positions;
    }

    /// <summary>
    /// Determine where to insert a dragged widget based on its center position in grid coordinates.
    ///
    /// Algorithm:
    /// 1. If the cursor center is still within the dragged widget's original bounding box,
    ///    return the original index (no reflow — small drag within own territory).
    /// 2. Otherwise, count how many remaining widgets occupy visual positions before the cursor,
    ///    plus how many on the same row start before the cursor's column.
    /// 3. Validate by re-packing: if the dragged widget doesn't land on the cursor's row,
    ///    adjust the index ±1 to get it closer.
    /// </summary>
    /// <param name="remaining">Widgets in logical order, excluding the dragged widget.</param>
    /// <param name="dragged">The widget being dragged.</param>
    /// <param name="draggedCenterRow">Dragged widget center row in grid coords.</param>
    /// <param name="draggedCenterCol">Dragged widget center column in grid coords.</param>
    /// <param name="columns">Grid column count.</param>
    /// <param name="originalPosition">The widget's position before the drag started (null to skip check).</param>
    /// <param name="originalIndex">The widget's list index before the drag started.</param>
    /// <returns>Index in the remaining list where the dragged widget should be inserted.</returns>
    public static int ComputeInsertionIndex(
        IReadOnlyList<GridWidget> remaining,
        GridWidget dragged,
        double draggedCenterRow,
        double draggedCenterCol,
        int columns,
        WidgetPosition? originalPosition = null,
        int originalIndex = -1)
    {
        if (remaining.Count == 0) return 0;

        // If cursor center is still within the original bounding box, no reflow
        if (originalPosition != null && originalIndex >= 0)
        {
            var inOriginalRow = draggedCenterRow >= originalPosition.Row
                             && draggedCenterRow < originalPosition.Row + originalPosition.RowSpan;
            var inOriginalCol = draggedCenterCol >= originalPosition.Column
                             && draggedCenterCol < originalPosition.Column + originalPosition.ColumnSpan;

            if (inOriginalRow && inOriginalCol)
            {
                return originalIndex;
            }
        }

        // Pack remaining widgets to get their visual positions
        var packed = Pack(remaining, columns);

        // Brute-force: try each possible insertion index and pick the one
        // that places the dragged widget closest to the cursor position,
        // with a directional bonus that favors the drag direction.
        // With small widget counts (typically <20), this is very fast.
        int bestIndex = originalIndex >= 0 ? Math.Clamp(originalIndex, 0, remaining.Count) : 0;
        double bestScore = double.MaxValue;

        // Compute drag direction from original center to current cursor center.
        // The bonus kicks in once the cursor center has clearly left the original position,
        // favoring reflow in the drag direction over snapping back.
        double dirRow = 0, dirCol = 0;
        if (originalPosition != null)
        {
            var origCenterRow = originalPosition.Row + originalPosition.RowSpan / 2.0;
            var origCenterCol = originalPosition.Column + originalPosition.ColumnSpan / 2.0;
            dirRow = draggedCenterRow - origCenterRow;
            dirCol = draggedCenterCol - origCenterCol;
        }

        for (int i = 0; i <= remaining.Count; i++)
        {
            var testPositions = PackWithInserted(remaining, dragged, i, columns);
            var dragPos = testPositions.Find(p => p.Id == dragged.Id);
            if (dragPos == null) continue;

            var centerRow = dragPos.Row + dragPos.RowSpan / 2.0;
            var centerCol = dragPos.Column + dragPos.ColumnSpan / 2.0;

            // Primary: minimize Manhattan distance from widget center to cursor
            var distance = Math.Abs(centerRow - draggedCenterRow) + Math.Abs(centerCol - draggedCenterCol);

            // Directional bonus: subtract a small amount when the candidate position
            // is in the same direction as the drag. This breaks ties in favor of
            // moving forward (e.g., dragging right swaps with the next widget
            // instead of snapping back to the original position).
            double bonus = 0;
            if (originalPosition != null)
            {
                var deltaRow = centerRow - (originalPosition.Row + originalPosition.RowSpan / 2.0);
                var deltaCol = centerCol - (originalPosition.Column + originalPosition.ColumnSpan / 2.0);

                if ((dirCol > 0.3 && deltaCol > 0) || (dirCol < -0.3 && deltaCol < 0)
                    || (dirRow > 0.3 && deltaRow > 0) || (dirRow < -0.3 && deltaRow < 0))
                {
                    bonus = 0.2;
                }
            }

            // Tiebreaker: prefer index closest to original (less list disruption)
            var indexPenalty = originalIndex >= 0 ? Math.Abs(i - originalIndex) * 0.01 : 0;

            var score = distance - bonus + indexPenalty;
            if (score < bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    /// <summary>
    /// Build a full ordered list with the dragged widget inserted at the given index,
    /// then pack all widgets.
    /// </summary>
    public static List<WidgetPosition> PackWithInserted(
        IReadOnlyList<GridWidget> remaining,
        GridWidget dragged,
        int insertionIndex,
        int columns)
    {
        var combined = new List<GridWidget>(remaining.Count + 1);
        int clampedIndex = Math.Clamp(insertionIndex, 0, remaining.Count);

        for (int i = 0; i < clampedIndex; i++)
            combined.Add(remaining[i]);

        combined.Add(dragged);

        for (int i = clampedIndex; i < remaining.Count; i++)
            combined.Add(remaining[i]);

        return Pack(combined, columns);
    }

    // ===== Private helpers =====

    private static bool AllCellsFree(
        HashSet<(int row, int col)> occupied,
        int startRow, int startCol,
        int rowSpan, int colSpan)
    {
        for (int r = 0; r < rowSpan; r++)
            for (int c = 0; c < colSpan; c++)
                if (occupied.Contains((startRow + r, startCol + c)))
                    return false;
        return true;
    }

    private static void MarkOccupied(
        HashSet<(int row, int col)> occupied,
        int startRow, int startCol,
        int rowSpan, int colSpan)
    {
        for (int r = 0; r < rowSpan; r++)
            for (int c = 0; c < colSpan; c++)
                occupied.Add((startRow + r, startCol + c));
    }
}
