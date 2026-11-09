// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
// metrics.js — shared rendering of live metrics readouts. Builds the CPU /
// GPU / RAM / fan / power / charge-limit rows into any popup menu and updates
// them from a parsed MetricsChanged payload (see protocol.cpp
// serialize_metrics). Used by both the top-bar indicator popup (indicator.js)
// and the Quick Settings metrics tile (quickSettings.js).

import {gettext as _} from 'resource:///org/gnome/shell/extensions/extension.js';

import {PopupMenu} from './shellCompat.js';

const pct = n => `${Math.round(n)}%`;
const temp = n => `${Math.round(n)}°C`;
const mhz = n => `${Math.round(n)} MHz`;

/**
 * Add the readout rows to a popup menu.
 *
 * @param {PopupMenu.PopupMenuBase} menu menu to populate (indicator popup or
 * Quick Settings tile menu)
 * @returns {Map<string, Object>} row map for updateMetricsRows()
 */
export function addMetricsRows(menu) {
    const rows = new Map();

    // Readout rows are PopupMenuItems with a static prefix label and a value
    // we rewrite on each metrics update.
    const addRow = (id, prefix) => {
        const item = new PopupMenu.PopupMenuItem(`${prefix} --`);
        item.reactive = false;
        item.can_focus = false;
        menu.addMenuItem(item);
        rows.set(id, {item, prefix});
    };

    addRow('cpu', _('CPU'));
    addRow('gpu', _('GPU'));
    addRow('ram', _('RAM'));
    menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
    addRow('fan', _('Fan'));
    addRow('power', _('Power'));
    addRow('charge', _('Charge limit'));

    return rows;
}

/**
 * Update the rows created by addMetricsRows() from a metrics payload.
 *
 * @param {Map<string, Object>} rows row map from addMetricsRows()
 * @param {Object} m parsed metrics object (see protocol.cpp serialize_metrics)
 */
export function updateMetricsRows(rows, m) {
    if (!m)
        return;

    const setValue = (id, value) => {
        const row = rows.get(id);
        if (row)
            row.item.label.text = `${row.prefix}  ·  ${value}`;
    };

    if (m.cpu) {
        const parts = [];
        if (m.cpu.temp_c !== null && m.cpu.temp_c !== undefined)
            parts.push(temp(m.cpu.temp_c));
        if (typeof m.cpu.util_pct === 'number')
            parts.push(pct(m.cpu.util_pct));
        if (m.cpu.clock_mhz !== null && m.cpu.clock_mhz !== undefined)
            parts.push(mhz(m.cpu.clock_mhz));
        setValue('cpu', parts.join('  '));
    }

    if (m.gpu) {
        const parts = [];
        if (m.gpu.temp_c !== null && m.gpu.temp_c !== undefined)
            parts.push(temp(m.gpu.temp_c));
        if (typeof m.gpu.util_pct === 'number')
            parts.push(pct(m.gpu.util_pct));
        setValue('gpu', parts.length ? parts.join('  ') : '--');
    }

    if (m.ram && typeof m.ram.load_pct === 'number')
        setValue('ram', pct(m.ram.load_pct));

    if (m.fan)
        setValue('fan', `${m.fan.mode}${typeof m.fan.speed_pct === 'number' ? ` · ${pct(m.fan.speed_pct)}` : ''}`);

    if (m.power) {
        const parts = [m.power.mode];
        if (typeof m.power.battery_pct === 'number')
            parts.push(pct(m.power.battery_pct));
        setValue('power', parts.join('  '));
    }

    if (m.power) {
        const lim = m.power.charge_limit_pct;
        setValue('charge', lim !== null && lim !== undefined ? pct(lim) : _('disabled'));
    }
}

/**
 * Compact one-line summary for the Quick Settings tile subtitle.
 *
 * @param {Object} m parsed metrics object (see protocol.cpp serialize_metrics)
 * @returns {string} e.g. "CPU 62% · GPU 55°C", or '--' without data
 */
export function metricsSummary(m) {
    const parts = [];
    if (m?.cpu) {
        if (typeof m.cpu.util_pct === 'number')
            parts.push(`CPU ${pct(m.cpu.util_pct)}`);
        else if (m.cpu.temp_c !== null && m.cpu.temp_c !== undefined)
            parts.push(`CPU ${temp(m.cpu.temp_c)}`);
    }
    if (m?.gpu && m.gpu.temp_c !== null && m.gpu.temp_c !== undefined)
        parts.push(`GPU ${temp(m.gpu.temp_c)}`);
    return parts.length ? parts.join(' · ') : '--';
}
