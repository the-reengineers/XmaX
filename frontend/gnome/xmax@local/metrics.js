// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
// metrics.js — shared rendering of live metrics readouts. Builds the
// CPU / GPU / GPU-power / RAM / fan / power / charge-limit rows into any
// popup menu and updates them from a parsed MetricsChanged payload (see
// protocol.cpp serialize_metrics). Used by both the top-bar indicator popup
// (indicator.js) and the Quick Settings System Stats section
// (quickSettings.js).

import {gettext as _} from 'resource:///org/gnome/shell/extensions/extension.js';

import {PopupMenu} from './shellCompat.js';

const pct = n => `${Math.round(n)}%`;
const temp = n => `${Math.round(n)}°C`;
const mhz = n => `${Math.round(n)} MHz`;

// Runtime TDP ceiling per power source, in W — mirrors the backend's
// power_state_max_tdp() / runtime_tdp_ceiling() (shared.h): the max the APU
// may draw in the current state. DC-In rises to 120W while the Frost Bay
// cooler is connected.
const TDP_CEILING_W = {battery: 55, usb_c_slow: 20, usb_c_fast: 55, dc_in: 80};

/**
 * 'GPU Power' tile/row text: '<watts>W · <pct of ceiling>%'. The draw comes
 * from gpu.power_w — on APU platforms this is the amdgpu hwmon PPT reading,
 * i.e. the whole-package power. With an unrecognised power source (ceiling
 * unknown) only the wattage is shown; '--' when there is no reading at all.
 *
 * @param {Object} m parsed metrics object
 * @returns {string} e.g. '22W · 27%'
 */
function _gpuPowerText(m) {
    const w = m?.gpu?.power_w;
    if (typeof w !== 'number')
        return '--';
    const mode = m?.power?.mode;
    let ceiling = TDP_CEILING_W[mode];
    if (mode === 'dc_in' && m?.cooling?.connected)
        ceiling = 120;  // runtime_tdp_ceiling: Frost Bay raises the DC-In cap
    return ceiling
        ? `${Math.round(w)}W · ${Math.round(w / ceiling * 100)}%`
        : `${Math.round(w)}W`;
}

/**
 * All supported stat row IDs and their localised label prefixes.
 * @type {Record<string, string>}
 */
const ROW_PREFIXES = {
    cpu:         'CPU',
    gpu:         'GPU',
    gpu_power:   'GPU Power',
    cpu_temp:    'CPU Temp',
    gpu_temp:    'GPU Temp',
    gpu_hotspot: 'GPU HS Temp',
    apu:         'APU HS Temp',
    ram:         'RAM',
    fan:         'Fan',
    power:       'Power',
    charge:      'Charge limit',
};

/** Default order when no config is available. */
const DEFAULT_ORDER = ['cpu', 'gpu', 'gpu_power', 'cpu_temp', 'gpu_temp', 'gpu_hotspot', 'apu', 'ram', 'fan', 'power', 'charge'];

/**
 * Localised label prefix for a stat row ID (e.g. 'cpu' → 'CPU'), or null for
 * non-text rows like 'graph'.
 *
 * @param {string} id stat row ID
 * @returns {string|null}
 */
export function metricLabel(id) {
    const prefix = ROW_PREFIXES[id];
    return prefix ? _(prefix) : null;
}

/**
 * Create one non-reactive readout row ("CPU  ·  --"), or null for non-text
 * rows like 'graph' (rendered by quickSettings.js instead).
 *
 * @param {string} id stat row ID
 * @returns {PopupMenu.PopupMenuItem|null}
 */
export function createMetricsRow(id) {
    const label = metricLabel(id);
    if (!label)
        return null;
    const item = new PopupMenu.PopupMenuItem(`${label}  ·  --`);
    item.reactive = false;
    item.can_focus = false;
    return item;
}

/**
 * Add the readout rows to a popup menu in the given order.
 *
 * @param {PopupMenu.PopupMenuBase} menu menu to populate (indicator popup or
 * Quick Settings tile menu)
 * @param {string[]} [widgetIds] ordered list of stat row IDs to show; omitting
 * this defaults to all rows in the default order
 * @returns {Map<string, Object>} row map for updateMetricsRows()
 */
