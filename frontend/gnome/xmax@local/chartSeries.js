// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
// chartSeries.js — registry of chart types and data series for the usage
// graph. Pure data and functions, no imports: shared by the shell
// (quickSettings history sampling, statsGrid rendering) and the prefs
// (chart/series editor). Labels are plain English; callers translate them
// with gettext at render time.
//
// One chart = one unit (the graph draws a single y-axis). Series are told
// apart by line style: 'solid', 'dotted' (round dots, not dashes), 'filled'
// (area filled under the line) or 'dotted-filled' (dots plus the area fill,
// e.g. VRAM next to RAM). The legend mirrors the style with the matching
// symbol from LEGEND_SYMBOLS.
//
// Colour lives in the `colorGroup` field on each series definition:
// 'default' gets the main colour, 'hot' gets the hotspot colour. The caller
// resolves these to actual '#rrggbb' hex values via buildChartSeries()'s
// `colorMap` parameter — graph.js never sees colour groups, only per-series
// `color` fields.
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
        colorGroup: 'edge',
        value: m => _num(m?.cpu?.temp_c),
    },
    gpu_temp: {
        chart: 'temp', label: 'GPU', style: 'dotted', max: 110,
        colorGroup: 'edge',
        value: m => _num(m?.gpu?.temp_c),
    },
    gpu_hotspot: {
        chart: 'temp', label: 'GPU HS', style: 'dotted', max: 110,
        colorGroup: 'hot',
        value: m => _num(m?.gpu?.temp_hotspot_c),
    },
    apu_hotspot: {
        chart: 'temp', label: 'APU HS', style: 'solid', max: 110,
        colorGroup: 'hot',
        value: m => _num(m?.apu?.temp_hotspot_c),
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
 * Render-ready series definitions for the graph widget. Each series gets a
 * `color` field resolved from `colorMap`: the series' `colorGroup` (or
 * 'default') is looked up in the map; if the map is a plain string it
 * applies to every series; if omitted every series gets '' (the graph
 * falls back to white).
 *
 * @param {string} chartId
 * @param {string[]} [selected] enabled series ids (GSettings graph-series)
 * @param {Record<string, string>|string} [colorMap] colour group → hex
 *   mapping, or a single hex string for every series
 * @returns {{id: string, label: string, style: string, color: string,
 *   max?: number}[]}
 */
export function buildChartSeries(chartId, selected, colorMap) {
    return effectiveSeries(chartId, selected).map(id => {
        const group = SERIES[id].colorGroup ?? 'default';
        const color = typeof colorMap === 'string'
            ? colorMap
            : (colorMap?.[group] ?? colorMap?.default ?? '');
        return {
            id,
            label: SERIES[id].label,
            style: SERIES[id].style,
            color,
            ...(SERIES[id].max !== undefined ? {max: SERIES[id].max} : {}),
        };
    });
}

/**
 * Rotate the hue of a '#rrggbb' colour by `deg` degrees (positive or
 * negative), returning a new '#rrggbb' string. Lightness and saturation
 * are preserved. No-op (returns the input) if the hex is empty or the
 * colour is achromatic (grey/white/black — hue is undefined).
 *
 * @param {string} hex colour in '#rrggbb' format
 * @param {number} deg hue rotation in degrees
 * @returns {string} rotated '#rrggbb' colour
 */
export function rotateHue(hex, deg) {
    const m = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(String(hex));
    if (!m)
        return hex;
    let r = parseInt(m[1], 16) / 255;
    let g = parseInt(m[2], 16) / 255;
    let b = parseInt(m[3], 16) / 255;
    const max = Math.max(r, g, b);
    const min = Math.min(r, g, b);
    const l = (max + min) / 2;
    if (max === min) {
        // achromatic — hue is undefined; rotate has no visible effect
        return hex;
    }
    const d = max - min;
    const s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
    let h;
    if (max === r)
        h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
    else if (max === g)
        h = ((b - r) / d + 2) / 6;
    else
        h = ((r - g) / d + 4) / 6;
    h = (h * 360 + deg) % 360;
    if (h < 0)
        h += 360;
    h /= 360;
    // HSL → RGB
    const hue2rgb = (p, q, t) => {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1 / 6) return p + (q - p) * 6 * t;
        if (t < 1 / 2) return q;
        if (t < 2 / 3) return p + (q - p) * (2 / 3 - t) * 6;
        return p;
    };
    const q = l < 0.5 ? l * (1 + s) : l + s - l * s;
    const p = 2 * l - q;
    const hexOut = v => Math.round(v * 255).toString(16).padStart(2, '0');
    return `#${hexOut(hue2rgb(p, q, h + 1 / 3))}${hexOut(hue2rgb(p, q, h))}${hexOut(hue2rgb(p, q, h - 1 / 3))}`;
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
