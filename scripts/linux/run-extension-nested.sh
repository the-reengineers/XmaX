#!/usr/bin/env bash
# run-extension-nested.sh — boot a *private* GNOME Shell alongside a session-bus
# xmaxd and verify the extension loads cleanly: no root, no systemd unit, no
# polkit, and no need to disturb the running session. The daemon and the
# extension agree on the *session* bus via XMAX_DBUS_BUS=user (xmaxctl stays on
# the system bus, so this path is FE-focused).
#
# GNOME Shell 50 removed the --nested flag and made --wayland a full display
# server (taking over DRM -> EBUSY against a live session), so there is no
# longer a visible nested window. `--headless --virtual-monitor` gives a private
# Meta compositor on a render node: enough to load the extension (State: ACTIVE)
# and exercise its D-Bus client end-to-end, but with no pixels to click. For
# visual testing, reload the main session's shell (log out/in; on X11 Alt+F2 -> r).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
DAEMON="${XMAXD:-$ROOT/backend/build-linux/xmaxd}"

if ! command -v gnome-shell >/dev/null 2>&1; then
    echo "gnome-shell not found." >&2
    exit 1
fi
if [ ! -x "$DAEMON" ]; then
    echo "xmaxd not built at $DAEMON" >&2
    echo "build it first: just build-be   (or scripts/linux/verify-build.sh)" >&2
    exit 1
fi

export XMAX_DAEMON="$DAEMON"
exec dbus-run-session -- bash -c '
    set -u
    export XMAX_DBUS_BUS=user XMAX_NO_POLKIT=1

    "$XMAX_DAEMON" &
    daemon_pid=$!
    shell_pid=""
    log="$(mktemp)"

    cleanup() {
        [ -z "$shell_pid" ] || kill "$shell_pid" 2>/dev/null || true
        kill "$daemon_pid" 2>/dev/null || true
        rm -f "$log"
    }
    trap cleanup EXIT

    sleep 1   # let the daemon register org.xmax.Service on the session bus

    echo "private GNOME Shell starting (headless, virtual monitor 1280x720)…" >&2
    gnome-shell --headless --virtual-monitor=1280x720 >"$log" 2>&1 &
    shell_pid=$!

    # Wait up to ~15s for the private shell to own org.gnome.Shell.
    if ! gdbus wait --session --timeout 15 org.gnome.Shell >/dev/null 2>&1; then
        echo "timed out waiting for the private org.gnome.Shell" >&2
        tail -n 40 "$log" >&2 || true
        exit 1
    fi

    # Give the shell a beat to instantiate the enabled extensions before probing.
    sleep 2

    info="$(gnome-extensions info xmax@local 2>&1 || true)"
    printf "%s\n" "$info"

    if ! printf "%s\n" "$info" | grep -Eq "^ *State: ACTIVE"; then
        echo "xmax@local did not reach ACTIVE — shell log tail:" >&2
        grep -i "xmax@local\|Extension" "$log" | tail -n 40 >&2 || tail -n 40 "$log" >&2
        exit 1
    fi

    echo "xmax@local loaded cleanly (State: ACTIVE) on the private session bus." >&2
'