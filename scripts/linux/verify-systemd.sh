#!/usr/bin/env bash
# Verify Step 4 (systemd): prove xmaxd runs as a clean systemd service — start,
# assert active, confirm a clean startup log, then stop.
#
# Must run as root. Assumes the unit is installed (scripts/linux/verify-dbus.sh).

set -u

ok()   { printf '  [ok]   %s\n' "$1"; }
miss() { printf '  [warn] %s\n' "$1"; }
fail() { printf '  [FAIL] %s\n' "$1"; }

if [ "$(id -u)" -ne 0 ]; then
    echo "This script must run as root (manages the xmaxd system unit)."
    exit 1
fi

UNIT=xmaxd

echo "== start =="
systemctl start "${UNIT}"

# Wait briefly for Type=dbus to acquire its bus name and settle.
sleep 2

ACTIVE="$(systemctl is-active "${UNIT}")"
if [ "$ACTIVE" = "active" ]; then
    ok "systemctl is-active ${UNIT} = active"
else
    fail "systemctl is-active ${UNIT} = ${ACTIVE}"
fi

echo
echo "== startup log (last 40 lines) =="
journalctl -u "${UNIT}" -n 40 --no-pager | tail -n 40

echo
echo "== clean-startup check =="
if journalctl -u "${UNIT}" --no-pager -b 2>/dev/null | grep -Eq "XmaX Backend ready|D-Bus service started"; then
    ok "clean startup observed (service reached ready state)"
else
    miss "no 'ready' line in log yet (see output above)"
fi

echo
echo "== stop =="
systemctl stop "${UNIT}"
sleep 1
STOPPED="$(systemctl is-active "${UNIT}")"
if [ "$STOPPED" = "inactive" ] || [ "$STOPPED" = "failed" ]; then
    ok "systemctl is-active ${UNIT} = ${STOPPED} after stop"
else
    miss "systemctl is-active ${UNIT} = ${STOPPED} after stop"
fi

echo
echo "==> verify-systemd.sh done."