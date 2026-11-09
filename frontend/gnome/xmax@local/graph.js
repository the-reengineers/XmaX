// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
// graph.js — generic live line-graph widgets for the Quick Settings System
// Stats section. GNOME Shell has no charting toolkit, so this draws polylines
// with Cairo on an St.DrawingArea — the same technique Vitals / system-monitor
// use. Three building blocks, deliberately decoupled so any widget can reuse
// them with its own data:
//
//   GraphHistory  — timestamped ring buffer that accumulates samples whether
//                   or not anything is displayed (the graph is always "full"
//                   of recent history when the panel opens).
//   SystemGraph   — the drawing area: N series all drawn in one chart
//                   color (the system accent), told apart by line style —
//                   solid, dotted (round dots, not dashes), filled (area
//                   filled under the line at ~25% alpha) or dotted-filled
//                   (both); the visible window (right edge = newest
//                   sample) is configurable in seconds.
//   GraphBox     — a plain St box hosting title + legend + SystemGraph, with
//                   the chart unit drawn in parentheses after the title
//                   ("Usage (%)") — the x-axis is always time, so it gets no
//                   label. Attached into the System Stats grid (statsGrid.js)
//                   like any other tile, usually spanning the full row.

import Cairo from 'resource:///org/gnome/gjs/modules/esm/cairo.js';
import Clutter from 'gi://Clutter';
import GObject from 'gi://GObject';
import St from 'gi://St';

import {LEGEND_SYMBOLS} from './chartSeries.js';

/**
 * History windows offered in the preferences (seconds). The buffer capacity
 * must cover the longest window at the metrics cadence (180 s / 2 s = 90).
 * @type {number[]}
 */
export const GRAPH_WINDOWS = [30, 60, 120, 180];

/** Buffer capacity for the longest window (3 min at ~2 s cadence, +headroom). */
const HISTORY_CAPACITY = 128;

/** Translucent alpha used when filling under a series line. */
const FILL_ALPHA = 0.25;

/** Line width in px; also the dot diameter for dotted lines. */
const LINE_WIDTH = 2;

/**
 * Dash pattern for dotted lines: a ~0-length dash with round line caps
 * renders as a dot of the line width, followed by a gap — dots, not dashes.
 */
const DOT_DASH = [0.1, 5];

/**
 * Ring buffer of timestamped samples. `push()` is called by whoever owns the
 * data stream (e.g. quickSettings on every MetricsChanged); `series()` is
 * called at draw time. Not a GObject — purely passive storage.
 */
export class GraphHistory {
    /**
     * @param {number} [capacity] maximum number of samples kept
     */
    constructor(capacity = HISTORY_CAPACITY) {
        this._samples = []; // [{t: ms epoch, values: {seriesId: number|null}}]
        this._capacity = capacity;
    }

    /**
     * Record one sample. `values` maps series ids to numbers; absent/null
     * entries are skipped for that series at draw time.
     *
     * @param {number} t sample timestamp (ms since epoch, e.g. Date.now())
     * @param {Object} values {seriesId: number|null}
     */
    push(t, values) {
        this._samples.push({t, values});
        if (this._samples.length > this._capacity)
            this._samples.shift();
    }

    /**
     * All recorded points for a series, oldest first, gaps skipped.
     *
     * @param {string} id series id
     * @returns {{t: number, v: number}[]}
     */
    series(id) {
        const points = [];
        for (const sample of this._samples) {
            const v = sample.values[id];
            if (typeof v === 'number')
                points.push({t: sample.t, v});
        }
        return points;
    }

    /** @returns {number} number of samples currently kept */
    get length() {
        return this._samples.length;
    }
}

/**
 * The graph itself: an St.DrawingArea that draws one polyline per series.
 *
 * A series is `{id, label, style?, max?}`:
 *   - `style` — 'solid' (plain line), 'dotted' (round dots, not dashes),
 *               'filled' (area filled under the line at FILL_ALPHA) or
 *               'dotted-filled' (dots plus the area fill)
 *   - `max`   — value that maps to the top edge (default 100, i.e. %);
 *               omit it for a chart-wide dynamic axis (memory GB, power W)
 *
 * Every line draws in the single chart color (the system accent color,
 * '#rrggbb'). The x-axis is always time: the newest sample sits on the
 * right edge and `windowSeconds` of history is visible; points older
 * than the window are clipped at the left edge, so every line spans
 * the full drawing width. No x-axis labels are drawn.
 */
