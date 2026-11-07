#!/usr/bin/env bash
# install-smu-driver.sh — install the vendored ryzen_smu kernel module (see
# backend/lib/ryzen_smu/VENDOR.md) via DKMS, signed for Secure Boot.
#
# The module is the *only* supported SMU access path:
#   - Secure Boot enabled:  /dev/mem is blocked by kernel lockdown, so the
#     module (DKMS-built, signed with the local MOK key) is the only way to
#     control TDP. Requires a one-time MOK enrollment + reboot.
#   - Secure Boot disabled: the module loads unsigned — same install, minus
#     the MOK step, no reboot.
# In both cases DKMS rebuilds + re-signs the module automatically on kernel
# updates (dkms.conf AUTOINSTALL), so the MOK enrollment never repeats.
#
# Usage (interactive, prompts for a one-time MOK password when Secure Boot
# is on and the key is not yet enrolled):
#   sudo scripts/linux/install-smu-driver.sh
#
# Reversed by `sudo make -C backend/lib/ryzen_smu dkms-uninstall` plus
# removing /etc/modules-load.d/ryzen_smu.conf.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
SRC="${REPO_ROOT}/backend/lib/ryzen_smu"
MODULE="ryzen_smu"
SYSFS="/sys/kernel/ryzen_smu_drv"
MODULES_LOAD="/etc/modules-load.d/${MODULE}.conf"

ok()   { printf '  [ok]   %s\n' "$1"; }
info() { printf '  [i]    %s\n' "$1"; }
fail() { printf '  [FAIL] %s\n' "$1" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || fail "must run as root (sudo $0)"
for f in dkms.conf Makefile drv.c smu.c smu.h; do
    [ -f "${SRC}/${f}" ] || fail "vendored driver incomplete: ${SRC}/${f} missing"
done

KERNEL="$(uname -r)"
VERSION="$(sed -n 's/^VERSION[[:space:]]*:=[[:space:]]*//p' "${SRC}/Makefile" | tr -d '[:space:]')"
[ -n "$VERSION" ] || fail "could not parse module version from ${SRC}/Makefile"

# --- Idempotence: module already loaded -> just keep it autoloaded ----------
if [ -d "$SYSFS" ]; then
    ok "ryzen_smu already loaded (kernel ${KERNEL})"
else
    # --- Prerequisites ------------------------------------------------------
    need() { command -v "$1" >/dev/null 2>&1 || fail "$1 not found in PATH (dnf: sudo dnf install $2; apt: sudo apt install $2)"; }
    need dkms   "dkms"
    need make   "make gcc"
    need gcc    "make gcc"

    # Kernel build tree for the *running* kernel (DKMS builds against it).
    if [ ! -d "/usr/src/kernels/${KERNEL}" ] && [ ! -d "/usr/src/linux-headers-${KERNEL}" ]; then
        fail "no kernel headers for ${KERNEL}
  -> Fedora: sudo dnf install "kernel-devel-${KERNEL}"
  -> Debian/Ubuntu: sudo apt install "linux-headers-${KERNEL}"
     (headers normally arrive with the kernel package; DKMS needs them to build)"
    fi

    # --- Secure Boot state (drives the signing/enrollment path) -------------
    SB_STATE=""
    if command -v mokutil >/dev/null 2>&1; then
        SB_STATE="$(mokutil --sb-state 2>/dev/null || true)"
    fi
    case "$SB_STATE" in
        *[Ee]nabled*) SB=1 ;;
        *)            SB=0; [ -z "$SB_STATE" ] && info "Secure Boot state unknown (mokutil unavailable); treating as disabled" ;;
    esac

    # --- Build + install via DKMS -------------------------------------------
    # dkms-install copies the vendored source to /usr/src/<module>-<version>
    # (the repo tree is never built in), then dkms add/build/install —
    # signing with /var/lib/dkms/mok{.pub,.key} when Secure Boot requires it.
    # Replace any previous registration so re-running after a re-pin works.
    if dkms status 2>/dev/null | grep -q "${MODULE}/"; then
        info "removing previous DKMS registration (${MODULE}/${VERSION})"
        dkms remove "${MODULE}/${VERSION}" --all >/dev/null 2>&1 || true
    fi
    make -C "$SRC" dkms-install

    if [ "$SB" -eq 1 ]; then
        MOK_PUB="/var/lib/dkms/mok.pub"
        if [ ! -f "$MOK_PUB" ]; then
            fail "Secure Boot is on but DKMS produced no MOK key (${MOK_PUB}).
  Your dkms version did not auto-generate one; sign manually or disable
  Secure Boot, then re-run."
        fi
        if mokutil --test-key "$MOK_PUB" 2>/dev/null | grep -qi "already enrolled"; then
            ok "DKMS MOK key already enrolled"
            modprobe "$MODULE" || true
        else
            info "Secure Boot is ON and the DKMS signing key is not enrolled yet."
            info "Importing the key for enrollment at next boot — you will set a one-time password now."
            mokutil --import "$MOK_PUB"
            [ -d "$SYSFS" ] || cat <<EOF

  NEXT STEPS
  1. Reboot. At the blue "MOK Manager" screen:
       Enroll MOK -> Continue -> Yes -> enter the password you just set.
  2. After login the module auto-loads (modules-load.d entry installed
     below). Hardware control via XmaX starts working; no re-enrollment
     is ever needed again (kernel updates re-sign with the same key).
EOF
        fi
    else
        modprobe "$MODULE" || true
    fi

    if [ -d "$SYSFS" ]; then
        ok "ryzen_smu loaded ($(cat ${SYSFS}/drv_version 2>/dev/null || echo "kernel ${KERNEL}"))"
    elif [ "$SB" -eq 1 ]; then
        info "module installed but not loaded until the MOK key is enrolled (reboot, see above)"
    else
        fail "module installed but modprobe failed; check: dmesg | tail, dkms status"
    fi
fi

# --- Auto-load at boot --------------------------------------------------------
printf '%s\n' "$MODULE" > "$MODULES_LOAD"
ok "auto-load at boot: ${MODULES_LOAD}"

# --- Sanity readback ----------------------------------------------------------
if [ -d "$SYSFS" ]; then
    ok "sysfs interface present: ${SYSFS} (codename=$(cat ${SYSFS}/codename 2>/dev/null || echo '?'))"
fi

echo
echo "==> done. xmaxd picks this up on next start: sudo systemctl restart xmaxd"
