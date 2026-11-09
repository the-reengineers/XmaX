// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
// quickSettings.js — XmaX Quick Settings presence: the app logo inside the
// top-bar Quick Settings button (qs-show-logo), a persistent metrics section
// in the QS popup (qs-metrics), the charge-limit menu (qs-charge-limit) and
// the profile selector (qs-power-profile). Privileged writes go through
// XmaxClient to org.xmax.Service. The profile selector replaces GNOME's
// power-profile control and applies XmaX profiles (fixed TDP+fan, or adaptive
// PID tuning) instead.

import GObject from 'gi://GObject';
import St from 'gi://St';

import {gettext as _} from 'resource:///org/gnome/shell/extensions/extension.js';

import {PopupMenu, QuickSettings} from './shellCompat.js';
import {logoGicon} from './logo.js';
import {GraphHistory} from './graph.js';
import {MetricsGridItem} from './statsGrid.js';
import {getStatColumns, getStatLayout} from './statsConfig.js';
import {sampleSeries} from './chartSeries.js';

// Valid charge-limit values (multiples of 5 in 75-100).
const CHARGE_VALUES = [75, 80, 85, 90, 95, 100];
const CHARGE_ICON = 'battery-symbolic';

// Map XmaX tuning presets (and `type`) to Adwaita power-profile glyphs.
// Legacy names (pre-redesign "silent"/"default") kept for older payloads.
const TUNING_ICONS = {
    'quiet':        'power-profile-power-saver-symbolic',
    'balanced':     'power-profile-balanced-symbolic',
    'performance':  'power-profile-performance-symbolic',
    'silent':       'power-profile-power-saver-symbolic',
    'default':      'power-profile-balanced-symbolic',
};
const ADAPTIVE_ICON = 'preferences-system-power-symbolic';
const PROFILE_FALLBACK_ICON = 'power-profile-balanced-symbolic';

