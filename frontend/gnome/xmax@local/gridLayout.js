// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
// gridLayout.js — pure grid packing engine, shared by the GNOME Shell
// extension (Quick Settings System Stats section) and the extension
// preferences (Layout page preview). It is a direct port of the Windows
// implementation's GridLayoutEngine (frontend/windows/WidgetFramework/
// GridLayoutEngine.cs): widgets are an ORDERED list; positions are computed
// by first-fit row-major packing, so reordering the list is all a drag needs
// to do. No imports — runs in both the shell (St/Clutter) and prefs (GTK4)
// processes.

/**
 * Widget spec for packing: {id, col_span, row_span, always_fill_row?}.
 * always_fill_row forces col_span to the full grid width (the usage graph).
 *
 * @typedef {Object} PackWidget
 * @property {string} id
 * @property {number} [col_span]
 * @property {number} [row_span]
 * @property {boolean} [always_fill_row]
 */

/**
 * Computed cell placement: {id, row, column, col_span, row_span}.
 *
 * @typedef {Object} WidgetPosition
 * @property {string} id
 * @property {number} row 0-based
 * @property {number} column 0-based
 * @property {number} col_span
 * @property {number} row_span
 */

const MAX_ROWS = 1000;

/**
 * Pack widgets into a grid using first-fit row-major placement (same
 * algorithm as the Windows GridLayoutEngine.Pack). Widgets are placed in
 * list order, scanning left-to-right, top-to-bottom; each gets its clamped
 * col_span/row_span.
 *
 * @param {PackWidget[]} widgets ordered widget list
 * @param {number} columns grid column count (>= 1)
 * @returns {WidgetPosition[]} one position per widget, in input order
 */
export function packWidgets(widgets, columns) {
    const cols = Math.max(1, Math.floor(columns) || 1);
    const positions = [];
    /** @type {Set<string>} "row,col" keys */
    const occupied = new Set();

    for (const widget of widgets ?? []) {
        const colSpan = _effectiveColSpan(widget, cols);
        const rowSpan = Math.max(1, Math.floor(widget.row_span) || 1);

        let placed = false;
        for (let row = 0; row < MAX_ROWS && !placed; row++) {
            for (let col = 0; col <= cols - colSpan && !placed; col++) {
                if (!_allCellsFree(occupied, row, col, rowSpan, colSpan)) {
                    continue;
                }
                positions.push({
                    id: widget.id,
                    row,
                    column: col,
                    col_span: colSpan,
                    row_span: rowSpan,
                });
                _markOccupied(occupied, row, col, rowSpan, colSpan);
                placed = true;
            }
        }
    }

    return positions;
}

/**
 * Clamp a widget's column span into [1, columns]; always_fill_row spans
 * the full width.
 *
 * @param {PackWidget} widget
 * @param {number} columns
 * @returns {number}
 */
function _effectiveColSpan(widget, columns) {
    if (widget.always_fill_row)
        return columns;
    const span = Math.floor(widget.col_span) || 1;
    return Math.min(Math.max(span, 1), columns);
}

/**
 * @param {Set<string>} occupied
 * @param {number} startRow
 * @param {number} startCol
 * @param {number} rowSpan
 * @param {number} colSpan
 * @returns {boolean}
 */
function _allCellsFree(occupied, startRow, startCol, rowSpan, colSpan) {
    for (let r = 0; r < rowSpan; r++)
        for (let c = 0; c < colSpan; c++)
            if (occupied.has(`${startRow + r},${startCol + c}`))
                return false;
    return true;
}

/**
 * @param {Set<string>} occupied
 * @param {number} startRow
 * @param {number} startCol
 * @param {number} rowSpan
 * @param {number} colSpan
 */
function _markOccupied(occupied, startRow, startCol, rowSpan, colSpan) {
    for (let r = 0; r < rowSpan; r++)
        for (let c = 0; c < colSpan; c++)
            occupied.add(`${startRow + r},${startCol + c}`);
}
