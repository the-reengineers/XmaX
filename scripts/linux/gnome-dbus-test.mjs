// gnome-dbus-test.mjs — unit test for frontend/gnome/xmax@local/dbus.js.
// Runs under `gjs -m` against a private session bus where xmaxd has already
// registered org.xmax.Service (XMAX_DBUS_BUS=user). Exercises the read path,
// D-Bus error mapping, and the MetricsChanged signal subscription.
//
// Exit code 0 = pass, 1 = fail.

import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import system from 'system';
import { XmaxClient } from '../../frontend/gnome/xmax@local/dbus.js';

let passCount = 0;
let failCount = 0;

function check(name, ok, detail = '') {
    if (ok) {
        passCount++;
        console.log(`ok   - ${name}`);
    } else {
        failCount++;
        console.log(`FAIL - ${name}${detail ? ' :: ' + detail : ''}`);
    }
}

const client = new XmaxClient();

// 1. Connect + read path.
const inited = client.init();
check('init connects to org.xmax.Service', inited === true);
check('available flag is set', client.available === true);

if (inited) {
    try {
        client.ping();
        check('Ping round-trips', true);
    } catch (e) {
        check('Ping round-trips', false, e.message);
    }

    try {
        const metrics = client.getMetrics();
        check('GetMetrics returns an object', metrics && typeof metrics === 'object');
        check('  metrics.cpu present', !!(metrics && metrics.cpu));
        check('  metrics.cpu.util_pct is a number', typeof metrics?.cpu?.util_pct === 'number');
        check('  metrics.power present', !!(metrics && metrics.power));
    } catch (e) {
        check('GetMetrics returns an object', false, e.message);
    }

    // Writes are gated by session persist; a fresh daemon rejects them with a
    // D-Bus error (not a crash), proving request/response + error mapping.
    let writesRejected = false;
    try {
        client.setFan('auto');
    } catch (e) {
        writesRejected = true;
        console.log(`  info: SetFan rejected as expected (${e.message})`);
    }
    check('SetFan produces a well-formed D-Bus error', writesRejected);
}

// 2. Signal subscription: daemon emits MetricsChanged ~every 2s.
let signalSeen = false;
client._onMetricsChanged = () => {
    signalSeen = true;
};
client._subscribeMetrics();

const loop = new GLib.MainLoop(null, false);
const deadlineTimer = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 6000, () => {
    loop.quit();
    return GLib.SOURCE_REMOVE;
});
const pollTimer = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 100, () => {
    if (signalSeen) {
        loop.quit();
        return GLib.SOURCE_REMOVE;
    }
    return GLib.SOURCE_CONTINUE;
});

loop.run();
// Whichever timer fired auto-removed itself (SOURCE_REMOVE); remove the other.
if (signalSeen) {
    GLib.source_remove(deadlineTimer);
} else {
    GLib.source_remove(pollTimer);
}
check('MetricsChanged signal received within 6s', signalSeen);

client.destroy();

console.log(`\n${passCount} passed, ${failCount} failed`);
system.exit(failCount === 0 ? 0 : 1);