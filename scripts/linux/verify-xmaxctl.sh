#!/usr/bin/env bash
# Verify Step 4 (xmaxctl): smoke-test the CLI against the running service.
# Reads happen as any user; the TDP write round-trip and auto-start toggle need
# root/polkit (run as root or through the authorization prompt).
#
# Assumes org.xmax.Service is already up (verify-dbus.sh / verify-systemd.sh).

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
BUILD="${REPO_ROOT}/backend/build-linux"
CTL="${BUILD}/xmaxctl"

ok()   { printf '  [ok]   %s\n' "$1"; }
miss() { printf '  [warn] %s\n' "$1"; }
fail() { printf '  [FAIL] %s\n' "$1"; }

if [ ! -x "${CTL}" ]; then
    echo "xmaxctl not built (run scripts/linux/verify-build.sh first)"; exit 1
fi

echo "== version =="
V=$("${CTL}" version 2>&1) && ok "version: ${V}" || fail "version failed: ${V}"

echo
echo "== metrics (read) =="
if M=$("${CTL}" metrics 2>&1); then
    ok "metrics returned (${#M} bytes)"
else
    # The CLI falls back to the AF_UNIX socket; still report the outcome.
    fail "metrics failed: ${M}"
fi

echo
echo "== tdp round-trip (write 45/50/45, then read) =="
if "${CTL}" tdp 45 50 45 >/dev/null 2>&1; then
    ok "tdp write 45/50/45 accepted"
    if "${CTL}" metrics >/dev/null 2>&1; then
        ok "metrics read after tdp write"
    else
        miss "metrics read after tdp write failed"
    fi
else
    miss "tdp write 45/50/45 denied (expected without root/polkit auth)"
fi

echo
echo "== auto-start toggle =="
PREV=$("${CTL}" auto-start 2>&1 || true)
ok "auto-start state: ${PREV:-unknown}"
if "${CTL}" auto-start off >/dev/null 2>&1 && "${CTL}" auto-start on >/dev/null 2>&1; then
    ok "auto-start off/on toggled"
else
    miss "auto-start toggle denied (expected without root/polkit auth)"
fi

echo
echo "==> verify-xmaxctl.sh done."