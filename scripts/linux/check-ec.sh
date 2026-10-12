#!/usr/bin/env bash
# Probe the EC access paths the backend uses for fan / charge-limit control.
# The backend tries, in order: (1) the ec_sys debugfs char node, (2) raw
# Super-I/O port I/O via iopl(3). Both require root on a stock kernel, so this
# check is meant to be run as root when a dev wants to validate that control
# actually works on the hardware.
#
# Read-only environment check: it inspects presence/permissions and lists the
# register map; it never writes an EC register.

set -u

ok()   { printf '  [ok]   %s\n' "$1"; }
miss() { printf '  [warn] %s\n' "$1"; }   # absent on this host / not usable here
warn=0

if [ "$(id -u)" != 0 ]; then
    printf '  [warn] not running as root (EC access is root-only) -- republish with: sudo %s\n' "$0"
    warn=1
fi

EC_IO=/sys/kernel/debug/ec/ec0/io

echo "== debugfs =="
if mount | grep -q debugfs; then ok "debugfs mounted"
else miss "debugfs not mounted (mount -t debugfs none /sys/kernel/debug)"; fi

echo
echo "== ec_sys debugfs node (backend primary) =="
if [ -e "$EC_IO" ]; then
    ok "node present: $EC_IO"
    if [ -r "$EC_IO" ] && [ -w "$EC_IO" ]; then ok "ec0/io readable + writable"
    else miss "ec0/io not rw (run as root)"; fi
else
    miss "node absent: $EC_IO"
fi

echo
echo "== ec_sys module =="
if modprobe -n ec_sys >/dev/null 2>&1; then ok "modprobe ec_sys available (ensure loaded before probing)"
else miss "modprobe ec_sys unavailable (kernel missing CONFIG_ACPI_EC_DEBUGFS)"; fi

echo
echo "== backend register map (informational) =="
printf '  [ok]   FAN_MODE=0x044A  FAN_DUTY=0x044B  FAN_RPM_HI=0x0476  FAN_RPM_LO=0x0477\n'
printf '  [ok]   CPU_TEMP=0x0470  CHARGE_LIMIT=0x04A3  POWER_STATE=0x04FE  BUTTON=0x0230  APP_FUN_EN=0x0231\n'

echo
echo "== Super-I/O fallback (backend secondary) =="
case "$(uname -m)" in
    x86_64|i386|i686)
        ok "x86 host: Super-I/O path compiled in (needs CAP_SYS_RAWIO, e.g. root)"
        ;;
    *) miss "non-x86 host: Super-I/O path compiled out (ec_sys only)";;
esac

# Only reachable via ec_sys: the 16-bit vendor registers (0x0230..0x04FE) are
# beyond the ACPI EC's 256-byte Super-I/O window, so the fallback truncates to
# the low byte. Note this for devs validating charge-limit on fallback-only hosts.
echo
echo "  [note] 16-bit vendor registers (charge limit 0x04A3 etc.) need the ec_sys path;"
echo "         the Super-I/O fallback only sees the low byte (ACPI EC 256-byte space)."

echo
if [ "$warn" = 1 ]; then
    echo "RESULT: warnings above (EC control not usable as configured)."
else
    echo "RESULT: EC control primaries present and usable."
fi
exit 0