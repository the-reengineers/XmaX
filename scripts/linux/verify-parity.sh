#!/usr/bin/env bash
# verify-parity.sh — surface the gap between the full Windows transport surface
# and the Linux system-integration surface (D-Bus + xmaxctl), flagging any
# Linux-only gaps explicitly as "unsupported, not missing".
#
# Read-only / hermetic: it greps the source for the three command surfaces and
# prints a documented field-coverage table. No service needs to be running.
#
# Sources of truth:
#   backend/src/transport.cpp      (transport command dispatch)
#   backend/src/dbus/*.xml         (D-Bus method surface)
#   backend/src/xmaxctl.cpp        (CLI subcommands)
#   backend/src/shared.h           (Metrics struct — field list below)

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
SRC="${REPO_ROOT}/backend/src"

transport="${SRC}/transport.cpp"
dbusxml="${SRC}/dbus/org.xmax.Service.xml"
xmaxctl="${SRC}/xmaxctl.cpp"

ok()   { printf '  [ok]      %s\n' "$1"; }
gap()  { printf '  [gap]     %s\n' "$1"; }
info() { printf '  [--%s] %s\n' "$1" "$2"; }

# --- Derive the three command surfaces ---------------------------------------
transport_cmds="$(sed -n 's/.*cmd\.method == "\([a-z_]*\)".*/\1/p' "$transport" | sort -u)"
dbus_methods="$(sed -n 's/.*<method name="\([A-Za-z]*\)">.*/\1/p' "$dbusxml" | sort -u)"
cli_cmds="$(sed -n 's/.*if (cmd == "\([-a-z]*\)").*/\1/p' "$xmaxctl" | sort -u)"

# Transport commands reachable through the Linux system-integration surface
# (D-Bus methods for the system UI + xmaxctl). The D-Bus/CLI names differ in
# case/hyphens from the transport snake_case, so this is stated explicitly
# rather than string-matched. SetAutoStart has no transport command.
exposed='get_charge_limit
get_metrics
ping
set_charge_limit
set_fan
set_tdp'

echo "==================================================================="
echo " XmaX Linux / Windows surface parity"
echo "==================================================================="

echo
echo "== command surfaces (derived from source) =="
printf '  transport commands: %s\n' "$(echo "$transport_cmds" | tr '\n' ' ')"
echo
printf '  D-Bus methods:      %s\n' "$(echo "$dbus_methods" | tr '\n' ' ')"
echo
printf '  xmaxctl subcommands:%s\n' "$(echo "$cli_cmds" | tr '\n' ' ')"

echo
echo "== transport -> D-Bus -> xmaxctl mapping (the system-integration subset) =="
ok "Ping -> ping -> (n/a: health check)"
ok "GetMetrics -> get_metrics -> xmaxctl metrics"
ok "SetTdp -> set_tdp -> xmaxctl tdp"
ok "SetFan -> set_fan -> xmaxctl fan"
ok "SetChargeLimit / GetChargeLimit -> set_/get_charge_limit -> xmaxctl charge-limit"
ok "SetAutoStart -> (no transport cmd; Platform::set_auto_start) -> xmaxctl auto-start"

echo
echo "== transport commands with no system-integration exposure (socket-only) =="
# comm -23 reports transport commands not in the exposed set.
socket_only="$(comm -23 \
    <(printf '%s\n' "$transport_cmds") \
    <(printf '%s\n' "$exposed" | sort -u))"
if [ -n "$socket_only" ]; then
    for cmd in $socket_only; do
        gap "$cmd"
    done
    info "note" "profiles/fan-curves/config/subscription/UMA/reboot are deliberate Linux-UI omissions: the system integration only needs readouts + a few controls, and the full transport surface stays reachable over the AF_UNIX socket / xmaxctl fallback"
else
    ok "none — every transport command is exposed"
fi

echo
echo "== metrics field coverage (see shared.h Metrics) =="
# "field | status | note"; statuses: yes / control / gap.
while IFS='|' read -r field status note; do
    [ -n "$field" ] || continue
    case "$status" in
        yes)     ok   "$field — $note" ;;
        control) info "ctl" "$field — $note" ;;
        gap)     gap  "$field — $note (unsupported, not missing)" ;;
    esac
done <<'ROWS'
cpu.util_pct|yes|/proc/stat deltas
cpu.clock_mhz|yes|cpufreq sysfs (kHz -> MHz)
cpu.temp_c|control|EC 0x0470 (k10temp parser ready, unused)
cpu.package_watts|gap|no driver-free method on ANY OS (Windows parity gap)
cpu.smart_shift|gap|ADLX-only (SmartShift)
cpu.apu_shift_value|gap|ADLX-only (PowerDistribution)
cpu.apu_shift_limit|gap|ADLX-only (PowerDistribution)
gpu.util_pct|yes|drm gpu_busy_percent
gpu.clock_mhz|yes|amdgpu freq1_input
gpu.temp_c|yes|amdgpu temp1_input
gpu.power_w|yes|amdgpu power1_average
gpu.vram_used_bytes|yes|amdgpu mem_info_vram_used (chip-dependent -> nullopt)
gpu.vram_total_bytes|yes|amdgpu mem_info_vram_total (chip-dependent -> nullopt)
ram.used_bytes / total / avail / load|yes|/proc/meminfo
fan.mode / speed_pct / rpm|control|EC registers (graceful defaults when absent)
power.battery_pct|yes|/sys/class/power_supply
power.charge_limit_pct|control|EC 0x04A3
ts|yes|clock_gettime
ROWS

echo
echo "== documented Linux-only gaps (explicitly 'unsupported, not missing') =="
gap "SmartShift / PowerDistribution  -> ADLX has no clean Linux equivalent (system_metrics returns empty)"
gap "UMA / VRAM resize               -> BIOS-level, not runtime-settable (get_uma_options -> SensorUnavailable)"
gap "reboot                          -> SensorUnavailable on Linux (no shutdown.exe)"
info "note" "CPU package power is also unsupported on Windows (no driver-free method, see TODOS.md) — a parity gap, not a Linux-only gap"

echo
echo "parity report done (informational — exit 0 regardless of gaps)."
exit 0