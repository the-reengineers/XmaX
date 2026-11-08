// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
// dbus.js — D-Bus client for org.xmax.Service (the XmaX daemon). Imports only
// Gio/GLib so it runs unchanged in both the shell and a plain `gjs -m` harness
// (synchronous `call_sync` keeps the test main-loop-free; signal delivery is via
// GDBusConnection.signal_subscribe, which needs a running main context — present
// in GNOME Shell, driven by a GLib.MainLoop in tests).

import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

const SERVICE_NAME = 'org.xmax.Service';
const SERVICE_PATH = '/org/xmax/Service';
const SERVICE_IFACE = 'org.xmax.Service';

const CALL_TIMEOUT = 2000; // ms

export class XmaxClient {
    /**
     * @param {Object} [opts]
     * @param {Function|null} [opts.onMetricsChanged] invoked with a parsed metrics object
     * @param {Gio.DBusConnection} [opts.connection] defaults to the system bus
     */
    constructor({ onMetricsChanged = null, connection = null } = {}) {
        // The daemon runs as a root systemd service on the *system* bus
        // (production wiring, `just dev-sys`). XMAX_DBUS_BUS=user overrides
        // to the session bus for the nested-shell dev mode
        // (scripts/linux/run-extension-nested.sh), matching the same env
        // var the daemon itself understands.
        if (connection) {
            this._conn = connection;
        } else if (GLib.getenv('XMAX_DBUS_BUS') === 'user') {
            this._conn = Gio.DBus.session;
        } else {
            this._conn = Gio.DBus.system;
        }
        this._onMetricsChanged = onMetricsChanged;
        this._sub = 0;
        this._available = false;
    }

    get available() {
        return this._available;
    }

    get version() {
        return this._readString('Version');
    }

    get autoStartEnabled() {
        return this._readBoolean('AutoStartEnabled');
    }

    /**
     * Whether the daemon may write to hardware. While off, every hardware
     * write (setTdp/setFan/setChargeLimit/setProfile) is rejected with
     * persist_disabled; turning it on applies all configured settings.
     * @returns {boolean}
     */
    get sessionPersist() {
        return this._readBoolean('SessionPersist');
    }

    /**
     * Gate hardware writes. Mirrors the transport `set_session_persist`
     * command (the "apply" toggle in the Windows frontend).
     * @param {boolean} enabled
     */
    setSessionPersist(enabled) {
        this._callSync('SetSessionPersist', new GLib.Variant('(b)', [enabled]));
    }

    /**
     * Probe the daemon and subscribe to MetricsChanged. Returns true if the
     * service answered Ping.
     */
    init() {
        try {
            this.ping();
            this._available = true;
        } catch (e) {
            this._available = false;
        }
        this._subscribeMetrics();
        return this._available;
    }

    _callSync(method, parameters = null, flags = Gio.DBusCallFlags.NONE, iface = SERVICE_IFACE) {
        return this._conn.call_sync(
            SERVICE_NAME,
            SERVICE_PATH,
            iface,
            method,
            parameters,
            null,
            flags,
            CALL_TIMEOUT,
            null,
        );
    }

    ping() {
        this._callSync('Ping');
    }

    /** @returns {Object} parsed metrics object */
    getMetrics() {
        const ret = this._callSync('GetMetrics');
        const [jsonStr] = ret.deep_unpack();
        return JSON.parse(jsonStr);
    }

    setTdp(stapmW, fastW, slowW) {
        const params = new GLib.Variant('(a{sv})', [
            {
                stapm: GLib.Variant.new_uint32(stapmW),
                fast: GLib.Variant.new_uint32(fastW),
                slow: GLib.Variant.new_uint32(slowW),
            },
        ]);
        this._callSync('SetTdp', params);
    }

    setFan(mode, curveId = null) {
        const dict = { mode: GLib.Variant.new_string(mode) };
        if (curveId) dict.curve_id = GLib.Variant.new_string(curveId);
        this._callSync('SetFan', new GLib.Variant('(a{sv})', [dict]));
    }

    /** @returns {number} current charge limit percent (0 if unknown) */
    getChargeLimit() {
        const ret = this._callSync('GetChargeLimit');
        const [percent] = ret.deep_unpack();
        return percent;
    }

    setChargeLimit(percent) {
        this._callSync('SetChargeLimit', new GLib.Variant('(u)', [percent]));
    }

