// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
// prefs.js — optional preferences window (`gnome-extensions prefs xmax@local`).
// Exposes the org.xmax.shell visibility toggles, charge-limit option checkboxes,
// and profile/fan-curve management. Runs in the Extensions-app process, not inside
// gnome-shell.

import Adw from 'gi://Adw';
import Gdk from 'gi://Gdk';
import Gio from 'gi://Gio';
import GObject from 'gi://GObject';
import Gtk from 'gi://Gtk';

import {ExtensionPreferences, gettext as _} from 'resource:///org/gnome/Shell/Extensions/js/extensions/prefs.js';
import {XmaxClient} from './dbus.js';
import {chartDef, chartIds, LEGEND_SYMBOLS, seriesIdsFor, SERIES} from './chartSeries.js';
import {defaultSpans, packWidgets, sizeLimits} from './gridLayout.js';
import {getHiddenStats, getStatColumns, getStatLayout, setStatColumns, setStatLayout} from './statsConfig.js';

/**
 * Remove all children of a Gtk container (the editor rebuild path — the GTK
 * API for this is awkward in GJS).
 *
 * @param {Gtk.Widget} container
 */
function _clearChildren(container) {
    let child = container.get_first_child();
    while (child) {
        const next = child.get_next_sibling();
        container.remove(child);
        child = next;
    }
}

