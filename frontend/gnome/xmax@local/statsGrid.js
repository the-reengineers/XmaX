// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
// statsGrid.js — the Quick Settings System Stats section as a tile grid:
// compact stat tiles (title + value) laid out side-by-side by the shared
// first-fit packer (gridLayout.js), with the resizable usage graph (its
// height tracks its row span). Mirrors the Windows home grid: same config
// data (order + col_span/row_span), same packing algorithm, capped at 3
// columns for the popup's width.

import Clutter from 'gi://Clutter';
import GObject from 'gi://GObject';
import St from 'gi://St';

import {gettext as _} from 'resource:///org/gnome/shell/extensions/extension.js';

import {PopupMenu} from './shellCompat.js';
import {packWidgets} from './gridLayout.js';
import {compactValue, metricLabel} from './metrics.js';
import {GraphBox} from './graph.js';

// One tile: small dim title, value below. Sized to sit in ~120 px cells.
const TILE_STYLE = 'spacing: 2px; padding: 8px 12px;';
const TILE_TITLE_STYLE = 'font-size: 9pt;';

/**
 * A single stat tile: metricLabel id as the title, compactValue(id, m) as
 * the live value. Plain St widget for grid attachment; keep the returned
 * tile's `valueLabel` to update it from metrics updates.
 */
export const StatTile = GObject.registerClass(
class StatTile extends St.BoxLayout {
    /**
     * @param {string} id stat widget ID
     */
    _init(id) {
        super._init({
            orientation: Clutter.Orientation.VERTICAL,
            x_expand: true,
            y_expand: true,
            x_align: Clutter.ActorAlign.FILL,
            style: TILE_STYLE,
        });

        this.id = id;

        this.add_child(new St.Label({
            text: metricLabel(id) ?? id,
            style: TILE_TITLE_STYLE,
        }));

        this.valueLabel = new St.Label({text: '--'});
        this.add_child(this.valueLabel);
    }

    /**
     * Refresh the value text from a metrics payload.
     * @param {Object} m parsed metrics object
     */
    update(m) {
        this.valueLabel.text = compactValue(this.id, m);
    }
});

/**
 * The whole System Stats grid as one popup-menu item (so section rebuilds
 * destroy it like any other row). Packs the configured widgets into a
 * Clutter.GridLayout — the same layout manager GNOME's own Quick Settings
 * header uses — and owns the graph box (history is passed in and survives
 * rebuilds).
 */
export const MetricsGridItem = GObject.registerClass(
class MetricsGridItem extends PopupMenu.PopupBaseMenuItem {
    /**
     * @param {Object} opts
     * @param {{id: string, col_span: number, row_span: number}[]} opts.widgets
     * ordered visible stat widgets with spans (from statsConfig.getStatLayout)
     * @param {number} opts.columns grid column count (1-3)
     * @param {GraphHistory} opts.history usage-graph data source
     * @param {Gio.Settings} opts.settings org.xmax.shell settings, for the
     * graph's live history-window key
     * @param {string} opts.extensionDir unused, reserved
     */
    _init({widgets, columns, history, settings}) {
        super._init({
            reactive: false,
            activate: false,
            hover: false,
            can_focus: false,
        });
        // PopupBaseMenuItem carries the theme's .popup-menu-item padding,
        // which insets the grid more than the Quick Settings controls above
        // it. Zero it so the section spans the popup's full content width —
        // the tiles and graph carry their own inner padding.
        this.style = 'padding: 0;';

        this._columns = columns;

        // Spans are clamped to per-widget size limits at pack time
        // (gridLayout.WIDGET_SIZE_LIMITS) — the graph is the resizable one.
        this._packSpecs = widgets;
        const positions = packWidgets(this._packSpecs, columns);

        const layout = new Clutter.GridLayout();
        this._grid = new St.Widget({
            layout_manager: layout,
            x_expand: true,
            style: 'padding: 0 0 8px 0;',
        });
        this.add_child(this._grid);

        this._tiles = new Map(); // id -> StatTile
        this.graphBox = null;

        const positionById = new Map(positions.map(p => [p.id, p]));
        for (const spec of this._packSpecs) {
            const pos = positionById.get(spec.id);
            if (!pos)
                continue;

            if (spec.id === 'graph') {
                this.graphBox = new GraphBox({
                    title: _('Usage'),
                    unit: '%',
                    series: [
                        {id: 'cpu', label: _('CPU'), color: '#3584e4'},
                        {id: 'gpu', label: _('GPU'), color: '#9141ac'},
                        {id: 'ram', label: _('RAM'), color: '#33d17a'},
                    ],
                    history,
                    windowSeconds: settings.get_int('qs-graph-window'),
                    // Graph height tracks the row span: ~34 px of drawing
                    // area per grid row (2 rows ≈ 68 px … 4 rows ≈ 136 px).
                    height: pos.row_span * 34,
                });
                settings.connectObject('changed::qs-graph-window',
                    () => {
                        this.graphBox.graph.windowSeconds =
                            settings.get_int('qs-graph-window');
                    }, this);
                layout.attach(this.graphBox,
                    pos.column, pos.row, pos.col_span, pos.row_span);
            } else {
                const tile = new StatTile(spec.id);
                this._tiles.set(spec.id, tile);
                layout.attach(tile,
                    pos.column, pos.row, pos.col_span, pos.row_span);
            }
        }
    }

    /** @returns {SystemGraph|null} the graph widget, if configured */
    get graph() {
        return this.graphBox?.graph ?? null;
    }

    /**
     * Refresh all stat tiles from a metrics payload.
     * @param {Object} m parsed metrics object
     */
    updateMetrics(m) {
        for (const tile of this._tiles.values())
            tile.update(m);
    }
});
