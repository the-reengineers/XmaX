// extension.js — XmaX GNOME Shell extension entry point (GNOME 50 / ESM).
// Wires the sysfs/D-Bus telemetry from org.xmax.Service into a top-bar
// indicator (indicator.js) and opt-in Quick Settings controls (quickSettings.js).

import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';

import {Main} from './shellCompat.js';
import {XmaxClient} from './dbus.js';
import {XmaxIndicator} from './indicator.js';
import {XmaxQuickSettingsIndicator} from './quickSettings.js';

export default class XmaxExtension extends Extension {
    enable() {
        this._settings = this.getSettings();

        this._client = new XmaxClient({
            onMetricsChanged: m => {
                this._indicator?.updateMetrics(m);
                this._quickSettings?.updateMetrics(m);
            },
        });

        this._indicator = new XmaxIndicator(this._settings);
        Main.panel.addToStatusArea('xmax-indicator', this._indicator);

        if (Main.panel.statusArea.quickSettings) {
            this._quickSettings =
                new XmaxQuickSettingsIndicator(this._client, this._settings);
            Main.panel.statusArea.quickSettings
                .addExternalIndicator(this._quickSettings);
        }

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
        this._indicator?.destroy();
        this._indicator = null;

        this._quickSettings?.destroy();
        this._quickSettings = null;

        this._client?.destroy();
        this._client = null;

        this._settings = null;
    }
}