// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
// extension.js — XmaX GNOME Shell extension entry point (GNOME 50 / ESM).
// Wires the sysfs/D-Bus telemetry from org.xmax.Service into a top-bar
// indicator (indicator.js) and opt-in Quick Settings controls (quickSettings.js).

import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';

import {Main} from './shellCompat.js';
import {XmaxClient} from './dbus.js';
import {XmaxIndicator} from './indicator.js';
import {XmaxQuickSettingsIndicator} from './quickSettings.js';
import {getConfigPath} from './statsConfig.js';

export default class XmaxExtension extends Extension {
    enable() {
        this._settings = this.getSettings();

        this._client = new XmaxClient({
            onMetricsChanged: m => {
                this._indicator?.updateMetrics(m);
                this._quickSettings?.updateMetrics(m);
            },
        });

        this._indicator = new XmaxIndicator(this._settings, this.path);
        Main.panel.addToStatusArea('xmax-indicator', this._indicator);

        if (Main.panel.statusArea.quickSettings) {
            this._quickSettings =
                new XmaxQuickSettingsIndicator(this._client, this._settings,
                    this.path);
            Main.panel.statusArea.quickSettings
                .addExternalIndicator(this._quickSettings);

            // Persistent metrics section in the QS popup (below the tile
            // grid) — always visible when the panel opens, no tile needed.
            Main.panel.statusArea.quickSettings.menu
                .addMenuItem(this._quickSettings.metricsSection);
        }

        // Watch config.json for external changes (e.g. edits from the
        // extension preferences window) and rebuild the stats sections.
        this._cfgMonitor = _monitorConfig(
            () => {
                this._indicator?.reloadStats();
                this._quickSettings?.reloadMetricsSection();
            });

        this._client.init();

        // Populate immediately instead of waiting for the ~2s metrics cadence.
        try {
            const metrics = this._client.getMetrics();
            this._indicator.updateMetrics(metrics);
        } catch (e) {
            logError(e, 'XmaX initial getMetrics');
        }
    }

    disable() {
        this._cfgMonitor?.cancel();
        this._cfgMonitor = null;

        this._indicator?.destroy();
        this._indicator = null;

        this._quickSettings?.destroy();
        this._quickSettings = null;

        this._client?.destroy();
        this._client = null;

        this._settings = null;
    }
}

/**
 * Start watching config.json; calls `onChanged()` whenever the file is
 * created, deleted, or its contents change.
 *
 * @param {() => void} onChanged
 * @returns {Gio.FileMonitor}
 */
function _monitorConfig(onChanged) {
    try {
        const file = Gio.File.new_for_path(getConfigPath());
        const monitor = file.monitor_file(Gio.FileMonitorFlags.WATCH_MOVES, null);
        // Debounce: coalesce rapid events (atomic save = delete + create).
        let timer = 0;
        monitor.connect('changed', (_mon, _f, _other, eventType) => {
            if (eventType === Gio.FileMonitorEvent.CHANGES_DONE_HINT)
                return;
            if (timer)
                GLib.source_remove(timer);
            timer = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 300, () => {
                timer = 0;
                onChanged();
                return GLib.SOURCE_REMOVE;
            });
        });
        return monitor;
    } catch (e) {
        logError(e, 'XmaX config monitor');
        return null;
    }
}