// indicator.js — XmaX top-bar indicator (PanelMenu.Button + PopupMenu readouts).
// The icon is always shown (unless show-top-bar-icon is false); the popup lists
// live CPU / GPU / RAM / fan / power readouts refreshed from MetricsChanged.

import GObject from 'gi://GObject';
import Clutter from 'gi://Clutter';
import St from 'gi://St';

import {gettext as _} from 'resource:///org/gnome/shell/extensions/extension.js';

import {PanelMenu, PopupMenu} from './shellCompat.js';

// Adwaita icon used on the top bar. No dedicated "xmax" icon ships with the
// shell, so we borrow the system-monitor glyph.
const INDICATOR_ICON = 'utilities-system-monitor-symbolic';

const pct = n => `${Math.round(n)}%`;
const temp = n => `${Math.round(n)}°C`;
const mhz = n => `${Math.round(n)} MHz`;

export const XmaxIndicator = GObject.registerClass(
class XmaxIndicator extends PanelMenu.Button {
    constructor(settings) {
        super(0.0, _('XmaX'), false);

        this._settings = settings;

        this._icon = new St.Icon({
            icon_name: INDICATOR_ICON,
            style_class: 'system-status-icon',
        });
        this.add_child(this._icon);

        // Optional temperature readout replacing the glyph (off by default).
        this._tempLabel = new St.Label({
            y_align: Clutter.ActorAlign.CENTER,
            text: '--',
        });
        this._tempLabel.hide();
        this.add_child(this._tempLabel);

        // Readout rows are PopupMenuItems with a static prefix label and a
        // value we rewrite on each metrics update.
        this._rows = new Map();
        this._buildRows();

        this._settings.connectObject('changed::top-bar-icon-shows-temp',
            () => this._syncTempVisibility(), this);
        this._settings.connectObject('changed::show-top-bar-icon',
            () => this._syncVisibility(), this);
        this._syncVisibility();
    }

    _addRow(id, prefix) {
        const item = new PopupMenu.PopupMenuItem(`${prefix} --`);
        item.reactive = false;
        item.can_focus = false;
        this.menu.addMenuItem(item);
        this._rows.set(id, {item, prefix});
    }

    _buildRows() {
        this._addRow('cpu', _('CPU'));
        this._addRow('gpu', _('GPU'));
        this._addRow('ram', _('RAM'));
        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        this._addRow('fan', _('Fan'));
        this._addRow('power', _('Power'));
        this._addRow('charge', _('Charge limit'));
    }

    _setValue(id, value) {
        const row = this._rows.get(id);
        if (row)
            row.item.label.text = `${row.prefix}  ·  ${value}`;
    }

    /** @param {Object} m parsed metrics object (see protocol.cpp serialize_metrics) */
    updateMetrics(m) {
        if (!m)
            return;

        if (m.cpu) {
            const parts = [];
            if (m.cpu.temp_c !== null && m.cpu.temp_c !== undefined)
                parts.push(temp(m.cpu.temp_c));
            if (typeof m.cpu.util_pct === 'number')
                parts.push(pct(m.cpu.util_pct));
            if (m.cpu.clock_mhz !== null && m.cpu.clock_mhz !== undefined)
                parts.push(mhz(m.cpu.clock_mhz));
            this._setValue('cpu', parts.join('  '));
        }

        if (m.gpu) {
            const parts = [];
            if (m.gpu.temp_c !== null && m.gpu.temp_c !== undefined)
                parts.push(temp(m.gpu.temp_c));
            if (typeof m.gpu.util_pct === 'number')
                parts.push(pct(m.gpu.util_pct));
            this._setValue('gpu', parts.length ? parts.join('  ') : '--');
        }

        if (m.ram && typeof m.ram.load_pct === 'number')
            this._setValue('ram', pct(m.ram.load_pct));

        if (m.fan)
            this._setValue('fan', `${m.fan.mode}${typeof m.fan.speed_pct === 'number' ? ` · ${pct(m.fan.speed_pct)}` : ''}`);

        if (m.power) {
            const parts = [m.power.mode];
            if (typeof m.power.battery_pct === 'number')
                parts.push(pct(m.power.battery_pct));
            this._setValue('power', parts.join('  '));
        }

        if (m.power) {
            const lim = m.power.charge_limit_pct;
            this._setValue('charge', lim !== null && lim !== undefined ? pct(lim) : _('disabled'));
        }

        this._updateTemp(m.cpu?.temp_c ?? null);
    }

    _updateTemp(celsius) {
        const show = this._settings.get_boolean('top-bar-icon-shows-temp');
        if (show && celsius !== null && celsius !== undefined) {
            this._tempLabel.text = temp(celsius);
            this._icon.hide();
            this._tempLabel.show();
        } else {
            this._tempLabel.hide();
            this._icon.show();
        }
    }

    _syncTempVisibility() {
        this._updateTemp(null);
    }

    _syncVisibility() {
        this.visible = this._settings.get_boolean('show-top-bar-icon');
    }

    destroy() {
        this._settings.disconnectObject(this);
        super.destroy();
    }
});