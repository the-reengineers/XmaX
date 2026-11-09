// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
// indicator.js — XmaX standalone top-bar indicator (PanelMenu.Button +
// PopupMenu readouts). Disabled by default (show-top-bar-icon): the logo and
// the readouts normally live in Quick Settings (quickSettings.js) instead.
// When enabled, the button shows the bundled XmaX logo (or a live °C label)
// and its popup lists live CPU / GPU / RAM / fan / power readouts refreshed
// from MetricsChanged.

import GObject from 'gi://GObject';
import Clutter from 'gi://Clutter';
import St from 'gi://St';

import {gettext as _} from 'resource:///org/gnome/shell/extensions/extension.js';

import {PanelMenu} from './shellCompat.js';
import {logoGicon} from './logo.js';
import {addMetricsRows, updateMetricsRows} from './metrics.js';

export const XmaxIndicator = GObject.registerClass(
class XmaxIndicator extends PanelMenu.Button {
    /**
     * @param {Gio.Settings} settings org.xmax.shell settings object
     * @param {string} extensionDir this extension's install directory
     * (Extension#path), used to locate the bundled logo
     */
    constructor(settings, extensionDir) {
        super(0.0, _('XmaX'), false);

        this._settings = settings;

        this._icon = new St.Icon({
            gicon: logoGicon(extensionDir),
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

        // Readout rows are shared with the Quick Settings metrics tile.
        this._rows = addMetricsRows(this.menu);

        this._settings.connectObject('changed::top-bar-icon-shows-temp',
            () => this._syncTempVisibility(), this);
        this._settings.connectObject('changed::show-top-bar-icon',
            () => this._syncVisibility(), this);
        this._syncVisibility();
    }

    /** @param {Object} m parsed metrics object (see protocol.cpp serialize_metrics) */
    updateMetrics(m) {
        updateMetricsRows(this._rows, m);
        this._updateTemp(m?.cpu?.temp_c ?? null);
    }

    _updateTemp(celsius) {
        const show = this._settings.get_boolean('top-bar-icon-shows-temp');
        if (show && celsius !== null && celsius !== undefined) {
            this._tempLabel.text = `${Math.round(celsius)}°C`;
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