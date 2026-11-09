// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
// statsConfig.js — reads/writes the shared config.json home_layout that
// drives the Quick Settings System Stats grid: which stat widgets are
// visible, in what order, and with which col_span/row_span. The same file
// also serves the Windows home/dashboard tile page; ids unknown to Windows
// (fan, charge, graph) are skipped by its widget factory, so the layout
// round-trips across platforms.

import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

// All widget IDs supported by the System Stats section, in default display
// order (used when config.json is missing or unreadable). 'graph' is the
// CPU/GPU/RAM usage graph row; like fan/charge it is not a Windows home tile
// id, so the Windows frontend ignores it when reading the shared layout.
const STAT_IDS = ['cpu', 'gpu', 'ram', 'fan', 'power', 'charge', 'graph'];
const DEFAULT_ORDER = [...STAT_IDS];

/** @param {string[]} a @param {string[]} b @returns {boolean} */
export function arraysEqual(a, b) {
    if (a.length !== b.length)
        return false;
    for (let i = 0; i < a.length; i++) {
        if (a[i] !== b[i])
            return false;
    }
    return true;
}

/**
 * Returns the absolute path to the shared config file.
 * @returns {string}
 */
export function getConfigPath() {
    return GLib.build_filenamev([GLib.get_user_config_dir(), 'xmax', 'config.json']);
}

/**
 * Returns the ordered list of stat widget IDs to show, as determined by
 * config.json's home_layout (see getStatLayout). Convenience wrapper for
 * consumers that only care about identity/order (indicator.js).
 *
 * When config.json is absent or unreadable the full default order is returned.
 *
 * @returns {string[]} e.g. ['cpu', 'gpu', 'ram', 'fan', 'power', 'charge']
 */
export function getStatWidgets() {
    const ids = getStatLayout().map(w => w.id);
    return ids.length ? ids : DEFAULT_ORDER;
}

/**
 * Full layout of the visible stat widgets: the ordered list with each
 * widget's col_span/row_span taken from config.json's home_layout.widgets
 * (1×1 for entries without spans). This is the same data the Windows home
 * grid uses, so spans set on one platform apply on the other (column count
 * is capped separately — see getStatColumns).
 *
 * Unlike getStatWidgets, an explicitly empty layout is returned as-is (the
 * System Stats section then hides itself); only a missing/unreadable config
 * falls back to the default layout.
 *
 * @returns {{id: string, col_span: number, row_span: number}[]}
 */
export function getStatLayout() {
    const cfg = _readConfig();
    if (!cfg)
        return DEFAULT_ORDER.map(id => ({id, col_span: 1, row_span: 1}));

    const widgets = cfg?.home_layout?.widgets || [];
    const hidden = new Set(cfg?.home_layout?.hidden_widgets || []);

    const result = [];
    const seen = new Set();
    for (const w of widgets) {
        if (STAT_IDS.includes(w.id) && !hidden.has(w.id)) {
            result.push({
                id: w.id,
                col_span: Math.max(1, Math.floor(w.col_span) || 1),
                row_span: Math.max(1, Math.floor(w.row_span) || 1),
            });
            seen.add(w.id);
        }
    }

    // Stats not present in the widgets array go last, in default order.
    for (const id of STAT_IDS) {
        if (!seen.has(id) && !hidden.has(id))
            result.push({id, col_span: 1, row_span: 1});
    }

    return result;
}

/**
 * IDs of stat widgets the user has hidden (available in the prefs Layout
 * page palette). A missing config means nothing is hidden.
 *
 * @returns {string[]}
 */
export function getHiddenStats() {
    const cfg = _readConfig();
    if (!cfg)
        return [];
    return (cfg?.home_layout?.hidden_widgets || [])
        .filter(id => STAT_IDS.includes(id));
}

