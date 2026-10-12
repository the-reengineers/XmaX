// quickSettings.js — XmaX Quick Settings controls (charge-limit menu,
// profile selector). Each control is a tile registered into the Quick Settings
// panel; it can be hidden via its `qs-*` GSettings flag (default ON). Privileged
// writes go through XmaxClient to org.xmax.Service. The profile selector
// replaces GNOME's power-profile control and applies XmaX profiles (fixed
// TDP+fan, or adaptive PID tuning) instead.

import GObject from 'gi://GObject';

import {gettext as _} from 'resource:///org/gnome/shell/extensions/extension.js';

import {PopupMenu, QuickSettings} from './shellCompat.js';

// Valid charge-limit values (multiples of 5 in 75-100).
const CHARGE_VALUES = [75, 80, 85, 90, 95, 100];
const CHARGE_ICON = 'battery-symbolic';

// Map XmaX tuning presets (and `type`) to Adwaita power-profile glyphs.
const TUNING_ICONS = {
    'silent':       'power-profile-power-saver-symbolic',
    'default':      'power-profile-balanced-symbolic',
    'performance':  'power-profile-performance-symbolic',
};
const ADAPTIVE_ICON = 'preferences-system-power-symbolic';
const PROFILE_FALLBACK_ICON = 'power-profile-balanced-symbolic';

export const XmaxQuickSettingsIndicator = GObject.registerClass(
class XmaxQuickSettingsIndicator extends QuickSettings.SystemIndicator {
    _init(client, settings) {
        super._init();
        this._client = client;
        this._settings = settings;
        this._activeProfileId = null;
        this._powerMode = null;

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

    _gate(key, item) {
        item.visible = this._settings.get_boolean(key);
    }

    /**
     * Called on every MetricsChanged signal (~2 s). When the power source
     * changes (e.g. AC plugged in / unplugged), the profile menu is rebuilt
     * to show only profiles for the active mode and pick the default for it.
     */
    updateMetrics(m) {
        const mode = m?.power?.mode ?? null;
        if (mode !== this._powerMode) {
            this._powerMode = mode;
            this._refreshProfileMenu(this._power);
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

        this._applyChargeUi(toggle, selectedPct);
    }

    /**
     * Write the selection to hardware and update the toggle UI.
     * Always updates the subtitle and check ornament from GSettings,
     * even when the daemon D-Bus call fails.  Safe to call from
     * within a menu-item activate handler — no items are destroyed.
     */
    _applyChargeUi(toggle, pct) {
        try {
            this._client.setChargeLimit(pct);
        } catch (e) {
            logError(e, 'XmaX setChargeLimit');
        }
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
        toggle.connectObject('toggled', () => {
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

        super.destroy();
    }
});