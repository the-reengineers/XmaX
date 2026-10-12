#!/usr/bin/env bash
# Probe for libryzenadj -- the library the backend dlopen()s to control STAPM /
# Fast / Slow TDP limits. Reports presence and, when the library loads, reads the
# current limits (read-only: no limit is ever changed).
#
# Durable dev probe. Run as root when validating on hardware: libryzenadj's own
# SMU backend needs root to touch the MP1 mailbox. Unprivileged runs still show
# library/CLI presence.

set -u

ok()   { printf '  [ok]   %s\n' "$1"; }
miss() { printf '  [warn] %s\n' "$1"; }

lib_from_ldconfig() {
    for name in libryzenadj.so libryzenadj.so.6 libryzenadj.so.5 libryzenadj.so.4 libryzenadj.so.1; do
        p=$(ldconfig -p 2>/dev/null | awk -v n="$name" '$1==n {print $NF; exit}')
        if [ -n "$p" ]; then printf '%s' "$p"; return 0; fi
    done
    return 1
}

echo "== library search =="
if [ -n "${XMAX_RYZENADJ_LIB:-}" ]; then
    if [ -e "$XMAX_RYZENADJ_LIB" ]; then ok "XMAX_RYZENADJ_LIB=$XMAX_RYZENADJ_LIB"
    else miss "XMAX_RYZENADJ_LIB=$XMAX_RYZENADJ_LIB (file missing)"; fi
fi

LIB=""
if p=$(lib_from_ldconfig); then
    ok "ldconfig: $p"
    LIB="$p"
else
    miss "libryzenadj not found by ldconfig (install ryzenadj)"
fi

echo
echo "== ryzenadj CLI =="
if command -v ryzenadj >/dev/null 2>&1; then ok "ryzenadj CLI: $(command -v ryzenadj)"
else miss "ryzenadj CLI not installed"; fi

echo
echo "== current TDP limits (read-only, via ctypes) =="
if command -v python3 >/dev/null 2>&1 && [ -n "$LIB" ]; then
    XMAX_RYZENADJ_LIB="$LIB" python3 - <<'PY'
import ctypes, os, sys
path = os.environ["XMAX_RYZENADJ_LIB"]
try:
    lib = ctypes.CDLL(path)
    lib.init_ryzenadj.restype = ctypes.c_int
    if lib.init_ryzenadj() != 0:
        print("  [warn] init_ryzenadj() failed (run as root?)")
        sys.exit(0)
    for name in ("stapm", "fast", "slow"):
        fn = getattr(lib, f"get_{name}_limit")
        fn.restype = ctypes.c_int
        v = fn()
        if v < 0:
            print(f"  [warn] get_{name}_limit() -> {v} (unsupported on this chip)")
        else:
            print(f"  [ok]   {name}_limit = {v} mW ({v/1000:.2f} W)")
except OSError as e:
    print(f"  [warn] could not load {path}: {e}")
PY
else
    miss "cannot read limits (python3 or library missing)"
fi

exit 0