/**
 * Column count for the Quick Settings System Stats grid: the shared
 * home_layout.columns, clamped to 1–3 (the QS popup is narrower than the
 * Windows home window; three tiles ≈ 120 px each is the comfortable max).
 *
 * @returns {number} 1, 2 or 3
 */
export function getStatColumns() {
    const cfg = _readConfig();
    const columns = Math.floor(cfg?.home_layout?.columns);
    if (!cfg || !columns || columns < 1)
        return 3;
    return Math.min(columns, 3);
}

/**
 * Writes the full stat layout — order, spans and visibility — into
 * config.json's home_layout.widgets and home_layout.hidden_widgets, the
 * same fields the Windows home grid reads/writes. Non-stat widgets are
 * preserved untouched; hidden lists are merged for non-stat ids.
 *
 * Creates the config file (with defaults) if it doesn't exist.
 *
 * @param {{id: string, col_span: number, row_span: number}[]} visible
 * ordered list of visible stat widgets with spans (may be empty)
 * @param {string[]} hidden ids of hidden stat widgets
 */
export function setStatLayout(visible, hidden) {
    const path = getConfigPath();
    const parent = Gio.File.new_for_path(path).get_parent();
    if (!parent.query_exists(null))
        parent.make_directory_with_parents(null);

    const cfg = _readConfig() || _defaultConfig();
    if (!cfg.home_layout)
        cfg.home_layout = {};
    if (!cfg.home_layout.widgets)
        cfg.home_layout.widgets = [];
    if (!cfg.home_layout.hidden_widgets)
        cfg.home_layout.hidden_widgets = [];

    // Keep non-stat widgets intact; only rebuild the stat-related entries.
    const nonStat = cfg.home_layout.widgets.filter(
        w => !STAT_IDS.includes(w.id));

    const statWidgets = visible.map(w => ({
        id: w.id,
        col_span: Math.max(1, Math.floor(w.col_span) || 1),
        row_span: Math.max(1, Math.floor(w.row_span) || 1),
    }));

    cfg.home_layout.widgets = [...statWidgets, ...nonStat];

    cfg.home_layout.hidden_widgets = [
        ...new Set([
            ...hidden,
            ...cfg.home_layout.hidden_widgets.filter(
                h => !STAT_IDS.includes(h)),
        ]),
    ];

    _writeConfig(path, cfg);
}

/**
 * Reads and parses config.json.
 * @returns {Object|null} the parsed config, or null on any failure
 * @private
 */
function _readConfig() {
    try {
        const file = Gio.File.new_for_path(getConfigPath());
        if (!file.query_exists(null))
            return null;
        const [ok, contents] = file.load_contents(null);
        if (!ok)
            return null;
        const text = new TextDecoder('utf-8').decode(contents);
        return JSON.parse(text);
    } catch (e) {
        logError(e, 'XmaX config read');
        return null;
    }
}

/**
 * Serialises and writes the config object to disk.
 * @param {string} path
 * @param {Object} cfg
 * @private
 */
function _writeConfig(path, cfg) {
    try {
        const text = JSON.stringify(cfg, null, 2) + '\n';
        const file = Gio.File.new_for_path(path);
        file.replace_contents(
            text, null, false, Gio.FileCreateFlags.REPLACE_DESTINATION, null);
    } catch (e) {
        logError(e, 'XmaX config write');
    }
}

/**
 * Default config skeleton — matches what the backend's load_config produces
 * on first run. Only the fields relevant to our access are populated.
 * @returns {Object}
 * @private
 */
function _defaultConfig() {
    return {
        home_layout: {
            widgets: [
                {id: 'cpu', col_span: 1, row_span: 1},
                {id: 'gpu', col_span: 1, row_span: 1},
                {id: 'ram', col_span: 1, row_span: 1},
                {id: 'power', col_span: 1, row_span: 1},
            ],
            hidden_widgets: [],
            columns: 3,
            column_width: 140,
            window_height_rows: 4,
        },
    };
}