export const SystemGraph = GObject.registerClass(
class SystemGraph extends St.DrawingArea {
    /**
     * @param {Object} opts
     * @param {Array} opts.series series definitions (see above)
     * @param {string} [opts.color] line color, '#rrggbb' — the system
     * accent color, resolved by the caller (quickSettings._accentHex);
     * plain white as an inert fallback if omitted
     * @param {GraphHistory} opts.history data source
     * @param {number} [opts.windowSeconds] visible time span (default 180)
     * @param {number} [opts.height] graph height in px (default 56)
     */
    _init({series, history, color = '#ffffff', windowSeconds = 180, height = 56}) {
        super._init({
            style: `height: ${height}px;`,
            x_expand: true,
            reactive: false,
            track_hover: false,
            can_focus: false,
        });
        this._series = series;
        this._rgb = _parseColor(color);
        this._history = history;
        this._windowSeconds = windowSeconds;

        // GNOME 50's repaint signal carries no arguments — the Cairo
        // context is fetched with get_context() and must be $dispose()d
        // before returning (see st-drawing-area.c).
        this.connect('repaint', () => this._draw());
    }

    /** @returns {number} visible time span in seconds */
    get windowSeconds() {
        return this._windowSeconds;
    }

    /** @param {number} sec visible time span in seconds */
    set windowSeconds(sec) {
        this._windowSeconds = sec;
        this.queue_repaint();
    }

    /**
     * Cairo repaint: one polyline per series, newest sample at the right
     * edge. The context from get_context() must be disposed on every exit
     * path, hence the try/finally. Coordinates come from get_surface_size()
     * (device pixels — good enough at scale factor 1; HiDPI just crisper,
     * thinner lines).
     *
     * Series without a fixed max (memory GB, power W) share a chart-wide
     * dynamic axis: the highest recorded value rounded up to a nice number.
     * Clock series keep their own max so CPU and GPU both use the full
     * vertical range.
     */
    _draw() {
        const cr = this.get_context();
        try {
            const [width, height] = this.get_surface_size();
            if (width < 2 || height < 2)
                return;

            const windowMs = this._windowSeconds * 1000;

            // Right edge = newest sample across all series (not wall-clock
            // now), so the graph doesn't slide sideways between repaints.
            let newest = 0;
            for (const s of this._series) {
                const pts = this._history.series(s.id);
                if (pts.length && pts[pts.length - 1].t > newest)
                    newest = pts[pts.length - 1].t;
            }
            if (!newest)
                return;

            // Chart-wide dynamic max for max-less series (computed first so
            // every such series shares one axis).
            let dynValue = 0;
            const needsDynamic = this._series.some(
                s => s.max === undefined || s.max === null);
            if (needsDynamic) {
                for (const s of this._series) {
                    if (s.max !== undefined && s.max !== null)
                        continue;
                    for (const p of this._history.series(s.id)
                        .filter(p => newest - p.t <= windowMs))
                        dynValue = Math.max(dynValue, p.v);
                }
            }
            const dynamicMax = needsDynamic ? _niceMax(dynValue) : null;

            const xOf = t => width * (1 - (newest - t) / windowMs);
            const yOf = (v, max) =>
                height * (1 - Math.min(Math.max(v / max, 0), 1));

            const [r, g, b] = this._rgb;

            // Every recorded point is drawn, not just the in-window ones:
            // older points land at negative x and are clipped by Cairo at
            // the drawing area's left edge, so the line spans the full
            // width instead of starting one sample short of the left edge
            // (the right edge is always exact — it is pinned to the newest
            // sample). The dynamic axis above still only considers
            // in-window values.
            for (const s of this._series) {
                const pts = this._history.series(s.id);
                if (pts.length < 2)
                    continue;

                const max = s.max ?? dynamicMax ?? 100;

                if (s.style === 'filled' || s.style === 'dotted-filled') {
                    cr.moveTo(xOf(pts[0].t), height);
                    for (const p of pts)
                        cr.lineTo(xOf(p.t), yOf(p.v, max));
                    cr.lineTo(xOf(pts[pts.length - 1].t), height);
                    cr.closePath();
                    cr.setSourceRGBA(r, g, b, FILL_ALPHA);
                    cr.fill();
                }

                cr.setLineWidth(LINE_WIDTH);
                cr.setLineCap(Cairo.LineCap.ROUND);
                if (s.style === 'dotted' || s.style === 'dotted-filled') {
                    // A ~0-length dash with round caps renders as one dot
                    // per dash — dots, not dashes.
                    cr.setDash(DOT_DASH, 0);
                } else {
                    cr.setDash([], 0);
                }

                cr.moveTo(xOf(pts[0].t), yOf(pts[0].v, max));
                for (const p of pts.slice(1))
                    cr.lineTo(xOf(p.t), yOf(p.v, max));
                cr.setSourceRGB(r, g, b);
                cr.stroke();
                cr.setDash([], 0);
            }
        } finally {
            cr.$dispose();
        }
    }
});

