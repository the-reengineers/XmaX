// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
// statsConfig.js — reads the shared config.json home_layout to determine
// which metrics rows to show in the Quick Settings System Stats section and
// in what order. The same file also serves the Windows home/dashboard tile
// page; the GNOME extension mirrors the widget visibility + ordering from
// home_layout.widgets and home_layout.hidden_widgets.

import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

// All metric row IDs supported by the System Stats section, in default
// display order (used when config.json is missing or unreadable).
const STAT_IDS = ['cpu', 'gpu', 'ram', 'fan', 'power', 'charge'];
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
 * config.json's home_layout: the order they appear in the widgets array,
 * then any unlisted stat in default order. IDs listed in hidden_widgets
 * are filtered out. GNOME-only rows (fan, charge) share the same array —
 * the Windows frontend skips widget ids it doesn't know, so the ordering
 * round-trips safely across platforms.
 *
 * When config.json is absent or unreadable the full default order is returned.
 *
 * @returns {string[]} e.g. ['cpu', 'gpu', 'ram', 'fan', 'power', 'charge']
 */
export function getStatWidgets() {
    const cfg = _readConfig();
    if (!cfg)
        return DEFAULT_ORDER;

    const widgets = cfg?.home_layout?.widgets || [];
    const hidden = new Set(cfg?.home_layout?.hidden_widgets || []);

    const result = [];
    const seen = new Set();
    for (const w of widgets) {
        if (STAT_IDS.includes(w.id) && !hidden.has(w.id)) {
            result.push(w.id);
            seen.add(w.id);
        }
    }

    // Stats not present in the widgets array go last, in default order.
    for (const id of STAT_IDS) {
        if (!seen.has(id) && !hidden.has(id))
            result.push(id);
    }

    return result.length ? result : DEFAULT_ORDER;
}

/**
 * Writes the visibility and ordering of the given stat IDs into config.json's
 * home_layout.widgets and home_layout.hidden_widgets. All stat IDs (shared
 * and GNOME-only) are reflected in both arrays in the given order; the
 * Windows frontend ignores ids it doesn't know, so reordering fan/charge
 * alongside the shared stats round-trips safely.
 *
 * Creates the config file (with defaults) if it doesn't exist.
 *
 * @param {string[]} visible ordered list of visible stat IDs
 * @param {string[]} hidden list of hidden stat IDs
 */
export function setStatWidgets(visible, hidden) {
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

    // Preserve span metadata the Windows layout may have set on shared
    // stat widgets (fan/charge are GNOME-only and always 1×1).
    const statWidgets = visible.map(id => {
        const existing = cfg.home_layout.widgets.find(w => w.id === id);
        return {
            id,
            col_span: existing?.col_span ?? 1,
            row_span: existing?.row_span ?? 1,
        };
    });

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