// prefs.js — optional preferences window (`gnome-extensions prefs xmax@local`).
// Exposes the org.xmax.shell visibility toggles, charge-limit option checkboxes,
// and profile/fan-curve management. Runs in the Extensions-app process, not inside
// gnome-shell.

import Adw from 'gi://Adw';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Gtk from 'gi://Gtk';

import {ExtensionPreferences, gettext as _} from 'resource:///org/gnome/Shell/Extensions/js/extensions/prefs.js';
import {XmaxClient} from './dbus.js';

export default class XmaxPreferences extends ExtensionPreferences {
    fillPreferencesWindow(window) {
        const settings = this.getSettings();

        const page = new Adw.PreferencesPage({
            title: _('General'),
            icon_name: 'utilities-system-monitor-symbolic',
        });
        window.add(page);

        const group = new Adw.PreferencesGroup({
            title: _('Visibility'),
            description: _('Quick Settings controls are shown by default; disable any you do not need.'),
        });
        page.add(group);

        const toggles = [
            ['show-top-bar-icon', _('Show top-bar indicator')],
            ['top-bar-icon-shows-temp', _('Show CPU temperature in the top bar')],
            ['qs-charge-limit', _('Charge limit Quick Settings menu')],
            ['qs-power-profile', _('Power profile Quick Settings control')],
        ];

        for (const [key, title] of toggles) {
            const row = new Adw.SwitchRow({title});
            settings.bind(key, row, 'active', Gio.SettingsBindFlags.DEFAULT);
            group.add(row);
        }

        // ---- Charge Limit Options --------------------------------------------
        const chargeGroup = new Adw.PreferencesGroup({
            title: _('Charge Limit Options'),
            description: _('Select which values appear in the Quick Settings charge limit menu. At least one value must be enabled.'),
        });
        page.add(chargeGroup);

        const CHARGE_OPTIONS = [75, 80, 85, 90, 95, 100];
        this._chargeCheckboxes = {};

        const currentValues = settings.get_strv('charge-limit-values');

        for (const pct of CHARGE_OPTIONS) {
            const row = new Adw.SwitchRow({title: `${pct}%`});
            row.active = currentValues.includes(String(pct));
            row.connect('notify::active', () => this._syncChargeLimitValues(settings));
            this._chargeCheckboxes[pct] = row;
            chargeGroup.add(row);
        }

        // ---- Profiles page ---------------------------------------------------
        this._buildProfilesPage(window);
    }

    _syncChargeLimitValues(settings) {
        const values = [];
        for (const [pct, row] of Object.entries(this._chargeCheckboxes)) {
            if (row.active)
                values.push(pct);
        }

        // Enforce at least one value selected. If the user turned off the last
        // checkbox, re-enable it and bail out.
        if (values.length === 0) {
            const pct = Object.keys(this._chargeCheckboxes)[0];
            this._chargeCheckboxes[pct].active = true;
            return;
        }

        settings.set_strv('charge-limit-values', values);

        // If the currently-selected value is no longer in the available set,
        // move the selection to the first available value.
        const selected = settings.get_int('charge-limit-selected');
        if (!values.includes(String(selected)))
            settings.set_int('charge-limit-selected', parseInt(values[0], 10));
    }

