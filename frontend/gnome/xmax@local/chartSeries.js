// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
// chartSeries.js — registry of chart types and data series for the usage
// graph. Pure data and functions, no imports: shared by the shell
// (quickSettings history sampling, statsGrid rendering) and the prefs
// (chart/series editor). Labels are plain English; callers translate them
// with gettext at render time.
//
// One chart = one unit (the graph draws a single y-axis). Every line draws
// in the system accent color (see graph.js); series are told apart by line
// style: 'solid', 'dotted' (round dots, not dashes), 'filled' (area filled
// under the line) or 'dotted-filled' (dots plus the area fill, e.g. VRAM
// next to RAM). The legend mirrors the style with the matching symbol from
// LEGEND_SYMBOLS.
//
// Series without a fixed `max` (memory GB, power W) share a chart-wide
// dynamic axis computed at draw time (see graph.js). Clock series each
// carry their own max so CPU (~5.5 GHz) and GPU (~3 GHz) lines both use
// the full vertical range, normalized to their own ceiling.

/**
 * Chart type: {title, unit, defaultSeries}.
 *
 * @typedef {Object} ChartDef
 * @property {string} title untranslated chart title
 * @property {string} unit drawn after the title in parentheses
 * @property {string[]} defaultSeries series ids enabled by default
 */

/**
 * Series: {chart, label, style, max?, value(metrics) -> number|null}.
 * value() returns null when the metric is unavailable in a payload; such
 * samples are skipped at draw time.
 *
 * @typedef {Object} SeriesDef
 * @property {string} chart owning chart id
 * @property {string} label untranslated legend label
 * @property {string} style 'solid' | 'dotted' | 'filled' |
 * 'dotted-filled' (see graph.js)
 * @property {number} [max] fixed axis ceiling; omit for a dynamic one
 * @property {function(Object): (number|null)} value
 */

/**
 * Legend symbol per line style, mirroring how the line is drawn: a filled
 * circle for filled lines, a dotted circle for dotted lines, an outline
 * circle for solid lines, and a vertically-filled circle for dotted lines
 * with an area fill.
 *
 * @type {Record<string, string>}
 */
export const LEGEND_SYMBOLS = {
    filled: '●',        // U+25CF black circle
    dotted: '◌',        // U+25CC dotted circle
    solid: '○',         // U+25CB white circle
    'dotted-filled': '◍', // U+25CD circle with vertical fill
};

/** @type {Record<string, ChartDef>} */
export const CHARTS = {
    usage: {
        title: 'Usage',
        unit: '%',
        defaultSeries: ['cpu_util', 'gpu_util', 'ram_util'],
    },
    temp: {
        title: 'Temperature',
        unit: '°C',
        defaultSeries: ['cpu_temp', 'gpu_temp'],
    },
    clock: {
        title: 'Clock',
        unit: 'MHz',
        defaultSeries: ['cpu_clock', 'gpu_clock'],
    },
    memory: {
        title: 'Memory',
        unit: 'GB',
        defaultSeries: ['ram_used', 'vram_used'],
    },
    power: {
        title: 'Power',
        unit: 'W',
        defaultSeries: ['cpu_power', 'gpu_power', 'sys_power'],
    },
};

/**
 * Line style per series: first component solid, second dotted, totals
 * filled — enough to tell same-colored lines apart in every chart. VRAM
 * adds an area fill under its dotted line, mirroring the filled RAM line
 * it sits next to in the usage and memory charts.
 */