    setAutoStart(enabled) {
        this._callSync('SetAutoStart', new GLib.Variant('(b)', [enabled]));
    }

    // ---- Profiles -----------------------------------------------------------

    /**
     * Fetch all profiles (fixed + adaptive).
     * @returns {Object[]} array of profile objects
     */
    getProfiles() {
        const ret = this._callSync('GetProfiles');
        const [jsonStr] = ret.deep_unpack();
        const data = JSON.parse(jsonStr);
        return data.profiles || [];
    }

    /**
     * Apply a profile to hardware by slug ID.
     * @param {string} id profile slug
     */
    setProfile(id) {
        this._callSync('SetProfile', new GLib.Variant('(s)', [id]));
    }

    /**
     * Create or update a profile.
     * @param {Object} profile profile object (id, name, type, tdp/tuning fields, etc.)
     */
    saveProfile(profile) {
        this._callSync('SaveProfile', new GLib.Variant('(s)', [JSON.stringify(profile)]));
    }

    /**
     * Delete a profile by slug ID.
     * @param {string} id profile slug
     */
    deleteProfile(id) {
        this._callSync('DeleteProfile', new GLib.Variant('(s)', [id]));
    }

    // ---- Fan curves ---------------------------------------------------------

    /**
     * Fetch all fan curves (builtin + custom).
     * @returns {Object[]} array of fan curve objects
     */
    getFanCurves() {
        const ret = this._callSync('GetFanCurves');
        const [jsonStr] = ret.deep_unpack();
        const data = JSON.parse(jsonStr);
        return data.fan_curves || [];
    }

    /**
     * Create or update a fan curve.
     * @param {Object} curve curve object (id, name, points[])
     */
    saveFanCurve(curve) {
        this._callSync('SaveFanCurve', new GLib.Variant('(s)', [JSON.stringify(curve)]));
    }

    /**
     * Delete a fan curve by slug ID.
     * @param {string} id curve slug
     */
    deleteFanCurve(id) {
        this._callSync('DeleteFanCurve', new GLib.Variant('(s)', [id]));
    }

    // ---- properties ---------------------------------------------------------

    _prop(key, iface = SERVICE_IFACE, connection = null) {
        const ret = (connection ?? this._conn).call_sync(
            SERVICE_NAME,
            SERVICE_PATH,
            'org.freedesktop.DBus.Properties',
            'Get',
            new GLib.Variant('(ss)', [iface, key]),
            null,
            Gio.DBusCallFlags.NONE,
            CALL_TIMEOUT,
            null,
        );
        // Get returns (v) — a single-element tuple containing a variant.
        // deep_unpack() unpacks the outer tuple but leaves the variant
        // wrapper intact (GJS limitation).  Unwrap it ourselves.
        return ret.get_child_value(0).get_variant().unpack();
    }

    _readString(key) {
        try {
            return this._prop(key);
        } catch {
            return null;
        }
    }

    _readBoolean(key) {
        try {
            return this._prop(key) === true;
        } catch {
            return false;
        }
    }

    // ---- MetricsChanged signal ----------------------------------------------

    _subscribeMetrics() {
        if (this._sub !== 0) return;
        const cb = (conn, senderName, objectPath, iface, signalName, parameters) => {
            if (signalName !== 'MetricsChanged' || !this._onMetricsChanged) return;
            // GJS deep_unpack() collapses a{sv} variant values to {}, so pull
            // the 'metrics' string out of the emitted a{sv} explicitly.
            let raw = null;
            try {
                const dict = parameters.get_child_value(0); // a{sv}
                const v = dict.lookup_value('metrics', null); // s (auto-unwrapped)
                if (v) raw = v.unpack();
            } catch {
                return;
            }
            if (!raw) return;
            try {
                this._onMetricsChanged(JSON.parse(raw));
            } catch {
                // Ignore malformed payload.
            }
        };
        this._sub = this._conn.signal_subscribe(
            SERVICE_NAME,
            SERVICE_IFACE,
            'MetricsChanged',
            SERVICE_PATH,
            null,
            Gio.DBusSignalFlags.NONE,
            cb,
        );
    }

    destroy() {
        if (this._sub !== 0) {
            this._conn.signal_unsubscribe(this._sub);
            this._sub = 0;
        }
    }
}