    _buildProfilesPage(window) {
        this._client = new XmaxClient();
        if (!this._client.init()) {
            // Daemon unreachable — show a placeholder page.
            const page = new Adw.PreferencesPage({
                title: _('Profiles'),
                icon_name: 'preferences-system-power-symbolic',
            });
            const group = new Adw.PreferencesGroup({
                title: _('Profiles'),
                description: _('The XmaX daemon is not running. Start it to manage profiles.'),
            });
            page.add(group);
            window.add(page);
            return;
        }

        const page = new Adw.PreferencesPage({
            title: _('Profiles'),
            icon_name: 'preferences-system-power-symbolic',
        });
        window.add(page);

        // ---- Profiles group --------------------------------------------------
        const profilesGroup = new Adw.PreferencesGroup({
            title: _('Profiles'),
            description: _('Create and manage power profiles (Fixed TDP+fan or Adaptive PID tuning).'),
        });
        page.add(profilesGroup);

        this._profilesGroup = profilesGroup;
        this._profileRows = new Map(); // id -> ActionRow
        this._refreshProfileList();

        const addBtn = new Gtk.Button({
            label: _('Create Profile'),
            css_classes: ['suggested-action'],
            halign: Gtk.Align.CENTER,
            margin_top: 12,
        });
        addBtn.connect('clicked', () => this._openProfileEditor(null));
        profilesGroup.add(addBtn);

        // ---- Fan curves group ------------------------------------------------
        const fanCurvesGroup = new Adw.PreferencesGroup({
            title: _('Fan Curves'),
            description: _('Custom fan curves can be assigned to Fixed profiles.'),
        });
        page.add(fanCurvesGroup);

        this._fanCurvesGroup = fanCurvesGroup;
        this._fanCurveRows = new Map(); // id -> ActionRow
        this._refreshFanCurveList();

        const addCurveBtn = new Gtk.Button({
            label: _('Create Fan Curve'),
            css_classes: ['suggested-action'],
            halign: Gtk.Align.CENTER,
            margin_top: 12,
        });
        addCurveBtn.connect('clicked', () => this._openFanCurveEditor(null));
        fanCurvesGroup.add(addCurveBtn);
    }

    _refreshProfileList() {
        // Clear existing rows — remove from the preferences group (Adw.ActionRow
        // has no destroy() method; the container owns the widget lifecycle).
        for (const row of this._profileRows.values()) {
            this._profilesGroup.remove(row);
        }
        this._profileRows.clear();

        let profiles = [];
        try {
            profiles = this._client.getProfiles();
        } catch (e) {
            console.error('Failed to fetch profiles:', e);
            return;
        }

        for (const profile of profiles) {
            const row = new Adw.ActionRow({
                title: profile.name || profile.id,
                subtitle: this._profileSubtitle(profile),
            });

            const editBtn = new Gtk.Button({
                icon_name: 'document-edit-symbolic',
                valign: Gtk.Align.CENTER,
                css_classes: ['flat'],
            });
            editBtn.connect('clicked', () => this._openProfileEditor(profile));
            row.add_suffix(editBtn);

            const deleteBtn = new Gtk.Button({
                icon_name: 'user-trash-symbolic',
                valign: Gtk.Align.CENTER,
                css_classes: ['flat', 'destructive-action'],
            });
            deleteBtn.connect('clicked', () => this._deleteProfile(profile.id));
            row.add_suffix(deleteBtn);

            this._profilesGroup.add(row);
            this._profileRows.set(profile.id, row);
        }
    }

    _profileSubtitle(profile) {
        if (profile.type === 'adaptive') {
            const tuning = profile.tuning || 'default';
            return `Adaptive — ${tuning.charAt(0).toUpperCase() + tuning.slice(1)}`;
        }
        // Fixed profile — TDP values may be nested in `tdp` or flat.
        const tdp = profile.tdp || {};
        const stapm = profile.stapm ?? tdp.stapm ?? '?';
        const fast = profile.fast ?? tdp.fast ?? '?';
        const slow = profile.slow ?? tdp.slow ?? '?';
        return `Fixed — ${stapm}/${fast}/${slow}W`;
    }

    _deleteProfile(id) {
        const dialog = new Adw.AlertDialog({
            heading: _('Delete Profile?'),
            body: _('This profile will be permanently deleted.'),
        });
        dialog.add_response('cancel', _('Cancel'));
        dialog.add_response('delete', _('Delete'));
        dialog.set_response_appearance('delete', Adw.ResponseAppearance.DESTRUCTIVE);
        dialog.connect('response', (_, response) => {
            if (response === 'delete') {
                try {
                    this._client.deleteProfile(id);
                    this._refreshProfileList();
                } catch (e) {
                    this._showError(_('Failed to delete profile'), e.message || String(e));
                }
            }
        });
        dialog.present(this._getMainWindow());
    }