/**
 * A graph host box: title (with the unit in parentheses after it) and a
 * style-keyed legend on a header line, the graph below. A plain St widget,
 * not a menu item — the Quick Settings System Stats grid attaches it into
 * a grid cell like any other tile (usually spanning the full row).
 * `this.graph` is the SystemGraph (for queue_repaint / windowSeconds).
 */
export const GraphBox = GObject.registerClass(
class GraphBox extends St.BoxLayout {
    /**
     * @param {Object} opts
     * @param {string} opts.title chart title (already localised)
     * @param {string} [opts.unit] unit drawn in parentheses after the title
     * @param {Array} opts.series series definitions (see SystemGraph)
     * @param {string} [opts.color] line color for every series, '#rrggbb'
     * (the system accent color)
     * @param {GraphHistory} opts.history data source (owned by the caller,
     * survives this box's destruction)
     * @param {number} [opts.windowSeconds] visible time span in seconds
     * @param {number} [opts.height] graph height in px
     */
    _init({title, unit = '', series, color, history, windowSeconds, height}) {
        super._init({
            orientation: Clutter.Orientation.VERTICAL,
            x_expand: true,
            // 12px horizontal padding matches the stat tiles' inner padding
            // (statsGrid.TILE_STYLE) so titles line up in the same column.
            style: 'spacing: 4px; padding: 10px 12px 6px 12px;',
        });

        // Header: "Usage (%)" left, style-keyed legend right — the symbol
        // mirrors how each line draws (filled / dotted / solid outline).
        const header = new St.BoxLayout({
            orientation: Clutter.Orientation.HORIZONTAL,
            style: 'spacing: 12px;',
        });
        const titleText = unit ? `${title} (${unit})` : title;
        header.add_child(new St.Label({
            text: titleText,
            y_align: Clutter.ActorAlign.CENTER,
        }));
        header.add_child(new St.Bin({x_expand: true}));
        for (const s of series) {
            header.add_child(new St.Label({
                text: `${LEGEND_SYMBOLS[s.style] ?? '●'} ${s.label}`,
                y_align: Clutter.ActorAlign.CENTER,
            }));
        }
        this.add_child(header);

        this.graph = new SystemGraph({
            series,
            history,
            color,
            windowSeconds,
            height,
        });
        this.add_child(this.graph);
    }
});

/**
 * Parse a series color into Cairo-friendly normalized RGB.
 * Accepts '#rrggbb' or [r, g, b] (0-255). Falls back to a neutral grey.
 *
 * @param {string|number[]} color
 * @returns {number[]} [r, g, b] with components 0-1
 */
function _parseColor(color) {
    if (Array.isArray(color) && color.length === 3)
        return [color[0] / 255, color[1] / 255, color[2] / 255];
    const m = /^#?([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i
        .exec(String(color));
    if (m)
        return [
            parseInt(m[1], 16) / 255,
            parseInt(m[2], 16) / 255,
            parseInt(m[3], 16) / 255,
        ];
    return [0.7, 0.7, 0.7];
}

/**
 * Round a dynamic axis maximum up to a "nice" 1/2/5×10ⁿ step, so the
 * memory/power charts rescale in stable jumps instead of continuously.
 *
 * @param {number} v highest recorded value
 * @returns {number} at least 1
 */
function _niceMax(v) {
    if (!(v > 0))
        return 1;
    const base = Math.pow(10, Math.floor(Math.log10(v)));
    for (const m of [1, 2, 5, 10]) {
        if (v <= m * base)
            return m * base;
    }
    return 10 * base;
}