export default class XmaxPreferences extends ExtensionPreferences {
    fillPreferencesWindow(window) {
        const settings = this.getSettings();

        const page = new Adw.PreferencesPage({
            title: _('General'),
            icon_name: 'utilities-system-monitor-symbolic',
        });
        window.add(page);

        // ---- Hardware ---------------------------------------------------------
        // session_persist gates every hardware write in the daemon; without it
        // profile/charge-limit selections are silently rejected. This is the
        // GNOME counterpart of the Windows frontend's "apply" toggle.
        const hwGroup = new Adw.PreferencesGroup({
            title: _('Hardware'),
            description: _('While off, the daemon keeps the hardware at BIOS defaults and profile or charge-limit changes have no effect. Turning this on applies the configured settings for this session.'),
        });
        page.add(hwGroup);

        this._client = new XmaxClient();
        const daemonReachable = this._client.init();

        const persistRow = new Adw.SwitchRow({
            title: _('Apply settings to hardware'),
        });
        if (daemonReachable) {
            persistRow.active = this._client.sessionPersist;
            persistRow.connect('notify::active', row => {
                try {
                    this._client.setSessionPersist(row.active);
                } catch (e) {
                    // Revert on failure — the daemon state didn't change.
                    row.active = !row.active;
                    this._showError(_('Failed to apply the setting'), e.message || String(e));
                }
            });
        } else {
            persistRow.subtitle = _('The XmaX daemon is not running.');
            persistRow.sensitive = false;
        }
        hwGroup.add(persistRow);

        const group = new Adw.PreferencesGroup({
            title: _('Visibility'),
            description: _('Quick Settings controls are shown by default; disable any you do not need.'),
        });
        page.add(group);

        const toggles = [
            ['qs-show-logo', _('XmaX logo in Quick Settings')],
            ['qs-metrics', _('Metrics readouts Quick Settings menu')],
            ['show-top-bar-icon', _('Standalone top-bar indicator')],
            ['top-bar-icon-shows-temp', _('Show CPU temperature in the top-bar indicator')],
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

        // ---- Layout page (System Stats widget editor) ------------------------
        // Drag-and-drop editor for the Quick Settings System Stats grid,
        // written to config.json and picked up live by the running shell.
        this._buildLayoutPage(window, settings);

        // ---- Profiles page ---------------------------------------------------
        this._buildProfilesPage(window);
    }

    /**
     * Layout page: drag-and-drop editor for the System Stats grid in the
     * Quick Settings panel — a clone of the popup section on the right, the
     * available (hidden) widgets on the left. Mirrors the Windows home
     * editor: visible widgets carry an X close button, available widgets a
     * "+" button, and drags rearrange the shared config.json layout, which
     * the running shell picks up via its config file monitor.
     *
     * @param {Adw.PreferencesWindow} window
     * @param {Gio.Settings} settings org.xmax.shell settings
     */
    _buildLayoutPage(window, settings) {
        const page = new Adw.PreferencesPage({
            title: _('Layout'),
            icon_name: 'view-grid-symbolic',
        });
        window.add(page);

        const group = new Adw.PreferencesGroup({
            title: _('Quick Settings Layout'),
            description: _('Arrange the System Stats widgets in the Quick Settings panel. Drag widgets to reorder them; drag between the preview and the available list to show or hide them. Changes apply without restarting the shell.'),
        });
        page.add(group);

        // Adw groups only accept preference rows — the whole editor lives
        // in one full-width non-activatable ActionRow.
        const editorRow = new Adw.ActionRow({activatable: false});
        editorRow.add_suffix(this._buildLayoutEditor());
        group.add(editorRow);

        // Column count: 1, 2 or 3 widgets side-by-side. Written to the
        // shared home_layout.columns (the same field the Windows home grid
        // reads); the running shell picks it up live via the config monitor.
        const colModel = new Gtk.StringList();
        for (const n of [1, 2, 3])
            colModel.append(`${n}`);
        this._columnsRow = new Adw.ComboRow({
            title: _('Columns'),
            subtitle: _('How many widgets fit side-by-side in the Quick Settings section.'),
            model: colModel,
            selected: getStatColumns() - 1,
        });
        this._columnsRow.connect('notify::selected', row => {
            setStatColumns(row.selected + 1);
            // Re-pack the preview with the new column count.
            this._refreshLayoutEditor();
        });
        group.add(this._columnsRow);

        // History window for the usage graph (seconds). A GSettings key, not
        // config.json: it is shell-side only, and it applies live — no
        // section rebuild needed.
        const GRAPH_WINDOWS = [30, 60, 120, 180];
        const GRAPH_WINDOW_LABELS = [
            _('30 seconds'), _('1 minute'), _('2 minutes'), _('3 minutes'),
        ];
        const windowModel = new Gtk.StringList();
        for (const label of GRAPH_WINDOW_LABELS)
            windowModel.append(label);
        let selected = GRAPH_WINDOWS.indexOf(settings.get_int('qs-graph-window'));
        if (selected < 0)
            selected = GRAPH_WINDOWS.length - 1;
        this._graphWindowRow = new Adw.ComboRow({
            title: _('Graph history'),
            subtitle: _('How far back the usage graph in Quick Settings reaches.'),
            model: windowModel,
            selected,
        });
        this._graphWindowRow.connect('notify::selected', row => {
            const sec = GRAPH_WINDOWS[row.selected];
            if (sec !== undefined)
                settings.set_int('qs-graph-window', sec);
        });
        group.add(this._graphWindowRow);

        // ---- Usage graph chart/series -------------------------------------
        this._chartGroup = new Adw.PreferencesGroup({
            title: _('Usage graph'),
            description: _('Choose what the Quick Settings usage graph charts and which lines it draws. All lines use the system accent color; the symbol shows the line style.'),
        });
        page.add(this._chartGroup);
        this._chartSettings = settings;

        const chartModel = new Gtk.StringList();
        for (const id of chartIds())
            chartModel.append(this._chartLabel(id));
        let chartSel = chartIds().indexOf(settings.get_string('graph-chart'));
        if (chartSel < 0)
            chartSel = 0;
        this._chartRow = new Adw.ComboRow({
            title: _('Chart'),
            model: chartModel,
            selected: chartSel,
        });
        this._chartRow.connect('notify::selected', row => {
            const id = chartIds()[row.selected];
            settings.set_string('graph-chart', id);
            // Switching charts resets the series to the new chart's
            // defaults (a series list only makes sense within one chart).
            settings.set_strv('graph-series', chartDef(id).defaultSeries);
            this._rebuildSeriesRows();
        });
        this._chartGroup.add(this._chartRow);

        this._rebuildSeriesRows();
    }

    /** "Temperature (°C)"-style label for a chart id. */
    _chartLabel(id) {
        const chart = chartDef(id);
        return `${_(chart.title)} (${chart.unit})`;
    }

    /**
     * (Re)builds the series rows for the selected chart: one row per
     * available series with an enabled checkbox. The title is prefixed
     * with the legend symbol for that series' line style (all lines draw
     * in the system accent color). Changing a row writes GSettings
     * immediately — the running shell rebuilds the graph section on those
     * keys.
     */
    _rebuildSeriesRows() {
        const settings = this._chartSettings;
        const chartId = settings.get_string('graph-chart');
        const enabled = new Set(settings.get_strv('graph-series'));

        for (const row of this._seriesRows ?? [])
            this._chartGroup.remove(row);
        this._seriesRows = [];

        for (const sid of seriesIdsFor(chartId)) {
            const series = SERIES[sid];
            const row = new Adw.ActionRow({
                title: `${LEGEND_SYMBOLS[series.style] ?? '●'} ${_(series.label)}`,
            });

            const check = new Gtk.CheckButton({valign: Gtk.Align.CENTER});
            check.active = enabled.has(sid);
            check.connect('toggled', () => {
                const next = new Set(settings.get_strv('graph-series'));
                if (check.active) {
                    next.add(sid);
                } else {
                    // Keep at least one series — revert the checkbox.
                    if (next.size <= 1) {
                        check.active = true;
                        return;
                    }
                    next.delete(sid);
                }
                settings.set_strv('graph-series', [...next]);
            });
            row.add_prefix(check);
            row.activatable_widget = check;

            this._chartGroup.add(row);
            this._seriesRows.push(row);
        }
    }

    /**
     * The editor itself: available widgets (left) and the Quick Settings
     * clone (right), both built from tile widgets with corner buttons and
     * GTK4 drag-and-drop.
     *
     * @returns {Gtk.Widget}
     */
    _buildLayoutEditor() {
        this._layoutWidgets = getStatLayout();
        this._layoutHidden = getHiddenStats();

        this._ensureLayoutCss();

        const box = new Gtk.Box({
            orientation: Gtk.Orientation.HORIZONTAL,
            spacing: 18,
            hexpand: true,
            margin_top: 6,
            margin_bottom: 6,
        });

        // ---- Available (hidden) widgets, with "+" buttons -----------------
        const avail = new Gtk.Box({
            orientation: Gtk.Orientation.VERTICAL,
            spacing: 6,
            valign: Gtk.Align.START,
        });
        avail.append(new Gtk.Label({
            label: _('Available widgets'),
            halign: Gtk.Align.START,
            css_classes: ['heading'],
        }));
        this._availList = new Gtk.Box({
            orientation: Gtk.Orientation.VERTICAL,
            spacing: 6,
        });
        avail.append(this._availList);

        // Dropping a preview widget onto the available side hides it.
        // DropTarget.new (static call): the positional `new Gtk.DropTarget(...)`
        // form is silently ignored by this GJS/GTK combo, leaving the target
        // with no accepted type — every drop is then rejected.
        const availTarget = Gtk.DropTarget.new(GObject.TYPE_STRING,
            Gdk.DragAction.MOVE | Gdk.DragAction.COPY);
        availTarget.connect('drop', (_t, value) => {
            this._hideWidget(String(value));
            return true;
        });
        avail.add_controller(availTarget);
        box.append(avail);

        // ---- Quick Settings clone ------------------------------------------
        const preview = new Gtk.Box({
            orientation: Gtk.Orientation.VERTICAL,
            spacing: 6,
            hexpand: true,
        });
        preview.append(new Gtk.Label({
            label: _('Quick Settings preview'),
            halign: Gtk.Align.START,
            css_classes: ['heading'],
        }));
        this._previewFrame = new Gtk.Box({
            orientation: Gtk.Orientation.VERTICAL,
            spacing: 8,
            css_classes: ['xmax-preview'],
        });
        this._previewGrid = new Gtk.Grid({
            row_spacing: 6,
            column_spacing: 6,
            hexpand: true,
        });
        // Dropping onto empty grid space appends the widget at the end.
        const gridTarget = Gtk.DropTarget.new(GObject.TYPE_STRING,
            Gdk.DragAction.MOVE | Gdk.DragAction.COPY);
        gridTarget.connect('drop', (_t, value) => {
            this._insertWidget(String(value), null, false);
            return true;
        });
        this._previewGrid.add_controller(gridTarget);
        this._previewFrame.append(this._previewGrid);
        preview.append(this._previewFrame);
        box.append(preview);

        this._refreshLayoutEditor();
        return box;
    }

    /**
     * Tile styling (GTK CSS classes for the editor tiles and preview frame).
     * Registered once per prefs process.
     */
    _ensureLayoutCss() {
        if (this._layoutCss)
            return;
        const css = new Gtk.CssProvider();
        // load_from_string (GTK 4.12+): the old load_from_data took a
        // (bytes, length) pair that GJS no longer fills in automatically.
        css.load_from_string(`
            .xmax-preview { background: alpha(currentColor, 0.05); border-radius: 12px; padding: 12px; }
            .xmax-tile { background: alpha(currentColor, 0.08); border-radius: 10px; padding: 10px 12px; min-width: 110px; }
            .xmax-tile-title { font-size: 11px; opacity: 0.7; font-weight: 600; }
            .xmax-tile-value { font-size: 13px; }
            .xmax-tile-btn { border-radius: 999px; min-width: 24px; min-height: 24px; padding: 0; margin: 4px; }
            .xmax-tile-empty { opacity: 0.55; font-style: italic; }
        `);
        Gtk.StyleContext.add_provider_for_display(
            Gdk.Display.get_default(), css,
            Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION);
        this._layoutCss = css;
    }

    /**
     * Rebuilds both panes from this._layoutWidgets / this._layoutHidden,
     * packing preview tiles with the same first-fit algorithm the shell uses.
     */
    _refreshLayoutEditor() {
        _clearChildren(this._previewGrid);
        _clearChildren(this._availList);

        const columns = getStatColumns();
        const positions = new Map(
            packWidgets(this._layoutWidgets, columns).map(p => [p.id, p]));

        for (const w of this._layoutWidgets) {
            const pos = positions.get(w.id);
            if (!pos)
                continue;
            const tile = this._createLayoutTile(
                w.id,
                'window-close-symbolic',
                _('Hide this widget'),
                () => this._hideWidget(w.id),
                (targetId, draggedId, after) =>
                    this._onTileDrop(targetId, draggedId, after),
                pos);
            this._previewGrid.attach(
                tile, pos.column, pos.row, pos.col_span, pos.row_span);
        }
        if (!this._layoutWidgets.length)
            this._previewGrid.attach(new Gtk.Label({
                label: _('No widgets — drag one in from the available list.'),
                css_classes: ['xmax-tile-empty'],
                halign: Gtk.Align.CENTER,
            }), 0, 0, 1, 1);

        for (const id of this._layoutHidden) {
            const tile = this._createLayoutTile(
                id,
                'list-add-symbolic',
                _('Show this widget'),
                () => this._showWidget(id),
                (_targetId, draggedId) => this._hideWidget(draggedId));
            this._availList.append(tile);
        }
        if (!this._layoutHidden.length)
            this._availList.append(new Gtk.Label({
                label: _('Nothing hidden.'),
                halign: Gtk.Align.START,
                css_classes: ['xmax-tile-empty'],
            }));
    }

    /**
     * One editor tile: a card with title and sample value, a corner button
     * (X on preview tiles, + on available tiles), and drag support. Preview
     * tiles of resizable widgets additionally carry the packed position
     * (`pos`): the tile height tracks its row span and a resize button
     * (bottom-right, like the Windows editor) opens a size popover. The
     * onDrop callback (preview tiles only) receives
     * (targetId, draggedId, dropAfterTargetCenter).
     *
     * @param {string} id widget id
     * @param {string} cornerIcon icon name for the corner button
     * @param {string} cornerTooltip
     * @param {function()} onCornerClick
     * @param {function(string, string, boolean)} [onDrop]
     * @param {WidgetPosition|null} [pos] packed position (preview tiles)
     * @returns {Gtk.Overlay}
     */
    _createLayoutTile(id, cornerIcon, cornerTooltip, onCornerClick, onDrop, pos = null) {
        const tile = new Gtk.Box({
            orientation: Gtk.Orientation.VERTICAL,
            spacing: 2,
            css_classes: ['xmax-tile'],
            hexpand: id === 'graph',
        });
        tile.append(new Gtk.Label({
            label: this._widgetLabel(id),
            halign: Gtk.Align.START,
            css_classes: ['xmax-tile-title'],
        }));
        tile.append(new Gtk.Label({
            // Mirrors the graph legend: symbol per line style (solid CPU,
            // dotted GPU, filled RAM) — see chartSeries.LEGEND_SYMBOLS.
            label: id === 'graph' ? '○ CPU  ◌ GPU  ● RAM' : '62% · 72°C',
            halign: Gtk.Align.START,
            css_classes: ['xmax-tile-value'],
        }));

        const overlay = new Gtk.Overlay();
        overlay.set_child(tile);

        const cornerBtn = new Gtk.Button({
            icon_name: cornerIcon,
            css_classes: ['xmax-tile-btn'],
            valign: Gtk.Align.START,
            halign: Gtk.Align.END,
            tooltip_text: cornerTooltip,
        });
        cornerBtn.connect('clicked', onCornerClick);
        overlay.add_overlay(cornerBtn);

        // Resizable preview tiles: height tracks the row span, and a
        // bottom-right resize button (as in the Windows editor) opens the
        // size popover.
        if (pos && sizeLimits(id)) {
            tile.height_request = pos.row_span * 54;
            this._addResizeControl(overlay, id);
        }

        const drag = new Gtk.DragSource({
            actions: Gdk.DragAction.MOVE | Gdk.DragAction.COPY,
        });
        drag.connect('prepare', (source, x, y) => {
            source.set_icon(new Gtk.WidgetPaintable({widget: overlay}), x, y);
            return Gdk.ContentProvider.new_for_value(id);
        });
        overlay.add_controller(drag);

        if (onDrop) {
            const target = Gtk.DropTarget.new(GObject.TYPE_STRING,
                Gdk.DragAction.MOVE | Gdk.DragAction.COPY);
            target.connect('drop', (_t, value, x) => {
                onDrop(id, String(value), x > overlay.get_width() / 2);
                return true;
            });
            overlay.add_controller(target);
        }

        return overlay;
    }

    /**
     * Resize control for a resizable preview tile: a bottom-right
     * MenuButton whose popover offers the allowed column/row spans
     * (one button per value, the current one highlighted).
     *
     * @param {Gtk.Overlay} overlay tile overlay to attach to
     * @param {string} id widget id (must be in WIDGET_SIZE_LIMITS)
     */
    _addResizeControl(overlay, id) {
        const limits = sizeLimits(id);
        const columns = getStatColumns();
        const spec = this._layoutWidgets.find(w => w.id === id);
        // Effective spans (clamped the same way the packer does) — highlight
        // the button matching what's actually on screen.
        const maxCol = Math.min(limits.maxColSpan ?? Infinity, columns);
        const minCol = Math.min(limits.minColSpan, columns);
        const currentCol = Math.min(
            Math.max(Math.floor(spec?.col_span) || limits.defaultColSpan, minCol), maxCol);
        const currentRow = Math.min(
            Math.max(Math.floor(spec?.row_span) || limits.defaultRowSpan, limits.minRowSpan),
            limits.maxRowSpan);

        const box = new Gtk.Box({
            orientation: Gtk.Orientation.VERTICAL,
            spacing: 6,
            margin_top: 6,
            margin_bottom: 6,
            margin_start: 6,
            margin_end: 6,
        });

        // Width choices — only offered when there is more than one.
        const widthOptions = [];
        for (let n = minCol; n <= maxCol; n++)
            widthOptions.push(n);
        if (widthOptions.length > 1) {
            box.append(new Gtk.Label({
                label: _('Width'),
                halign: Gtk.Align.START,
                css_classes: ['heading'],
            }));
            const rowBox = new Gtk.Box({
                orientation: Gtk.Orientation.HORIZONTAL,
                spacing: 6,
            });
            for (const n of widthOptions) {
                rowBox.append(this._createSizeButton(
                    n >= columns ? _('Full') : `${n}`,
                    n === currentCol,
                    () => this._resizeWidget(id, n, currentRow)));
            }
            box.append(rowBox);
        }

        // Height choices.
        box.append(new Gtk.Label({
            label: _('Height'),
            halign: Gtk.Align.START,
            css_classes: ['heading'],
        }));
        const heightBox = new Gtk.Box({
            orientation: Gtk.Orientation.HORIZONTAL,
            spacing: 6,
        });
        for (let n = limits.minRowSpan; n <= limits.maxRowSpan; n++) {
            heightBox.append(this._createSizeButton(
                `${n}`, n === currentRow,
                () => this._resizeWidget(id, currentCol, n)));
        }
        box.append(heightBox);

        const popover = new Gtk.Popover();
        popover.set_child(box);

        const resizeBtn = new Gtk.MenuButton({
            icon_name: 'zoom-fit-best-symbolic',
            css_classes: ['xmax-tile-btn'],
            valign: Gtk.Align.END,
            halign: Gtk.Align.END,
            tooltip_text: _('Resize'),
            popover,
        });
        overlay.add_overlay(resizeBtn);
    }

    /**
     * One size-choice button for the resize popover.
     *
     * @param {string} label
     * @param {boolean} isCurrent
     * @param {function()} onClick
     * @returns {Gtk.Button}
     */
    _createSizeButton(label, isCurrent, onClick) {
        const btn = new Gtk.Button({
            label,
            css_classes: [isCurrent ? 'suggested-action' : 'flat'],
        });
        btn.connect('clicked', onClick);
        return btn;
    }

    /** Resize a widget: update its spans in the layout and save. */
    _resizeWidget(id, colSpan, rowSpan) {
        const spec = this._layoutWidgets.find(w => w.id === id);
        if (!spec)
            return;
        spec.col_span = colSpan;
        spec.row_span = rowSpan;
        this._saveLayout();
    }

    /** Preview tile drop: insert the dragged widget before/after the target. */
    _onTileDrop(targetId, draggedId, after) {
        if (draggedId === targetId)
            return;
        this._insertWidget(draggedId, targetId, after);
    }

    /**
     * Insert a widget (currently in the grid or the available list) at the
     * position of `anchorId` (before/after it), or at the end when null.
     *
     * @param {string} id
     * @param {string|null} anchorId
     * @param {boolean} after
     */
    _insertWidget(id, anchorId, after) {
        let spec = this._layoutWidgets.find(w => w.id === id);
        if (spec) {
            this._layoutWidgets = this._layoutWidgets.filter(w => w.id !== id);
        } else {
            const hi = this._layoutHidden.indexOf(id);
            if (hi < 0)
                return;
            this._layoutHidden.splice(hi, 1);
            spec = {id, ...defaultSpans(id)};
        }

        let index = this._layoutWidgets.length;
        if (anchorId !== null) {
            const anchor = this._layoutWidgets.findIndex(w => w.id === anchorId);
            if (anchor >= 0)
                index = anchor + (after ? 1 : 0);
        }
        this._layoutWidgets.splice(index, 0, spec);
        this._saveLayout();
    }

    /** Move a widget from the preview grid to the available list. */
    _hideWidget(id) {
        const idx = this._layoutWidgets.findIndex(w => w.id === id);
        if (idx < 0)
            return;
        const [spec] = this._layoutWidgets.splice(idx, 1);
        this._layoutHidden.push(spec.id);
        this._saveLayout();
    }

    /** Move a widget from the available list to the end of the grid. */
    _showWidget(id) {
        const hi = this._layoutHidden.indexOf(id);
        if (hi < 0)
            return;
        this._layoutHidden.splice(hi, 1);
        this._layoutWidgets.push({id, ...defaultSpans(id)});
        this._saveLayout();
    }

    /** Persist the layout and re-render the editor. */
    _saveLayout() {
        setStatLayout(this._layoutWidgets, this._layoutHidden);
        this._refreshLayoutEditor();
    }

    /** Localised label for a widget id. */
    _widgetLabel(id) {
        const labels = {
            cpu:    _('CPU'),
            gpu:    _('GPU'),
            ram:    _('RAM'),
            fan:    _('Fan'),
            power:  _('Power'),
            charge: _('Charge limit'),
            graph:  _('Usage graph'),
        };
        return labels[id] ?? id;
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
        // Reuse the client created in fillPreferencesWindow (it also serves
        // the Hardware apply toggle); create one only if somehow missing.
        this._client ??= new XmaxClient();
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
            // Backend normalizes legacy names on load; map leftovers here too.
            const legacy = {silent: 'quiet', default: 'balanced'};
            const tuning = legacy[profile.tuning] || profile.tuning || 'balanced';
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
            fan_curve: '',
            tuning: 'balanced',
            target_temp_c: 85,
            tdp_max_w: 45,
            fan_max_pct: 80,
            fan_min_pct: 25,
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
        // First entry is the default: Auto — the EC firmware's own hardware
        // fan curve. Its id is the empty string ("no curve"), which the
        // backend maps to fan Auto mode. User-created curves follow.
        const fanCurveModel = new Gtk.StringList();
        const fanCurveIds = [''];
        fanCurveModel.append(_('Auto (hardware)'));
        for (const curve of fanCurves) {
            fanCurveIds.push(curve.id);
            fanCurveModel.append(curve.name || curve.id);
        }
        const fanCurveRow = new Adw.ComboRow({
            title: _('Fan Curve'),
            model: fanCurveModel,
            selected: fanCurveIds.indexOf(profile.fan_curve || ''),
        });
        if (fanCurveRow.selected < 0) fanCurveRow.selected = 0;
        fixedGroup.add(fanCurveRow);

        // ---- Adaptive profile fields -----------------------------------------
        const adaptiveGroup = new Adw.PreferencesGroup({title: _('Adaptive Profile Settings')});
        page.add(adaptiveGroup);

        // Tuning presets — value bundles matching the handheld ecosystem
        // convention (OneXConsole, ASUS, Legion Go, power-profiles-daemon).
        // Selecting a preset pre-fills the knob rows below; tweaking them
        // afterwards simply makes the profile custom.  Mirrors the Windows
        // ProfileEditorPage and backend preset_defaults().
        const TUNING_VALUES = ['quiet', 'balanced', 'performance'];
        const TUNING_LABELS = [_('Quiet'), _('Balanced'), _('Performance')];
        // Knob defaults per preset: [targetTemp, tdpMax, fanMax, fanMin].
        const PRESET_DEFAULTS = {
            quiet:       [80, 25, 45, 20],
            balanced:    [85, 45, 80, 25],
            performance: [90, 100, 100, 30],
        };
        const tuningModel = new Gtk.StringList();
        for (const label of TUNING_LABELS) tuningModel.append(label);
        // Backend normalizes legacy names ("silent"/"default") on load;
        // map them here too in case an old payload slips through.
        const legacyTuning = {silent: 'quiet', default: 'balanced'};
        const tuningValue = legacyTuning[profile.tuning] || profile.tuning || 'balanced';
        const tuningRow = new Adw.ComboRow({
            title: _('Tuning Preset'),
            model: tuningModel,
            selected: TUNING_VALUES.indexOf(tuningValue),
        });
        if (tuningRow.selected < 0) tuningRow.selected = 1;
        adaptiveGroup.add(tuningRow);

        const targetTempRow = new Adw.SpinRow({
            title: _('Target Temperature (°C)'),
            adjustment: new Gtk.Adjustment({
                lower: 60, upper: 95, step_increment: 1, page_increment: 5,
                value: profile.target_temp_c ?? 85,
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
                value: profile.fan_max_pct ?? 80,
            }),
        });
        adaptiveGroup.add(fanMaxRow);

        const fanMinRow = new Adw.SpinRow({
            title: _('Fan Min (%)'),
            adjustment: new Gtk.Adjustment({
                lower: 0, upper: 100, step_increment: 1, page_increment: 10,
                value: profile.fan_min_pct ?? 25,
            }),
        });
        adaptiveGroup.add(fanMinRow);

        // Selecting a preset loads its value bundle into the knob rows.
        // The preset is a starting point, not a hidden cap: every value
        // stays editable afterwards.
        tuningRow.connect('notify::selected', () => {
            const preset = TUNING_VALUES[tuningRow.selected];
            const defaults = PRESET_DEFAULTS[preset];
            if (!defaults)
                return;
            targetTempRow.value = defaults[0];
            tdpMaxRow.value = defaults[1];
            fanMaxRow.value = defaults[2];
            fanMinRow.value = defaults[3];
        });

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
        // TODO: 120W (dc_in) is a temporary placeholder. Add external cooling
        // device detection and only allow 120W TDP when cooling is present (fall
        // back to the previous 80W otherwise) once that detection is implemented.
        const TDP_CELLINGS = [55, 20, 55, 120]; // battery, usb_c_slow, usb_c_fast, dc_in

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
            const tunings = TUNING_VALUES;

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
                newProfile.fan_curve = fanCurveIds[fanCurveRow.selected] || '';
            } else {
                newProfile.tuning = tunings[tuningRow.selected] || 'balanced';
                newProfile.target_temp_c = targetTempRow.value;
                newProfile.tdp_max_w = tdpMaxRow.value;
                newProfile.fan_max_pct = fanMaxRow.value;
                // Floor can never exceed the ceiling (backend validates too).
                newProfile.fan_min_pct = Math.min(fanMinRow.value, fanMaxRow.value);
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