    _openProfileEditor(existingProfile) {
        const isEdit = !!existingProfile;
        const profile = existingProfile || {
            id: '',
            name: '',
            type: 'fixed',
            stapm: 25,
            fast: 30,
            slow: 25,
            fan_curve: 'default',
            tuning: 'default',
            target_temp_c: 80,
            tdp_max_w: 45,
            fan_max_pct: 100,
            power_state: '',
        };
        // Fixed-profile TDP values come back nested (profile.tdp.stapm/fast/slow)
        // from GetProfiles, even though SaveProfile accepts them flat. Normalize
        // so the spin rows show the real values, not the 25/30/25 defaults.
        const tdp = existingProfile?.tdp || {};

        const dialog = new Adw.Dialog({
            title: isEdit ? _('Edit Profile') : _('Create Profile'),
            content_width: 500,
        });

        const toolbarView = new Adw.ToolbarView();
        const header = new Adw.HeaderBar();
        header.set_title_widget(new Adw.WindowTitle({
            title: isEdit ? _('Edit Profile') : _('Create Profile'),
        }));
        toolbarView.add_top_bar(header);

        const page = new Adw.PreferencesPage();
        toolbarView.set_content(page);

        const basicGroup = new Adw.PreferencesGroup({title: _('Basic')});
        page.add(basicGroup);

        const nameRow = new Adw.EntryRow({
            title: _('Name'),
            text: profile.name || '',
        });
        basicGroup.add(nameRow);

        // No ID field — the slug is derived from the name. For new profiles the
        // id stays empty and the backend generates the slug (matching Windows).

        const typeModel = new Gtk.StringList();
        typeModel.append(_('Fixed'));
        typeModel.append(_('Adaptive'));
        const typeRow = new Adw.ComboRow({
            title: _('Type'),
            model: typeModel,
            selected: profile.type === 'adaptive' ? 1 : 0,
        });
        basicGroup.add(typeRow);

        // ---- Fixed profile fields --------------------------------------------
        const fixedGroup = new Adw.PreferencesGroup({title: _('Fixed Profile Settings')});
        page.add(fixedGroup);

        const stapmRow = new Adw.SpinRow({
            title: _('STAPM (W)'),
            adjustment: new Gtk.Adjustment({
                lower: 6, upper: 120, step_increment: 1, page_increment: 5,
                value: tdp.stapm ?? profile.stapm ?? 25,
            }),
        });
        fixedGroup.add(stapmRow);

        const fastRow = new Adw.SpinRow({
            title: _('Fast (W)'),
            adjustment: new Gtk.Adjustment({
                lower: 6, upper: 120, step_increment: 1, page_increment: 5,
                value: tdp.fast ?? profile.fast ?? 30,
            }),
        });
        fixedGroup.add(fastRow);

        const slowRow = new Adw.SpinRow({
            title: _('Slow (W)'),
            adjustment: new Gtk.Adjustment({
                lower: 6, upper: 120, step_increment: 1, page_increment: 5,
                value: tdp.slow ?? profile.slow ?? 25,
            }),
        });
        fixedGroup.add(slowRow);

        // Fan curve selector — populated from daemon.
        let fanCurves = [];
        try {
            fanCurves = this._client.getFanCurves();
        } catch (e) {
            console.error('Failed to fetch fan curves:', e);
        }
        // The daemon always returns the builtin "default" curve first; no need
        // to hardcode a duplicate. Show the display name, not the id slug.
        const fanCurveModel = new Gtk.StringList();
        const fanCurveIds = [];
        for (const curve of fanCurves) {
            fanCurveIds.push(curve.id);
            fanCurveModel.append(curve.name || curve.id);
        }
        const fanCurveRow = new Adw.ComboRow({
            title: _('Fan Curve'),
            model: fanCurveModel,
            selected: fanCurveIds.indexOf(profile.fan_curve || 'default'),
        });
        if (fanCurveRow.selected < 0) fanCurveRow.selected = 0;
        fixedGroup.add(fanCurveRow);

        // ---- Adaptive profile fields -----------------------------------------
        const adaptiveGroup = new Adw.PreferencesGroup({title: _('Adaptive Profile Settings')});
        page.add(adaptiveGroup);

        const tuningModel = new Gtk.StringList();
        tuningModel.append('silent');
        tuningModel.append('default');
        tuningModel.append('performance');
        const tuningRow = new Adw.ComboRow({
            title: _('Tuning Preset'),
            model: tuningModel,
            selected: ['silent', 'default', 'performance'].indexOf(profile.tuning || 'default'),
        });
        if (tuningRow.selected < 0) tuningRow.selected = 1;
        adaptiveGroup.add(tuningRow);

        const targetTempRow = new Adw.SpinRow({
            title: _('Target Temperature (°C)'),
            adjustment: new Gtk.Adjustment({
                lower: 60, upper: 95, step_increment: 1, page_increment: 5,
                value: profile.target_temp_c ?? 80,
            }),
        });
        adaptiveGroup.add(targetTempRow);

        const tdpMaxRow = new Adw.SpinRow({
            title: _('TDP Max (W)'),
            adjustment: new Gtk.Adjustment({
                lower: 6, upper: 120, step_increment: 1, page_increment: 5,
                value: profile.tdp_max_w ?? 45,
            }),
        });
        adaptiveGroup.add(tdpMaxRow);

        const fanMaxRow = new Adw.SpinRow({
            title: _('Fan Max (%)'),
            adjustment: new Gtk.Adjustment({
                lower: 0, upper: 100, step_increment: 1, page_increment: 10,
                value: profile.fan_max_pct ?? 100,
            }),
        });
        adaptiveGroup.add(fanMaxRow);

        // ---- Power state assignment ------------------------------------------
        // Values must match the backend's snake_case power state strings
        // (see backend/src/profiles.cpp: parse_power_state / power_state_str).
        const POWER_STATE_VALUES = ['battery', 'usb_c_slow', 'usb_c_fast', 'dc_in'];
        const POWER_STATE_LABELS = ['Battery', 'USB-C Slow', 'USB-C Fast', 'DC-In'];

        // Default new profiles to the current power state detected by the daemon.
        let currentPowerMode = '';
        try {
            const metrics = this._client.getMetrics();
            currentPowerMode = metrics?.power?.mode || '';
        } catch (e) {
            // Daemon metrics unavailable — fall back to first option.
        }
        const defaultPowerState = POWER_STATE_VALUES.includes(profile.power_state)
            ? profile.power_state
            : (POWER_STATE_VALUES.includes(currentPowerMode) ? currentPowerMode : POWER_STATE_VALUES[0]);

        const powerGroup = new Adw.PreferencesGroup({title: _('Power State Assignment')});
        page.add(powerGroup);

        const powerStateModel = new Gtk.StringList();
        for (const label of POWER_STATE_LABELS) powerStateModel.append(_(label));
        const powerStateRow = new Adw.ComboRow({
            title: _('Power State'),
            model: powerStateModel,
            selected: POWER_STATE_VALUES.indexOf(defaultPowerState),
        });
        if (powerStateRow.selected < 0) powerStateRow.selected = 0;
        powerGroup.add(powerStateRow);

        // ---- Visibility toggle based on type ---------------------------------
        const updateVisibility = () => {
            const isAdaptive = typeRow.selected === 1;
            fixedGroup.set_visible(!isAdaptive);
            adaptiveGroup.set_visible(isAdaptive);
        };
        typeRow.connect('notify::selected', updateVisibility);
        updateVisibility();

        // ---- Clamp TDP limits per power state ---------------------------------
        // Must match power_state_max_tdp() in backend/src/shared.h.
        const TDP_CELLINGS = [55, 20, 55, 80]; // battery, usb_c_slow, usb_c_fast, dc_in

        const clampTdpRows = () => {
            const maxW = TDP_CELLINGS[powerStateRow.selected] ?? 120;
            for (const row of [stapmRow, fastRow, slowRow, tdpMaxRow]) {
                const adj = row.adjustment;
                adj.set_upper(maxW);
                if (adj.value > maxW)
                    adj.value = maxW;
            }
        };
        powerStateRow.connect('notify::selected', clampTdpRows);
        clampTdpRows();

        // ---- Save button -----------------------------------------------------
        const saveBtn = new Gtk.Button({
            label: _('Save'),
            css_classes: ['suggested-action'],
            halign: Gtk.Align.END,
            margin_top: 12,
            margin_bottom: 12,
            margin_end: 12,
        });
        saveBtn.connect('clicked', () => {
            const name = nameRow.text.trim();
            if (!name) {
                console.warn('Profile name is required');
                return;
            }

            const type = typeRow.selected === 1 ? 'adaptive' : 'fixed';
            const tunings = ['silent', 'default', 'performance'];

            // id: empty for new profiles (backend generates the slug), or the
            // existing profile's id when editing.
            const newProfile = {
                id: profile.id,
                name,
                type,
                power_state: POWER_STATE_VALUES[powerStateRow.selected] || POWER_STATE_VALUES[0],
            };

            if (type === 'fixed') {
                newProfile.stapm = stapmRow.value;
                newProfile.fast = fastRow.value;
                newProfile.slow = slowRow.value;
                newProfile.fan_curve = fanCurveIds[fanCurveRow.selected] || 'default';
            } else {
                newProfile.tuning = tunings[tuningRow.selected] || 'default';
                newProfile.target_temp_c = targetTempRow.value;
                newProfile.tdp_max_w = tdpMaxRow.value;
                newProfile.fan_max_pct = fanMaxRow.value;
            }

            try {
                this._client.saveProfile(newProfile);
                this._refreshProfileList();
                dialog.close();
            } catch (e) {
                this._showError(_('Failed to save profile'), e.message || String(e));
            }
        });

        const vbox = new Gtk.Box({
            orientation: Gtk.Orientation.VERTICAL,
        });
        vbox.append(toolbarView);
        vbox.append(saveBtn);
        dialog.set_child(vbox);

        dialog.present(this._getMainWindow());
    }

