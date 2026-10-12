#pragma once

#include <cstdint>
#include <optional>
#include <string>

// Read-only Linux metrics parsers built on sysfs + /proc. Pure apart from
// std::filesystem reads; each primitive takes an explicit path so tests can
// point them at committed fixtures, and each higher-level scanner takes a base
// directory (the real path is only the default).
//
// Conventions:
//   * An unreadable/malformed source yields nullopt, never a fake value.
//   * hwmon temperatures are millidegrees C in the kernel, reported here as °C.
//   * amdgpu power is µW -> W; freq is Hz -> MHz; VRAM is already in bytes.
namespace procfs {

// ---- CPU utilization (/proc/stat) ----

struct CpuTimes {
    uint64_t idle = 0;   // idle + iowait columns
    uint64_t total = 0;  // sum of all numeric columns
};

// Parse the first "cpu " line of a /proc/stat-style file.
std::optional<CpuTimes> read_cpu_times(const std::string& stat_path);

// Prev-idle/prev-total delta sampler. The first sample returns 0.0 (no
// baseline yet); subsequent samples return utilization in [0, 100].
class CpuUtil {
public:
    auto sample(const std::string& stat_path = "/proc/stat") -> double;

private:
    std::optional<CpuTimes> prev_;
};

// ---- RAM (/proc/meminfo) ----

struct MemInfo {
    uint64_t total_bytes = 0;
    uint64_t avail_bytes = 0;
    uint64_t free_bytes = 0;
    uint64_t used_bytes = 0;  // total - avail (matches Win32 GlobalMemoryStatusEx)
    double load_pct = 0.0;    // used / total * 100
};

std::optional<MemInfo> read_meminfo(const std::string& meminfo_path);

// ---- CPU clock (cpufreq) ----

// Read a scaling_cur_freq / cpuinfo_cur_freq file (kHz) and return whole MHz.
std::optional<uint32_t> cpu_clock_mhz(const std::string& freq_path);

// ---- Low-level helpers ----

// Read a signed 64-bit integer from a single-value sysfs file.
std::optional<long long> read_long(const std::string& path);

// Read a millidegree-C hwmon temperature file and return whole °C.
std::optional<int> read_temp_c(const std::string& path);

// ---- CPU temperature (k10temp hwmon) ----

// Scan <hwmon_base>/hwmon*/name for "k10temp", then read temp1_input (falling
// back to Tctl / Tctl_input).
std::optional<int> cpu_temp_c(const std::string& hwmon_base = "/sys/class/hwmon");

// ---- Power supply (battery % / AC presence) ----

// First BAT*/capacity in [0, 100].
std::optional<int> battery_pct(const std::string& ps_base = "/sys/class/power_supply");

// First AC*/online interpreted as a boolean.
std::optional<bool> ac_online(const std::string& ps_base = "/sys/class/power_supply");

// Infer the power source from /sys/class/power_supply entries.
// Returns one of: "battery", "usb_c_slow", "usb_c_fast", "dc_in", or "unknown".
// Without EC register access, USB-C and DC-In cannot be distinguished — any
// AC-online state is reported as "dc_in".
std::string power_source(const std::string& ps_base = "/sys/class/power_supply");

// ---- GPU telemetry (amdgpu hwmon + drm busy) ----

struct GpuInfo {
    bool found = false;  // true iff an amdgpu hwmon chip was located
    std::optional<double> util_pct;             // gpu_busy_percent (0-100)
    std::optional<uint32_t> clock_mhz;          // freq1_input
    std::optional<int> temp_c;                  // temp1_input
    std::optional<double> power_w;              // power1_average
    std::optional<uint64_t> vram_used_bytes;    // mem_info_vram_used
    std::optional<uint64_t> vram_total_bytes;   // mem_info_vram_total
};

GpuInfo gpu_info(const std::string& hwmon_base = "/sys/class/hwmon",
                 const std::string& drm_base = "/sys/class/drm");

}  // namespace procfs