export const XmaxQuickSettingsIndicator = GObject.registerClass(
class XmaxQuickSettingsIndicator extends QuickSettings.SystemIndicator {
    /**
     * @param {XmaxClient} client D-Bus client (org.xmax.Service)
     * @param {Gio.Settings} settings org.xmax.shell settings object
     * @param {string} extensionDir this extension's install directory
     * (Extension#path), used to locate the bundled logo
     */
    _init(client, settings, extensionDir) {
        super._init();
        this._client = client;
        this._settings = settings;
        this._activeProfileId = null;
        this._powerMode = null;

        // The XmaX logo inside the top-bar Quick Settings button, next to the
        // network / volume / battery icons (addExternalIndicator puts
        // SystemIndicator children there). The icon itself is not a separate
        // button — opening the Quick Settings panel is how users reach the
        // XmaX tiles.
        this._logo = this._addIndicator();
        this._logo.gicon = logoGicon(extensionDir);
        this._settings.connectObject('changed::qs-show-logo',
            () => this._gateLogo(), this);
        this._gateLogo();

        // Usage-graph history: timestamped ring buffer fed on every
        // MetricsChanged (~2 s), independent of the widgets — so the graph
        // is already full of recent history whenever the panel opens. It
        // survives section rebuilds (config changes) too.
        this._history = new GraphHistory();

        // Persistent metrics section added to the QS popup box (not the tile
        // grid) — always visible when the panel opens, no click required.
        this._metricsSection = this._buildMetricsSection();
        this._metricsStats = null; // last payload, replayed on reload

        this._settings.connectObject('changed::qs-metrics',
            () => this._metricsSection.visible =
                this._settings.get_boolean('qs-metrics'), this);
        this._metricsSection.visible = this._settings.get_boolean('qs-metrics');

        this._charge = this._buildChargeMenu();
        this._power = this._buildProfileToggle();

        this.quickSettingsItems.push(this._charge, this._power);

        const gates = [
            ['qs-charge-limit', this._charge],
            ['qs-power-profile', this._power],
        ];
        for (const [key, item] of gates) {
            this._settings.connectObject(`changed::${key}`,
                () => this._gate(key, item), this);
            this._gate(key, item);
        }
    }

    /** @returns {PopupMenu.PopupMenuSection} persistent metrics section */
    get metricsSection() { return this._metricsSection; }

    _gateLogo() {
        this._logo.visible = this._settings.get_boolean('qs-show-logo');
    }

    _gate(key, item) {
        item.visible = this._settings.get_boolean(key);
    }

    /**
     * Called on every MetricsChanged signal (~2 s). When the power source
     * changes (e.g. AC plugged in / unplugged), the profile menu is rebuilt
     * to show only profiles for the active mode and pick the default for it.
     * The metrics tile subtitle and popup rows are refreshed in passing.
     */
    updateMetrics(m) {
        const mode = m?.power?.mode ?? null;
        if (mode !== this._powerMode) {
            this._powerMode = mode;
            this._refreshProfileMenu(this._power);
        }

        // Cache the latest payload so reloadMetricsSection can replay it
        // after rebuilding the grid (config change).
        this._metricsStats = m;
        this._gridItem?.updateMetrics(m);

        // Feed the usage-graph history (nulls are skipped per-series at
        // draw time) and refresh the graph. Cheap when the panel is closed.
        this._history.push(Date.now(), sampleSeries(m));
        this._gridItem?.graph?.queue_repaint();
    }

    /**
     * Persistent metrics section in the Quick Settings popup: a labeled
     * separator (section header) followed by the System Stats tile grid —
     * stat tiles side-by-side plus the usage graph, laid out by config.json's
     * home_layout (shared with the Windows home grid). No clicking — the
     * stats are visible whenever the panel is open.
     */
    _buildMetricsSection() {
        const section = new PopupMenu.PopupMenuSection();
        this._gridItem = null;
        this._sectionSignature = null;
        this._repopulateMetricsSection(section);

        // Chart/series changes (GSettings, set by the prefs Layout page)
        // rebuild the section — the graph's series list is baked into the
        // GraphBox. The history survives (owned here). The accent color is
        // tracked live through St.Settings (dconf appearance accent-color).
        for (const key of ['graph-chart', 'graph-series'])
            this._settings.connectObject(`changed::${key}`,
                () => this.reloadMetricsSection(), this);
        St.Settings.get().connectObject('notify::accent-color',
            () => this.reloadMetricsSection(), this);

        return section;
    }

    /**
     * Fill a (fresh) section from the current config layout. An empty layout
     * (every widget hidden) renders an empty section — no separator either.
     *
     * @param {PopupMenu.PopupMenuSection} section
     */
    _repopulateMetricsSection(section) {
        const widgets = getStatLayout();
        const columns = getStatColumns();
        this._sectionSignature = JSON.stringify([
            widgets, columns, this._graphSettingsSnapshot()]);

        if (widgets.length) {
            const separator =
                new PopupMenu.PopupSeparatorMenuItem(_('System Stats'));
            // St 18 greys out non-reactive widgets (:insensitive → dim
            // text). The section header is informational, not disabled:
            // flip reactive after construction to get the standard primary
            // text color. track_hover stays off (set from the constructor's
            // reactive: false), so there is no hover highlight.
            separator.reactive = true;
            section.addMenuItem(separator);
            this._gridItem = new MetricsGridItem({
                widgets,
                columns,
                history: this._history,
                settings: this._settings,
                accentColor: this._accentHex(),
            });
            section.addMenuItem(this._gridItem);
            // Replay the latest snapshot so new tiles are populated at once.
            if (this._metricsStats)
                this._gridItem.updateMetrics(this._metricsStats);
        } else {
            this._gridItem = null;
        }
    }

    /**
     * Rebuild the System Stats section from config.json when the layout
     * changed (order, spans, columns, visibility). Safe to call from a
     * FileMonitor callback — it's a no-op without a delta. The graph's
     * history survives the rebuild (owned here, not by the grid item).
     */
    reloadMetricsSection() {
        const signature = JSON.stringify([
            getStatLayout(), getStatColumns(), this._graphSettingsSnapshot()]);
        if (signature === this._sectionSignature)
            return;

        this._metricsSection.removeAll();
        this._repopulateMetricsSection(this._metricsSection);
    }

    /** Current chart/series/accent settings — part of the section signature. */
    _graphSettingsSnapshot() {
        return [
            this._settings.get_string('graph-chart'),
            this._settings.get_strv('graph-series'),
            this._accentHex(),
        ];
    }

    /**
     * The live system accent color as '#rrggbb' — the line color for every
     * chart series. Resolved straight from St's theme machinery
     * (st_theme_context_get_accent_color), i.e. exactly the value
     * -st-accent-color expands to in the running theme — no palette
     * duplicated here to drift from the system. fg is the accent's
     * contrasting foreground color (unused). Returns '' (neutral graph
     * fallback) if the API ever moves.
     *
     * @returns {string}
     */
    _accentHex() {
        try {
            const [bg] = St.ThemeContext.get_for_stage(global.stage)
                .get_accent_color();
            const hex = v => v.toString(16).padStart(2, '0');
            return `#${hex(bg.red)}${hex(bg.green)}${hex(bg.blue)}`;
        } catch (e) {
            console.warn(`XmaX: accent color unavailable: ${e.message}`);
            return '';
        }
    }

    _buildChargeMenu() {
        const toggle = new QuickSettings.QuickMenuToggle({
            title: _('Charge Limit'),
            subtitle: '--',
            iconName: CHARGE_ICON,
            toggleMode: false,
            menuButtonAccessibleName: _('Open charge limit menu'),
        });
        this._chargeToggle = toggle;

        this._chargeSection = new PopupMenu.PopupMenuSection();
        toggle.menu.addMenuItem(this._chargeSection);
        toggle.menu.setHeader(CHARGE_ICON, _('Charge Limit'));

        // Rebuild the menu whenever the user changes which values are
        // available in the extension preferences.
        this._settings.connectObject('changed::charge-limit-values',
            () => this._refreshChargeMenu(), this);

        this._refreshChargeMenu();
        return toggle;
    }

    _refreshChargeMenu() {
        const toggle = this._chargeToggle;
        if (!toggle) return;

        // Read the user's configured values from GSettings. Fall back to
        // the full set if the key is empty or unreadable.
        let values;
        try {
            values = this._settings.get_strv('charge-limit-values')
                .map(s => parseInt(s, 10))
                .filter(n => !isNaN(n));
        } catch (_) {
            values = [];
        }
        if (!values.length)
            values = CHARGE_VALUES;

        // Pick the selected value, falling back to the schema default.
        let selectedPct = this._settings.get_int('charge-limit-selected');
        if (!values.includes(selectedPct)) {
            selectedPct = values[0];
            this._settings.set_int('charge-limit-selected', selectedPct);
        }

        // Build a map so _applyChargeUi can find items by value when
        // moving the check ornament in-place (no removeAll during a
        // signal handler — that destroys the item mid-flight).
        this._chargeItems = new Map();
        this._chargeSection.removeAll();
        for (const pct of values) {
            const item = new PopupMenu.PopupImageMenuItem(
                `${pct}%`, CHARGE_ICON);
            item.connect('activate',
                () => this._applyChargeUi(toggle, pct));
            this._chargeItems.set(pct, item);
            this._chargeSection.addMenuItem(item);
        }

        // UI only — the hardware write happens on explicit item activation
        // (_applyChargeUi). Writing here would push a charge limit at
        // menu-build time, which the daemon rejects with persist_disabled
        // unless hardware writes are enabled (and the user never asked).
        this._updateChargeUi(toggle, selectedPct);
    }

    /**
     * Write the selection to hardware (user activation only) and update the
     * toggle UI. Always updates the subtitle and check ornament, even when
     * the daemon D-Bus call fails. Safe to call from within a menu-item
     * activate handler — no items are destroyed.
     */
    _applyChargeUi(toggle, pct) {
        try {
            this._client.setChargeLimit(pct);
        } catch (e) {
            logError(e, 'XmaX setChargeLimit');
        }
        this._updateChargeUi(toggle, pct);
    }

    /**
     * Update the toggle subtitle, GSettings selection, and check ornament
     * for `pct`. No hardware writes, no error paths.
     */
    _updateChargeUi(toggle, pct) {
        this._settings.set_int('charge-limit-selected', pct);
        toggle.subtitle = `${pct}%`;

        // Move the check ornament from the previous active item.
        if (this._chargeActiveItem)
            this._chargeActiveItem.setOrnament(PopupMenu.Ornament.NONE);
        const item = this._chargeItems?.get(pct);
        if (item) {
            item.setOrnament(PopupMenu.Ornament.CHECK);
            this._chargeActiveItem = item;
        }
    }

    _buildProfileToggle() {
        const toggle = new QuickSettings.QuickMenuToggle({
            title: _('Power Profile'),
            subtitle: '--',
            iconName: PROFILE_FALLBACK_ICON,
            toggleMode: false,
            menuButtonAccessibleName: _('Open power profile menu'),
        });

        this._profileSection = new PopupMenu.PopupMenuSection();
        toggle.menu.addMenuItem(this._profileSection);
        toggle.menu.setHeader(PROFILE_FALLBACK_ICON, _('Power Profile'));

        // Refresh profiles every time the user opens the menu so we always
        // show the latest state — the daemon may have restarted or changed.
        toggle.menu.connectObject('open-state-changed', (menu, open) => {
            if (open) this._refreshProfileMenu(toggle);
        }, this);

        // When the toggle is in single-profile mode (toggleMode: true),
        // prevent visual toggling and refresh in case the user added
        // another profile for this power mode since last check.
        //
        // GNOME 50 has no 'toggled' signal on QuickMenuToggle (it was never
        // guaranteed API); notify::checked fires on every check-state change
        // and exists wherever the `checked` property does. Setting
        // checked = false from inside the handler is safe: the second
        // notify::checked no-ops because the value is unchanged.
        toggle.connectObject('notify::checked', () => {
            if (toggle.toggleMode) {
                toggle.checked = false;
                this._refreshProfileMenu(toggle);
            }
        }, this);

        this._refreshProfileMenu(toggle);
        return toggle;
    }

    _refreshProfileMenu(toggle) {
        try {
            const allProfiles = this._client.getProfiles();

            // Only show profiles that match the current power source.
            const profiles = this._powerMode
                ? allProfiles.filter(p => p.power_state === this._powerMode)
                : allProfiles;

            // When there is at most one profile for this power mode, the
            // control acts as a non-expandable indicator — no dropdown menu,
            // just the profile name and icon.
            if (profiles.length <= 1) {
                const profile = profiles[0] ?? null;
                toggle.toggleMode = true;
                toggle.checked = false;
                this._profileSection.removeAll();
                if (profile) {
                    this._activeProfileId = profile.id;
                    this._applyProfileUi(toggle, profile);
                } else {
                    toggle.subtitle = '--';
                    toggle.iconName = PROFILE_FALLBACK_ICON;
                }
                toggle.visible = this._settings.get_boolean('qs-power-profile');
                return;
            }

            // Multiple profiles — expandable menu mode.
            toggle.toggleMode = false;

            // Pick an initial active profile if we haven't tracked one yet —
            // prefer the default profile for the current power mode, then fall
            // back to the first profile in the list.
            if (this._activeProfileId &&
                !profiles.some(p => p.id === this._activeProfileId)) {
                this._activeProfileId = null;
            }
            if (!this._activeProfileId && profiles.length) {
                const def = profiles.find(p => p.is_default);
                this._activeProfileId = def ? def.id : profiles[0].id;
            }

            this._profileSection.removeAll();
            for (const profile of profiles) {
                const icon = this._iconForProfile(profile);
                const item = new PopupMenu.PopupImageMenuItem(
                    profile.name || profile.id, icon);
                item.connect('activate', () => {
                    try {
                        this._client.setProfile(profile.id);
                        this._activeProfileId = profile.id;
                        this._applyProfileUi(toggle, profile);
                    } catch (e) {
                        logError(e, 'XmaX setProfile');
                        // The persist gate is the one rejection the user can
                        // fix themselves — point at the prefs toggle instead
                        // of failing silently.
                        if (String(e).includes('persist_disabled')) {
                            toggle.subtitle = _(
                                'Hardware writes off — enable "Apply settings to hardware" in XmaX settings');
                        }
                    }
                });
                if (profile.id === this._activeProfileId)
                    item.setOrnament(PopupMenu.Ornament.CHECK);
                this._profileSection.addMenuItem(item);
            }

            const active = profiles.find(p => p.id === this._activeProfileId);
            if (active) this._applyProfileUi(toggle, active);

            toggle.visible = this._settings.get_boolean('qs-power-profile');
        } catch (e) {
            // Daemon unreachable — hide the control.
            toggle.visible = false;
        }
    }

    _applyProfileUi(toggle, profile) {
        toggle.subtitle = profile.name || profile.id;
        toggle.iconName = this._iconForProfile(profile);
    }

    _iconForProfile(profile) {
        // Adaptive profiles always get the generic power glyph. Fixed profiles
        // map their tuning preset (Silent/Default/Performance) to Adwaita icons.
        if (profile.type === 'adaptive') return ADAPTIVE_ICON;
        return TUNING_ICONS[profile.tuning] ?? PROFILE_FALLBACK_ICON;
    }

    destroy() {
        this._settings.disconnectObject(this);

        // Remove items from the Quick Settings menu grid. They were added as
        // grid children by addExternalIndicator() and are NOT children of the
        // SystemIndicator, so super.destroy() won't clean them up.
        for (const item of this.quickSettingsItems)
            item.destroy();
        this.quickSettingsItems = [];

        // The metrics section lives in the QS popup box (not the grid) — also
        // not a child of this SystemIndicator.
        this._metricsSection?.destroy();
        this._metricsSection = null;

        super.destroy();
    }
});