    _refreshFanCurveList() {
        // Clear existing rows.
        for (const row of this._fanCurveRows.values()) {
            this._fanCurvesGroup.remove(row);
        }
        this._fanCurveRows.clear();

        let curves = [];
        try {
            curves = this._client.getFanCurves();
        } catch (e) {
            console.error('Failed to fetch fan curves:', e);
            return;
        }

        for (const curve of curves) {
            const pointCount = curve.points?.length || 0;
            const row = new Adw.ActionRow({
                title: curve.name || curve.id,
                subtitle: `${pointCount} point${pointCount !== 1 ? 's' : ''}`,
            });

            const editBtn = new Gtk.Button({
                icon_name: 'document-edit-symbolic',
                valign: Gtk.Align.CENTER,
                css_classes: ['flat'],
            });
            editBtn.connect('clicked', () => this._openFanCurveEditor(curve));
            row.add_suffix(editBtn);

            // Built-in curves cannot be deleted — omit the delete button.
            if (!curve.builtin) {
                const deleteBtn = new Gtk.Button({
                    icon_name: 'user-trash-symbolic',
                    valign: Gtk.Align.CENTER,
                    css_classes: ['flat', 'destructive-action'],
                });
                deleteBtn.connect('clicked', () => this._deleteFanCurve(curve.id));
                row.add_suffix(deleteBtn);
            }

            this._fanCurvesGroup.add(row);
            this._fanCurveRows.set(curve.id, row);
        }
    }