export function addMetricsRows(menu, widgetIds) {
    const ids = widgetIds?.length ? widgetIds : DEFAULT_ORDER;
    const rows = new Map();

    for (const id of ids) {
        const item = createMetricsRow(id);
        if (!item)
            continue;
        menu.addMenuItem(item);
        rows.set(id, {item, prefix: metricLabel(id)});
    }

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

    if (m.cpu && m.cpu.temp_c !== null && m.cpu.temp_c !== undefined)
        setValue('cpu_temp', temp(m.cpu.temp_c));
    else
        setValue('cpu_temp', '--');

    if (m.gpu) {
        const parts = [];
        if (m.gpu.temp_c !== null && m.gpu.temp_c !== undefined)
            parts.push(temp(m.gpu.temp_c));
        if (typeof m.gpu.util_pct === 'number')
            parts.push(pct(m.gpu.util_pct));
        setValue('gpu', parts.length ? parts.join('  ') : '--');
    }

    if (m.gpu && m.gpu.temp_c !== null && m.gpu.temp_c !== undefined)
        setValue('gpu_temp', temp(m.gpu.temp_c));
    else
        setValue('gpu_temp', '--');

    if (m.gpu && m.gpu.temp_hotspot_c !== null && m.gpu.temp_hotspot_c !== undefined)
        setValue('gpu_hotspot', temp(m.gpu.temp_hotspot_c));
    else
        setValue('gpu_hotspot', '--');

    setValue('gpu_power', _gpuPowerText(m));

    if (m.apu && m.apu.temp_hotspot_c !== null && m.apu.temp_hotspot_c !== undefined)
        setValue('apu', temp(m.apu.temp_hotspot_c));
    else
        setValue('apu', '--');

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
 * Compact value text for one stat widget — the tile (grid cell) form of the
 * full readout row: title on the tile, this string as the value. Picked to
 * fit a ~120 px Quick Settings grid cell.
 *
 * @param {string} id stat row ID
 * @param {Object} m parsed metrics object (see protocol.cpp serialize_metrics)
 * @returns {string} e.g. '62% · 72°C', or '--' without data
 */
export function compactValue(id, m) {
    if (!m)
        return '--';

    switch (id) {
    case 'cpu':
        return _compactTempUtil(m.cpu);
    case 'gpu':
        return _compactTempUtil(m.gpu);
    case 'gpu_power':
        return _gpuPowerText(m);
    case 'cpu_temp':
        return m?.cpu?.temp_c !== null && m?.cpu?.temp_c !== undefined
            ? temp(m.cpu.temp_c) : '--';
    case 'gpu_temp':
        return m?.gpu?.temp_c !== null && m?.gpu?.temp_c !== undefined
            ? temp(m.gpu.temp_c) : '--';
    case 'gpu_hotspot':
        return m?.gpu?.temp_hotspot_c !== null && m?.gpu?.temp_hotspot_c !== undefined
            ? temp(m.gpu.temp_hotspot_c) : '--';
    case 'apu':
        return m?.apu?.temp_hotspot_c !== null && m?.apu?.temp_hotspot_c !== undefined
            ? temp(m.apu.temp_hotspot_c) : '--';
    case 'ram':
        return typeof m.ram?.load_pct === 'number' ? pct(m.ram.load_pct) : '--';
    case 'fan':
        if (!m.fan)
            return '--';
        return typeof m.fan.speed_pct === 'number'
            ? `${m.fan.mode} · ${pct(m.fan.speed_pct)}`
            : `${m.fan.mode}`;
    case 'power': {
        if (!m.power)
            return '--';
        return typeof m.power.battery_pct === 'number'
            ? `${m.power.mode} · ${pct(m.power.battery_pct)}`
            : `${m.power.mode}`;
    }
    case 'charge': {
        const lim = m.power?.charge_limit_pct;
        return lim !== null && lim !== undefined ? pct(lim) : _('disabled');
    }
    default:
        return '--';
    }
}

/** '62% · 72°C' from a cpu/gpu metrics node. */
function _compactTempUtil(node) {
    if (!node)
        return '--';
    const parts = [];
    if (typeof node.util_pct === 'number')
        parts.push(pct(node.util_pct));
    else if (node.temp_c !== null && node.temp_c !== undefined)
        parts.push(temp(node.temp_c));
    return parts.length ? parts.join(' · ') : '--';
}

/**
 * Compact one-line summary for the Quick Settings tile subtitle.
 *
 * @param {Object} m parsed metrics object (see protocol.cpp serialize_metrics)
 * @returns {string} e.g. "CPU 62% · GPU 55°C · APU 78°C", or '--' without data
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
    if (m?.apu && m.apu.temp_hotspot_c !== null && m.apu.temp_hotspot_c !== undefined)
        parts.push(`APU ${temp(m.apu.temp_hotspot_c)}`);
    return parts.length ? parts.join(' · ') : '--';
}
