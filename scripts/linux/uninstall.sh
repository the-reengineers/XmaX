#!/usr/bin/env bash
# uninstall.sh — reverse the Linux *source/dev* install: the system-level
# changes made by scripts/linux/verify-dbus.sh (daemon, polkit, D-Bus, unit)
# and scripts/linux/build-ryzenadj.sh (libryzenadj). It does NOT touch a
# packaged xmax RPM (use `dnf remove xmax xmax-gnome-extension` for that), and
# it does not touch the local build tree (use `just clean` for that).
#
# Idempotent: safe to run when only part of the install is present.
set -u

ok()   { printf '  [ok]   %s\n' "$1"; }
skip() { printf '  [skip] %s\n' "$1"; }

if [ "$(id -u)" -ne 0 ]; then
    echo "This script must run as root (it removes system files)." >&2
    echo "Run:  sudo scripts/linux/uninstall.sh" >&2
    exit 1
fi

# A packaged xmax owns the shared policy/unit files below; leave those to dnf.
rpm_owned=0
if rpm -q xmax >/dev/null 2>&1 || rpm -q xmax-gnome-extension >/dev/null 2>&1; then
    rpm_owned=1
    echo "note: the xmax RPM is installed — files it owns will be left alone." >&2
    echo "      reverse the packaged install with: dnf remove xmax xmax-gnome-extension" >&2
fi

# --- 1. Stop the dev service (best-effort; may not be running) ---------------
if command -v systemctl >/dev/null 2>&1; then
    systemctl stop xmaxd.service 2>/dev/null || true
    systemctl disable xmaxd.service 2>/dev/null || true
    systemctl reset-failed xmaxd.service 2>/dev/null || true
fi

# --- 2. Dev-only binaries + ryzenadj (a package never installs to /usr/local) -
rm -f /usr/local/bin/xmaxd /usr/local/bin/ryzenadj /usr/local/lib/libryzenadj.so
ok "removed /usr/local/bin/{xmaxd,ryzenadj} + /usr/local/lib/libryzenadj.so"

# --- 3. Integration files (skip if the RPM owns them) ------------------------
if [ "$rpm_owned" -eq 0 ]; then
    rm -f /usr/share/dbus-1/system.d/org.xmax.Service.conf
    rm -f /usr/share/polkit-1/actions/org.xmax.policy
    rm -f /etc/systemd/system/xmaxd.service
    ok "removed D-Bus policy, polkit actions, and the systemd unit"
else
    skip "D-Bus policy / polkit actions / systemd unit (owned by the RPM)"
fi

# --- 4. Reload live state so the removals take effect ------------------------
if command -v systemctl >/dev/null 2>&1; then
    systemctl daemon-reload 2>/dev/null || true
    systemctl try-reload dbus-broker.service 2>/dev/null || systemctl reload dbus.service 2>/dev/null || true
fi
ldconfig 2>/dev/null || true   # prune libryzenadj from the shared-lib cache

echo
echo "dev install reversed. The GNOME extension is per-user — remove it with:"
echo "  gnome-extensions uninstall xmax@local   # then log out/in or Alt+F2 -> r"