    _deleteFanCurve(id) {
        const dialog = new Adw.AlertDialog({
            heading: _('Delete Fan Curve?'),
            body: _('This fan curve will be permanently deleted.'),
        });
        dialog.add_response('cancel', _('Cancel'));
        dialog.add_response('delete', _('Delete'));
        dialog.set_response_appearance('delete', Adw.ResponseAppearance.DESTRUCTIVE);
        dialog.connect('response', (_, response) => {
            if (response === 'delete') {
                try {
                    this._client.deleteFanCurve(id);
                    this._refreshFanCurveList();
                } catch (e) {
                    this._showError(_('Failed to delete fan curve'), e.message || String(e));
                }
            }
        });
        dialog.present(this._getMainWindow());
    }

    _openFanCurveEditor(existingCurve) {
        const isEdit = !!existingCurve;
        const curve = existingCurve || {
            id: '',
            name: '',
            points: [],
        };

        const dialog = new Adw.Dialog({
            title: isEdit ? _('Edit Fan Curve') : _('Create Fan Curve'),
            content_width: 500,
        });

        const toolbarView = new Adw.ToolbarView();
        const header = new Adw.HeaderBar();
        header.set_title_widget(new Adw.WindowTitle({
            title: isEdit ? _('Edit Fan Curve') : _('Create Fan Curve'),
        }));
        toolbarView.add_top_bar(header);

        const page = new Adw.PreferencesPage();
        toolbarView.set_content(page);

        const basicGroup = new Adw.PreferencesGroup({title: _('Basic')});
        page.add(basicGroup);

        const nameRow = new Adw.EntryRow({
            title: _('Name'),
            text: curve.name || '',
        });
        basicGroup.add(nameRow);

        // No ID field — the slug is derived from the name. For new curves the
        // id stays empty and the backend generates the slug (matching Windows).

        // ---- Points editor ---------------------------------------------------
        const pointsGroup = new Adw.PreferencesGroup({
            title: _('Temperature Points'),
            description: _('Define fan speed at each temperature (°C → %).'),
        });
        page.add(pointsGroup);

        const pointsBox = new Gtk.Box({
            orientation: Gtk.Orientation.VERTICAL,
            spacing: 6,
            margin_top: 6,
            margin_bottom: 6,
        });
        pointsGroup.add(pointsBox);

        const pointRows = [];
        const addPointRow = (temp = 40, speed = 30) => {
            const hbox = new Gtk.Box({
                orientation: Gtk.Orientation.HORIZONTAL,
                spacing: 6,
            });

            const tempSpin = new Gtk.SpinButton({
                adjustment: new Gtk.Adjustment({
                    lower: 20, upper: 100, step_increment: 1, page_increment: 5,
                    value: temp,
                }),
            });
            hbox.append(tempSpin);

            const arrow = new Gtk.Label({label: '→'});
            hbox.append(arrow);

            const speedSpin = new Gtk.SpinButton({
                adjustment: new Gtk.Adjustment({
                    lower: 0, upper: 100, step_increment: 1, page_increment: 10,
                    value: speed,
                }),
            });
            hbox.append(speedSpin);

            const removeBtn = new Gtk.Button({
                icon_name: 'list-remove-symbolic',
                css_classes: ['flat', 'destructive-action'],
            });
            removeBtn.connect('clicked', () => {
                pointsBox.remove(hbox);
                const idx = pointRows.indexOf(row);
                if (idx >= 0) pointRows.splice(idx, 1);
            });
            hbox.append(removeBtn);

            const row = {hbox, tempSpin, speedSpin};
            pointRows.push(row);
            pointsBox.append(hbox);
        };

        // Populate with existing points or one default.
        if (curve.points?.length) {
            for (const p of curve.points) {
                addPointRow(p.temp_c ?? p.temp ?? 40, p.speed_pct ?? p.speed ?? 30);
            }
        } else {
            addPointRow(40, 30);
        }

        const addPointBtn = new Gtk.Button({
            label: _('Add Point'),
            css_classes: ['flat'],
            halign: Gtk.Align.START,
        });
        addPointBtn.connect('clicked', () => addPointRow(50, 50));
        pointsGroup.add(addPointBtn);

        // ---- Save button -----------------------------------------------------
        const saveBtn = new Gtk.Button({
            label: _('Save'),
            css_classes: ['suggested-action'],
            halign: Gtk.Align.END,
            margin_top: 12,
            margin_bottom: 12,
            margin_end: 12,
        });
        saveBtn.connect('clicked', () => {
            const name = nameRow.text.trim();
            if (!name) {
                console.warn('Fan curve name is required');
                return;
            }

            const points = pointRows.map(r => ({
                temp_c: r.tempSpin.value,
                speed_pct: r.speedSpin.value,
            }));

            // id: empty for new curves (backend generates the slug), or the
            // existing curve's id when editing.
            const newCurve = {id: curve.id, name, points};

            try {
                this._client.saveFanCurve(newCurve);
                this._refreshFanCurveList();
                dialog.close();
            } catch (e) {
                this._showError(_('Failed to save fan curve'), e.message || String(e));
            }
        });

        // Built-in curves are read-only — disable the Save button.
        if (existingCurve?.builtin) saveBtn.sensitive = false;

        const vbox = new Gtk.Box({
            orientation: Gtk.Orientation.VERTICAL,
        });
        vbox.append(toolbarView);
        vbox.append(saveBtn);
        dialog.set_child(vbox);

        dialog.present(this._getMainWindow());
    }

    _showError(heading, body) {
        const dlg = new Adw.AlertDialog({heading, body});
        dlg.add_response('ok', _('OK'));
        dlg.present(this._getMainWindow());
    }

    _getMainWindow() {
        // Find the parent window for dialog presentation.
        let widget = this._profilesGroup || this._fanCurvesGroup;
        while (widget && !(widget instanceof Gtk.Window)) {
            widget = widget.get_parent();
        }
        return widget;
    }
}