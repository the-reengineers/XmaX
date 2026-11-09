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
 * always_fill_row forces col_span to the full grid width. Widgets listed in
 * WIDGET_SIZE_LIMITS additionally get their spans clamped to their size
 * limits (see below) at pack time.
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

/**
 * Resizable-widget size limits, keyed by widget id — the GNOME counterpart
 * of the Windows GridWidget's Min/MaxColumnSpan / Min/MaxRowSpan
 * (frontend/windows/WidgetFramework/GridWidget.cs). Widgets without an
 * entry are fixed at 1×1.
 *
 * maxColSpan null means "as wide as the grid" (full row). Spans are clamped
 * at pack time (packWidgets) in BOTH the shell and the prefs preview, so a
 * stored out-of-range value (e.g. after the user shrinks the column count)
 * degrades gracefully instead of breaking the layout.
 *
 * The usage graph is resizable: width 2..full, height 2..4 rows, default
 * full × 3.
 *
 * @type {Record<string, {minColSpan: number, maxColSpan: number|null,
 *   minRowSpan: number, maxRowSpan: number, defaultColSpan: number,
 *   defaultRowSpan: number}>}
 */
export const WIDGET_SIZE_LIMITS = {
    graph: {
        minColSpan: 2,
        maxColSpan: null, // full row (limited only by the column count)
        minRowSpan: 2,
        maxRowSpan: 4,
        defaultColSpan: 3,
        defaultRowSpan: 3,
    },
};

/**
 * Size limits for a widget id, or null when the widget is fixed-size.
 *
 * @param {string} id
 * @returns {Object|null}
 */
export function sizeLimits(id) {
    return WIDGET_SIZE_LIMITS[id] ?? null;
}

/**
 * Default spans for a widget id — the limits' defaults for resizable
 * widgets, 1×1 otherwise. Used when a widget first appears in the layout
 * (statsConfig defaults, prefs "show" action).
 *
 * @param {string} id
 * @returns {{col_span: number, row_span: number}}
 */
export function defaultSpans(id) {
    const limits = WIDGET_SIZE_LIMITS[id];
    if (limits)
        return {col_span: limits.defaultColSpan, row_span: limits.defaultRowSpan};
    return {col_span: 1, row_span: 1};
}

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
        const rowSpan = _effectiveRowSpan(widget);

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
 * Clamp a widget's column span: always_fill_row spans the full width;
 * size-limited widgets are clamped into [minColSpan, min(maxColSpan,
 * columns)] (a grid narrower than the minimum still fills it); anything
 * else into [1, columns].
 *
 * @param {PackWidget} widget
 * @param {number} columns
 * @returns {number}
 */
function _effectiveColSpan(widget, columns) {
    if (widget.always_fill_row)
        return columns;

    const limits = WIDGET_SIZE_LIMITS[widget.id];
    if (limits) {
        const span = Math.floor(widget.col_span) || limits.defaultColSpan;
        const max = limits.maxColSpan === null
            ? Infinity
            : limits.maxColSpan;
        const min = Math.min(limits.minColSpan, columns);
        return Math.min(Math.max(span, min), Math.min(max, columns));
    }

    const span = Math.floor(widget.col_span) || 1;
    return Math.min(Math.max(span, 1), columns);
}

/**
 * Clamp a widget's row span into its size limits when it has any; [1, ∞)
 * otherwise (a 1×1 stat tile kept its plain span).
 *
 * @param {PackWidget} widget
 * @returns {number}
 */
function _effectiveRowSpan(widget) {
    const limits = WIDGET_SIZE_LIMITS[widget.id];
    if (limits) {
        const span = Math.floor(widget.row_span) || limits.defaultRowSpan;
        return Math.min(Math.max(span, limits.minRowSpan), limits.maxRowSpan);
    }
    return Math.max(1, Math.floor(widget.row_span) || 1);
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