export const SERIES = {
    // ---- Usage (%) ----
    cpu_util: {
        chart: 'usage', label: 'CPU', style: 'solid', max: 100,
        value: m => _num(m?.cpu?.util_pct),
    },
    gpu_util: {
        chart: 'usage', label: 'GPU', style: 'dotted', max: 100,
        value: m => _num(m?.gpu?.util_pct),
    },
    ram_util: {
        chart: 'usage', label: 'RAM', style: 'filled', max: 100,
        value: m => _num(m?.ram?.load_pct),
    },
    vram_util: {
        chart: 'usage', label: 'VRAM', style: 'dotted-filled', max: 100,
        value: m => {
            const used = _num(m?.gpu?.vram_used_bytes);
            const total = _num(m?.gpu?.vram_total_bytes);
            if (used === null || total === null || total <= 0)
                return null;
            return 100 * used / total;
        },
    },

    // ---- Temperature (°C) ----
    cpu_temp: {
        chart: 'temp', label: 'CPU', style: 'solid', max: 110,
        value: m => _num(m?.cpu?.temp_c),
    },
    gpu_temp: {
        chart: 'temp', label: 'GPU', style: 'dotted', max: 110,
        value: m => _num(m?.gpu?.temp_c),
    },

    // ---- Clock (MHz, per-series ceiling: CPU vs GPU maxes differ) ----
    cpu_clock: {
        chart: 'clock', label: 'CPU', style: 'solid', max: 5500,
        value: m => _num(m?.cpu?.clock_mhz),
    },
    gpu_clock: {
        chart: 'clock', label: 'GPU', style: 'dotted', max: 3000,
        value: m => _num(m?.gpu?.clock_mhz),
    },

    // ---- Memory (GB, dynamic axis) ----
    ram_used: {
        chart: 'memory', label: 'RAM', style: 'filled',
        value: m => _gb(m?.ram?.used_bytes),
    },
    vram_used: {
        chart: 'memory', label: 'VRAM', style: 'dotted-filled',
        value: m => _gb(m?.gpu?.vram_used_bytes),
    },

    // ---- Power (W, dynamic axis) ----
    cpu_power: {
        chart: 'power', label: 'CPU', style: 'solid',
        value: m => _num(m?.cpu?.package_watts),
    },
    gpu_power: {
        chart: 'power', label: 'GPU', style: 'dotted',
        value: m => _num(m?.gpu?.power_w),
    },
    // Total system power: the sum of the available component readings.
    sys_power: {
        chart: 'power', label: 'Total', style: 'filled',
        value: m => {
            const cpu = _num(m?.cpu?.package_watts);
            const gpu = _num(m?.gpu?.power_w);
            if (cpu === null && gpu === null)
                return null;
            return (cpu ?? 0) + (gpu ?? 0);
        },
    },
};

/** @returns {string[]} all chart ids, in registry order */
export function chartIds() {
    return Object.keys(CHARTS);
}

/**
 * @param {string} id chart id
 * @returns {ChartDef} the chart definition (usage for unknown ids)
 */
export function chartDef(id) {
    return CHARTS[id] ?? CHARTS.usage;
}

/**
 * All series ids belonging to a chart, in registry order.
 *
 * @param {string} chartId
 * @returns {string[]}
 */
export function seriesIdsFor(chartId) {
    return Object.keys(SERIES).filter(id => SERIES[id].chart === chartId);
}

/**
 * Sample every series from one metrics payload — the history push side.
 *
 * @param {Object} m parsed metrics payload
 * @returns {Record<string, number|null>} {seriesId: value}
 */
export function sampleSeries(m) {
    const out = {};
    for (const [id, series] of Object.entries(SERIES))
        out[id] = series.value(m);
    return out;
}

/**
 * The effective enabled series list for a chart: the selection filtered to
 * that chart's series, falling back to the chart defaults when nothing
 * valid remains (empty selection, or ids from a different chart).
 *
 * @param {string} chartId
 * @param {string[]} [selected] stored selection (GSettings graph-series)
 * @returns {string[]}
 */
export function effectiveSeries(chartId, selected) {
    const valid = seriesIdsFor(chartId);
    const filtered = (selected ?? []).filter(id => valid.includes(id));
    return filtered.length ? filtered : chartDef(chartId).defaultSeries;
}

/**
 * Render-ready series definitions for the graph widget. Line colors are
 * not part of the definitions — every line draws in the system accent
 * color, resolved by the caller (graph.js GraphBox `color` option).
 *
 * @param {string} chartId
 * @param {string[]} [selected] enabled series ids (GSettings graph-series)
 * @returns {{id: string, label: string, style: string, max?: number}[]}
 */
export function buildChartSeries(chartId, selected) {
    return effectiveSeries(chartId, selected).map(id => ({
        id,
        label: SERIES[id].label,
        style: SERIES[id].style,
        ...(SERIES[id].max !== undefined ? {max: SERIES[id].max} : {}),
    }));
}

/** @param {*} v @returns {number|null} a finite number, or null */
function _num(v) {
    return typeof v === 'number' && Number.isFinite(v) ? v : null;
}

/** Bytes → GB (one decimal). */
function _gb(bytes) {
    const n = _num(bytes);
    return n === null ? null : Math.round(n / 1e8) / 10;
}
