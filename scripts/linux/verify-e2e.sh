#!/usr/bin/env bash
# verify-e2e.sh — end-to-end smoke test across the whole Linux stack (Steps 0–5).
#
# Orchestrates the per-step verify scripts in dependency order and prints a
# green/red summary per phase:
#   build + ctest -> extension schema -> gjs D-Bus client -> D-Bus install +
#   introspect + polkit -> xmaxctl round-trips -> systemd start/stop ->
#   extension install.
#
# Phase gating (each phase records pass / fail / skip):
#   * build, extension schema, and the gjs D-Bus client run unprivileged.
#   * D-Bus install (+ polkit), xmaxctl control round-trips, and the systemd
#     start/stop cycle require root (they install a system unit + start a bus
#     service), so they are recorded as SKIP when run unprivileged.
#   * The GNOME extension install requires a live GNOME Shell session (it talks
#     to org.gnome.Shell.Extensions on the session bus) and is SKIP otherwise.
#
# Run the full backend stack as:
#     sudo scripts/linux/verify-e2e.sh
# and run the extension-install phase from inside a GNOME session.
#
# Exit 0 = no failed phase; 1 = at least one phase failed.

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

pass_count=0
fail_count=0
skip_count=0
summary_lines=()

track() {  # track <pass|fail|skip> <label> [detail]
    local status="$1" label="$2" detail="${3:-}"
    case "$status" in
        pass) pass_count=$((pass_count + 1)) ;;
        fail) fail_count=$((fail_count + 1)) ;;
        skip) skip_count=$((skip_count + 1)) ;;
    esac
    summary_lines+=( "$(printf '  [%-4s] %-18s %s' "$status" "$label" "$detail")" )
}

run() {  # run <label> <cmd...> ; records pass/fail, returns the cmd exit status
    local label="$1"; shift
    if "$@"; then track pass "$label" ""; return 0
    else         track fail "$label" ""; return 1; fi
}

skip_phase() { track skip "$1" "$2"; }

is_root=false
[ "$(id -u)" -eq 0 ] && is_root=true

# Live-session detection: the extension install needs org.gnome.Shell on the
# session bus (not present over SSH / a bare tty).
has_session=false
if command -v gnome-extensions >/dev/null 2>&1; then
    if busctl --user introspect org.gnome.Shell /org/gnome/Shell >/dev/null 2>&1 \
       || gdbus call --session --dest org.gnome.Shell --object-path /org/gnome/Shell \
            --method org.freedesktop.DBus.Peer.Ping >/dev/null 2>&1; then
        has_session=true
    fi
fi

has_gjs=false
command -v gjs >/dev/null 2>&1 && has_gjs=true

echo "==================================================================="
echo " XmaX Linux end-to-end verification"
echo "   root      : $is_root"
echo "   session   : $has_session"
echo "   gjs       : $has_gjs"
echo "==================================================================="
echo

# ---- 1/7 build + ctest (unprivileged) --------------------------------------
echo "== Phase 1/7: build + ctest =="
if run build "${SCRIPT_DIR}/verify-build.sh"; then BUILD_OK=1; else BUILD_OK=0; fi
echo

# ---- 2/7 extension schema (unprivileged; no binary needed) -----------------
echo "== Phase 2/7: extension schema =="
run extension-schema "${SCRIPT_DIR}/verify-extension-schema.sh"
echo

# ---- 3/7 gjs D-Bus client (unprivileged; needs xmaxd + gjs) ----------------
echo "== Phase 3/7: extension D-Bus client (gjs) =="
if [ "$BUILD_OK" = 1 ] && $has_gjs; then
    run extension-ui "${SCRIPT_DIR}/verify-extension-ui.sh"
elif [ "$BUILD_OK" != 1 ]; then
    skip_phase extension-ui "needs a successful build"
else
    skip_phase extension-ui "gjs not installed"
fi
echo

# ---- 4/7 D-Bus install + introspect + polkit (root) ------------------------
echo "== Phase 4/7: D-Bus install + introspect + polkit =="
DBUS_OK=0
if $is_root && [ "$BUILD_OK" = 1 ]; then
    if run dbus "${SCRIPT_DIR}/verify-dbus.sh"; then DBUS_OK=1; fi
else
    skip_phase dbus "needs root (sudo ${BASH_SOURCE[0]##*/})"
fi
echo

# ---- 5/7 xmaxctl control/metrcis round-trips (service up via D-Bus phase) --
echo "== Phase 5/7: xmaxctl round-trips =="
if [ "$DBUS_OK" = 1 ]; then
    run xmaxctl "${SCRIPT_DIR}/verify-xmaxctl.sh"
else
    skip_phase xmaxctl "needs the D-Bus phase (root)"
fi
echo

# ---- 6/7 systemd start/stop (root; leaves the unit stopped) ----------------
echo "== Phase 6/7: systemd start/stop =="
if [ "$DBUS_OK" = 1 ]; then
    run systemd "${SCRIPT_DIR}/verify-systemd.sh"
else
    skip_phase systemd "needs the D-Bus phase (root)"
fi
echo

# ---- 7/7 extension install (live GNOME session) ----------------------------
echo "== Phase 7/7: extension install =="
if $has_session; then
    run extension-install "${SCRIPT_DIR}/verify-extension-install.sh"
else
    skip_phase extension-install "no live GNOME session"
fi
echo

# ---- Summary ----------------------------------------------------------------
echo "==================================================================="
echo " Phase summary"
echo "==================================================================="
for line in "${summary_lines[@]}"; do printf '%s\n' "$line"; done
echo "-------------------------------------------------------------------"
printf '  %d passed, %d failed, %d skipped\n' "$pass_count" "$fail_count" "$skip_count"
if [ "$fail_count" -eq 0 ]; then
    echo "  RESULT: PASS"
    exit 0
else
    echo "  RESULT: FAIL"
    exit 1
fi