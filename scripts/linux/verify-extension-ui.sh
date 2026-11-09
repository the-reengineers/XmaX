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
  [ ] XmaX logo appears inside the top-bar Quick Settings button (left of the network/battery icons)
  [ ] "System Stats" section appears in the Quick Settings panel with labeled separator and a tile grid (up to 3 tiles side-by-side)
  [ ] Section text (title, tile labels/values, graph title and legend) uses the standard primary text color — not the dim insensitive grey
  [ ] A 3-line usage graph (CPU/GPU/RAM, %) appears in the System Stats section spanning its row, titled "Usage (%)" with a color legend
  [ ] The graph is full of history when the panel opens (data collected while it was closed), newest sample on the right
  [ ] Graph history window is configurable: prefs → Layout → Graph history (30 s / 1 min / 2 min / 3 min), applies live
  [ ] Column count is configurable: prefs → Layout → Columns (1 / 2 / 3), the QS grid re-flows on the next panel open
  [ ] The graph tile in the preview has a resize button (bottom-right) offering width (2 / Full) and height (2 / 3 / 4 rows); default full × 3
  [ ] Resizing the graph is reflected in the Quick Settings panel (graph height tracks its row span) without re-login
  [ ] Usage graph chart is configurable: prefs → Layout → Chart (Usage % / Temperature °C / Clock MHz / Memory GB / Power W), applies live via GSettings
  [ ] Chart series toggle on/off; at least one series stays enabled
  [ ] Every chart line draws in the system accent color (orange on this setup); the legend symbol shows the line style: ● filled (RAM/Total), ◌ dotted (GPU), ◍ dotted with fill (VRAM), ○ solid (CPU)
  [ ] In the Usage chart the RAM line has a translucent fill under it, the CPU line is solid, and the GPU line is dotted (round dots, not dashes)
  [ ] VRAM lines (Usage and Memory charts) are dotted AND have the translucent fill underneath, like the RAM line
  [ ] Power chart shows CPU, GPU and a Total line (sum of available component power); Memory chart shows RAM and VRAM (when the daemon reports VRAM) on a dynamic axis
  [ ] Clock chart draws CPU and GPU each normalized to their own ceiling (5500/3000 MHz)
  [ ] prefs → Layout: "Quick Settings preview" on the right mirrors the popup's System Stats grid; "Available widgets" list on the left shows hidden widgets
  [ ] Preview tiles have an X close button (top-right) that hides the widget; available tiles have a "+" button that shows it
  [ ] Dragging a preview tile onto another reorders it (drop before/after the target's center); dragging onto the available list hides it
  [ ] Layout changes apply to the real Quick Settings panel without re-login (config.json monitor)
  [ ] Charge-limit QuickMenuToggle is visible in Quick Settings by default (subtitle: 100%)
  [ ] Power-profile control is visible in Quick Settings by default
  [ ] Charge-limit menu shows 85% and 100% options with 100% checked
  [ ] Selecting 85% applies it to hardware and marks it checked
  [ ] `gsettings set org.xmax.shell qs-show-logo false` removes the logo from the Quick Settings button
  [ ] `gsettings set org.xmax.shell qs-metrics false` hides the System Stats section
  [ ] `gsettings set org.xmax.shell show-top-bar-icon true` shows the standalone top-bar logo button
  [ ] `gsettings set org.xmax.shell top-bar-icon-shows-temp true` swaps that button's logo for a °C readout
  [ ] `gsettings set org.xmax.shell qs-charge-limit false` hides the charge-limit control
  [ ] `gsettings set org.xmax.shell qs-power-profile false` hides the power-profile control
  [ ] `gsettings set org.xmax.shell show-top-bar-icon false` hides the standalone indicator
EOF

echo
echo "ui verification OK"