#!/usr/bin/env gjs -m
// extension-client-check.mjs — exercise the extension's REAL D-Bus client
// (frontend/gnome/xmax@local/dbus.js, the same module GNOME Shell imports)
// against the live daemon. This catches wrong-bus / auth regressions that a
// "service active" or "extension ACTIVE" check cannot see — e.g. the client
// defaulting to the session bus while the daemon runs on the system bus, or
// sd-bus denying non-root senders (SD_BUS_VTABLE_UNPRIVILEGED).
//
// Usage:  gjs -m scripts/linux/extension-client-check.mjs <extension-dir>
// Bus selection follows the client's own rules: XMAX_DBUS_BUS=user -> session
// bus (nested dev mode), otherwise the system bus (production / dev-sys).
// Exit code 0 = the client can ping, read metrics, and list profiles.

import System from 'system';
import GLib from 'gi://GLib';

const die = (msg) => {
    console.error(`[ext-client-check] FAIL: ${msg}`);
    System.exit(1);
};

let extDir = System.programArgs[0];
if (!extDir)
    die('usage: gjs -m extension-client-check.mjs <extension-dir>');
// gjs dynamic import needs an absolute file:// URI; resolve relative paths
// against the cwd (gjs itself mangles "file://<relative>").
if (!extDir.startsWith('/'))
    extDir = `${GLib.get_current_dir()}/${extDir}`;

// gjs needs a file:// URI for dynamic import of an absolute path.
let mod;
try {
    mod = await import(`file://${extDir.replace(/\/+$/, '')}/dbus.js`);
} catch (e) {
    die(`cannot import dbus.js from ${extDir}: ${e}`);
}

const client = new mod.XmaxClient();

if (!client.init())
    die('Ping failed — daemon unreachable on this bus. ' +
        'Run `just dev-sys`, then retry (no re-login needed for this check).');

const metrics = client.getMetrics();
const temp = metrics?.cpu?.temp_c;
if (typeof temp !== 'number')
    die(`GetMetrics returned no numeric cpu.temp_c: ${JSON.stringify(metrics)}`);

const profiles = client.getProfiles();
if (!Array.isArray(profiles) || profiles.length === 0)
    die('GetProfiles returned no profiles');

// Charge-limit read is hardware-dependent (the register lives in the
// vendor EC region beyond the 256-byte ACPI EC window on some machines) —
// report but don't fail on it.
let limitNote = 'charge limit: unavailable (sensor_unavailable)';
try {
    const limit = client.getChargeLimit();
    if (typeof limit === 'number')
        limitNote = `charge limit ${limit}%`;
} catch (e) {
    // reported via the default note above
}

console.log(`[ext-client-check] OK: ping, metrics (cpu ${temp} °C), ` +
    `${profiles.length} profiles (${profiles.map(p => p.name).join(', ')}), ` +
    limitNote);
