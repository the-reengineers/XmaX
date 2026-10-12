#!/usr/bin/env bash
# Probe the host for the read-only Linux metrics sources the backend reads
# (sysfs + /proc). Warns if a source the backend expects is absent so a dev can
# tell "this machine just doesn't expose it" from "the parser is broken".
#
# Durable env-check for developers; safe to run on any Linux host (read-only,
# no hardware writes).

set -u

warn=0
ok()   { printf '  [ok]   %s\n' "$1"; }
miss() { printf '  [warn] %s (absent)\n' "$1"; warn=1; }

echo "== /proc/stat (first line) =="
head -n1 /proc/stat 2>/dev/null || miss "/proc/stat"

echo
echo "== /proc/meminfo =="
grep -E '^Mem(Total|Free|Available):' /proc/meminfo 2>/dev/null || miss "/proc/meminfo"

echo
echo "== CPU clock (cpufreq) =="
for f in /sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq \
         /sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_cur_freq; do
    if [ -r "$f" ]; then
        printf '  [ok]   %s = %s kHz\n' "$f" "$(cat "$f")"
    else
        miss "$f"
    fi
done

echo
echo "== hwmon chips =="
found_ctr=0; found_k10=0; found_gpu=0
for h in /sys/class/hwmon/hwmon*; do
    [ -e "$h" ] || continue
    name=$(cat "$h/name" 2>/dev/null || echo "?")
    printf '  [ok]   %s = %s\n' "$h" "$name"
    case "$name" in k10temp) found_k10=1 ;; amdgpu) found_gpu=1 ;; esac
done
[ "$found_k10" = 1 ] && ok "k10temp present" || miss "k10temp hwmon (CPU temp)"
[ "$found_gpu" = 1 ] && ok "amdgpu present"     || miss "amdgpu hwmon (GPU telemetry)"

echo
echo "== amdgpu hwmon values (if present) =="
if [ "$found_gpu" = 1 ]; then
    for h in /sys/class/hwmon/hwmon*; do
        [ "$(cat "$h/name" 2>/dev/null)" = amdgpu ] || continue
        for f in temp1_input power1_average freq1_input mem_info_vram_used mem_info_vram_total; do
            if [ -r "$h/$f" ]; then printf '  [ok]   %s/%s = %s\n' "$h" "$f" "$(cat "$h/$f")"
            else miss "$h/$f"; fi
        done
    done
fi

echo
echo "== drm GPU busy =="
for d in /sys/class/drm/card*/device/gpu_busy_percent; do
    [ -e "$d" ] && printf '  [ok]   %s = %s\n' "$d" "$(cat "$d")" || miss "$d"
done

echo
echo "== power_supply =="
for p in /sys/class/power_supply/*; do
    [ -e "$p" ] || continue
    type=$(cat "$p/type" 2>/dev/null || echo "?")
    printf '  [ok]   %s type=%s\n' "$p" "$type"
done
find /sys/class/power_supply -maxdepth 2 -name capacity -exec sh -c 'printf "  [ok]   %s = %s\n" "$1" "$(cat "$1")"' _ {} \; 2>/dev/null

echo
if [ "$warn" = 1 ]; then
    echo "RESULT: warnings above (some sources absent on this host)."
    exit 0
fi
echo "RESULT: all expected metrics sources present."
exit 0