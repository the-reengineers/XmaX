#!/usr/bin/env bash
# verify-extension-ui.sh — headless fallback: exercise the extension's D-Bus
# client (dbus.js) against a live xmaxd on a private session bus with `gjs -m`,
# then print a manual screenshot checklist. On a GNOME desktop, prefer the live
# nested-shell path instead (scripts/linux/run-extension-nested.sh / just dev-gui);
# this script remains for no-display/CI runs where gnome-shell can't launch.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
DAEMON="${XMAXD:-$ROOT/backend/build-linux/xmaxd}"
TEST="$ROOT/scripts/linux/gnome-dbus-test.mjs"

if [[ ! -x "$DAEMON" ]]; then
    echo "xmaxd not found at $DAEMON" >&2
    echo "build it first: scripts/linux/verify-build.sh" >&2
    exit 1
fi

echo "== dbus.js client test (gjs -m, private session bus) =="
dbus-run-session -- bash -s "$DAEMON" "$TEST" <<'INNER'
export XMAX_DBUS_BUS=user XMAX_NO_POLKIT=1
"$1" >/tmp/xmaxd-ui.log 2>&1 &
pid=$!
sleep 1
gjs -m "$2"
rc=$?
kill -TERM "$pid" 2>/dev/null || true
wait "$pid" 2>/dev/null || true
exit "$rc"
INNER

cat <<'EOF'

Manual UI checklist (in a live GNOME session, after `gnome-extensions enable xmax@local`):
  [ ] Top-bar XmaX icon appears
  [ ] Clicking it opens a popup with CPU / GPU / RAM / Fan / Power / Charge readouts
  [ ] `gsettings set org.xmax.shell top-bar-icon-shows-temp true` swaps the icon for a °C readout
  [ ] Charge-limit QuickMenuToggle is visible in Quick Settings by default (subtitle: 100%)
  [ ] Power-profile control is visible in Quick Settings by default
  [ ] Charge-limit menu shows 85% and 100% options with 100% checked
  [ ] Selecting 85% applies it to hardware and marks it checked
  [ ] `gsettings set org.xmax.shell qs-charge-limit false` hides the charge-limit control
  [ ] `gsettings set org.xmax.shell qs-power-profile false` hides the power-profile control
  [ ] `gsettings set org.xmax.shell show-top-bar-icon false` hides the indicator
EOF

echo
echo "ui verification OK"