#!/usr/bin/env bash
# Verify Step 4 (D-Bus + polkit): install the D-Bus policy + polkit actions +
# systemd unit, start org.xmax.Service on the system bus, and exercise the
# interface over the bus.
#
# Must run as root. Idempotent: copies files over any previous install.

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PACK="${REPO_ROOT}/packaging/linux"
BUILD="${REPO_ROOT}/backend/build-linux"

ok()   { printf '  [ok]   %s\n' "$1"; }
miss() { printf '  [warn] %s\n' "$1"; }
fail() { printf '  [FAIL] %s\n' "$1"; }

if [ "$(id -u)" -ne 0 ]; then
    echo "This script must run as root (installs to /usr, starts a system unit)."
    exit 1
fi

echo "==> Repo root: ${REPO_ROOT}"

# --- 1. Install artifacts ---------------------------------------------------
echo
echo "== install (system-wide) =="
if [ ! -x "${BUILD}/xmaxd" ]; then
    fail "xmaxd not built (run scripts/linux/verify-build.sh first)"; exit 1
fi

install -m 0755 "${BUILD}/xmaxd" /usr/local/bin/xmaxd && ok "xmaxd -> /usr/local/bin/xmaxd"

install -m 0644 "${PACK}/org.xmax.policy" /usr/share/polkit-1/actions/org.xmax.policy && ok "polkit actions installed"
install -m 0644 "${PACK}/org.xmax.Service.conf" /usr/share/dbus-1/system.d/org.xmax.Service.conf && ok "D-Bus system policy installed"
# Neutralize legacy /etc installs — dbus-broker loads BOTH /usr/share and
# /etc drop-in dirs, so a stale /etc copy would shadow (and can outlive)
# the /usr/share one.
#
# Overwrite it with an empty stub rather than deleting it: dbus-broker-
# launch keeps a file watch on every drop-in it parsed, and DELETING a
# watched file makes every later reload abort with "Invalid configuration,
# ignored" — the broker then keeps serving its boot-time policy
# indefinitely (root still passes via the uid-0 privileged fallback, so
# root-only checks look green while user calls fail). An existing, valid,
# empty file reloads cleanly and stops shadowing the /usr/share policy.
#
# NEVER restart dbus-broker to apply a policy change — on Fedora GNOME
# Wayland it hard-crashes the session. If policy appears stale after this
# script, reboot instead.
if [ -e /etc/dbus-1/system.d/org.xmax.Service.conf ]; then
    printf '%s\n' \
        '<?xml version="1.0" encoding="UTF-8"?>' \
        '<!-- Empty stub: neutralizes a legacy install of this drop-in. -->' \
        '<busconfig/>' \
        > /etc/dbus-1/system.d/org.xmax.Service.conf
    ok "neutralized legacy /etc D-Bus policy (empty stub)"
fi
install -m 0644 "${PACK}/xmaxd.service" /etc/systemd/system/xmaxd.service && ok "systemd unit installed"

# --- 2. Reload policy + bring the service up --------------------------------
echo
echo "== reload & start =="
# dbus-broker reloads .conf drop-ins on SIGHUP; dbus-daemon with systemctl reload.
systemctl try-reload dbus-broker.service 2>/dev/null || systemctl reload dbus.service 2>/dev/null || miss "could not reload D-Bus (policy may need a reboot)"
systemctl daemon-reload
# Enable so the daemon survives reboots — otherwise the extension reports
# "daemon is not running" after every restart of the machine.
systemctl enable xmaxd >/dev/null 2>&1 || true
systemctl restart xmaxd

# --- 3. Introspect + basic calls --------------------------------------------
echo
echo "== org.xmax.Service introspection =="
if busctl --system introspect org.xmax.Service /org/xmax/Service >/dev/null 2>&1; then
    ok "introspect: bus name + path present"
    for m in Ping GetMetrics SetTdp SetFan SetChargeLimit GetChargeLimit SetAutoStart; do
        if busctl --system introspect org.xmax.Service /org/xmax/Service 2>/dev/null | grep -q "\.$m"; then
            ok "method: $m"
        else
            miss "method: $m (not listed)"
        fi
    done
else
    fail "org.xmax.Service is not reachable on the system bus"
    echo "  -> check: journalctl -u xmaxd ; systemctl status xmaxd"
    exit 1
fi

echo
echo "== Ping =="
if busctl --system call org.xmax.Service /org/xmax/Service org.xmax.Service Ping >/dev/null 2>&1; then
    ok "Ping returned"
else
    fail "Ping failed"
fi

# Resolve an unprivileged test user up-front: needed both for the bus-policy
# assertion below and the polkit denial check at the end.
TEST_USER="${SUDO_USER:-}"
if [ -z "$TEST_USER" ]; then
    TEST_USER="$(logname 2>/dev/null || true)"
fi
if [ -z "$TEST_USER" ] || [ "$TEST_USER" = "root" ]; then
    TEST_USER="$(getent passwd | awk -F: '$3>=1000 && $3<65534 {print $1; exit}')"
fi

echo
echo "== bus policy: unprivileged access =="
# A bus-level denial is indistinguishable from a polkit denial in the SetTdp
# check at the end (both just make the call fail), so assert the D-Bus policy
# actually lets a regular user talk to the service BEFORE trusting that check.
# Ping is not polkit-gated; if it fails here the policy on the running broker
# is stale or broken, not "authorization working".
if [ -z "$TEST_USER" ]; then
    miss "no unprivileged user found; skip bus-policy assertion"
elif runuser -u "$TEST_USER" -- busctl --system call \
        org.xmax.Service /org/xmax/Service org.xmax.Service Ping >/dev/null 2>&1; then
    ok "Ping as unprivileged user $TEST_USER (bus policy active)"
    BUS_POLICY_OK=1
else
    fail "Ping denied for $TEST_USER at the bus level"
    echo "  -> the installed policy is not what the broker is enforcing."
    echo "     reboot to make the broker re-parse its policy from disk"
fi

echo
echo "== GetMetrics =="
METRICS=$(busctl --system call org.xmax.Service /org/xmax/Service org.xmax.Service GetMetrics 2>/dev/null || true)
if [ -n "$METRICS" ]; then
    ok "GetMetrics returned a payload (${#METRICS} bytes)"
else
    miss "GetMetrics returned empty"
fi

echo
echo "== polkit: SetTdp as unprivileged user =="
# Prove the privileged write is gated. This only means something if the
# bus-policy assertion above passed — otherwise a bus-level denial is
# indistinguishable from a polkit denial (see the earlier check).
if [ -z "$TEST_USER" ]; then
    miss "no unprivileged user found; skip denial assertion"
elif [ "${BUS_POLICY_OK:-0}" -ne 1 ]; then
    miss "skipped: unprivileged Ping failed above, so a SetTdp denial would not prove polkit"
else
    if runuser -u "$TEST_USER" -- busctl --system call \
        org.xmax.Service /org/xmax/Service org.xmax.Service SetTdp a{sv} 3 stapm u 45000 fast u 50000 slow u 45000 \
        >/dev/null 2>&1; then
        fail "SetTdp succeeded as $TEST_USER (authorization not enforced)"
    else
        ok "SetTdp denied for unprivileged user $TEST_USER (polkit gating active)"
        echo "  -> interactive authorization path: run 'pkcheck --action-id org.xmax.set-tdp' in a session"
    fi
fi

echo
echo "==> verify-dbus.sh done (service left running for verify-systemd.sh / verify-xmaxctl.sh)."