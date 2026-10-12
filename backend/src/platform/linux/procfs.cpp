#include "procfs.h"

#include <filesystem>
#include <fstream>
#include <sstream>

namespace fs = std::filesystem;

namespace procfs {
namespace {

// Locate <base>/hwmon*/name == chip_name. Returns the hwmon directory path.
std::optional<fs::path> find_hwmon(const fs::path& base, const std::string& chip_name) {
    std::error_code ec;
    fs::directory_iterator it(base, ec);
    if (ec) {
        return std::nullopt;
    }
    for (const auto& entry : it) {
        std::string fn = entry.path().filename().string();
        if (fn.rfind("hwmon", 0) != 0) {
            continue;
        }
        std::ifstream name_file(entry.path() / "name");
        std::string name;
        if (name_file >> name && name == chip_name) {
            return entry.path();
        }
    }
    return std::nullopt;
}

}  // namespace

std::optional<CpuTimes> read_cpu_times(const std::string& stat_path) {
    std::ifstream in(stat_path);
    if (!in) {
        return std::nullopt;
    }
    std::string line;
    while (std::getline(in, line)) {
        if (line.rfind("cpu ", 0) != 0) {
            continue;  // skip "cpu0", "cpu1", ... per-CPU lines
        }
        std::istringstream iss(line);
        std::string label;
        iss >> label;  // "cpu"
        CpuTimes t;
        uint64_t value = 0;
        int column = 0;
        while (iss >> value) {
            t.total += value;
            // Columns: 0=user 1=nice 2=system 3=idle 4=iowait ...
            if (column == 3 || column == 4) {
                t.idle += value;
            }
            ++column;
        }
        if (column == 0) {
            return std::nullopt;  // "cpu" with no numbers is malformed
        }
        return t;
    }
    return std::nullopt;
}

double CpuUtil::sample(const std::string& stat_path) {
    auto now = read_cpu_times(stat_path);
    if (!now) {
        return 0.0;
    }
    double util = 0.0;
    if (prev_) {
        int64_t idle_d = static_cast<int64_t>(now->idle) - static_cast<int64_t>(prev_->idle);
        int64_t total_d = static_cast<int64_t>(now->total) - static_cast<int64_t>(prev_->total);
        if (total_d > 0) {
            int64_t busy_d = total_d - idle_d;
            if (busy_d < 0) {
                busy_d = 0;
            }
            util = 100.0 * static_cast<double>(busy_d) / static_cast<double>(total_d);
        }
    }
    prev_ = *now;
    return util;
}

std::optional<MemInfo> read_meminfo(const std::string& meminfo_path) {
    std::ifstream in(meminfo_path);
    if (!in) {
        return std::nullopt;
    }
    std::optional<uint64_t> total_kb;
    std::optional<uint64_t> avail_kb;
    std::optional<uint64_t> free_kb;
    std::string line;
    while (std::getline(in, line)) {
        std::istringstream iss(line);
        std::string key;  // includes the trailing ':', e.g. "MemTotal:"
        uint64_t value = 0;
        std::string unit;
        if (!(iss >> key >> value)) {
            continue;
        }
        if (key == "MemTotal:") {
            total_kb = value;
        } else if (key == "MemAvailable:") {
            avail_kb = value;
        } else if (key == "MemFree:") {
            free_kb = value;
        }
    }
    if (!total_kb) {
        return std::nullopt;  // meminfo without MemTotal is malformed
    }
    MemInfo m;
    m.total_bytes = *total_kb * 1024;
    m.avail_bytes = avail_kb ? *avail_kb * 1024 : 0;
    m.free_bytes = free_kb ? *free_kb * 1024 : 0;
    m.used_bytes = m.total_bytes - m.avail_bytes;
    m.load_pct = m.total_bytes
        ? 100.0 * static_cast<double>(m.used_bytes) / static_cast<double>(m.total_bytes)
        : 0.0;
    return m;
}

std::optional<long long> read_long(const std::string& path) {
    std::ifstream in(path);
    if (!in) {
        return std::nullopt;
    }
    long long value = 0;
    if (!(in >> value)) {
        return std::nullopt;
    }
    return value;
}

std::optional<int> read_temp_c(const std::string& path) {
    auto v = read_long(path);
    if (!v) {
        return std::nullopt;
    }
    return static_cast<int>(*v / 1000);
}

std::optional<uint32_t> cpu_clock_mhz(const std::string& freq_path) {
    auto khz = read_long(freq_path);
    if (!khz || *khz <= 0) {
        return std::nullopt;
    }
    return static_cast<uint32_t>(*khz / 1000);
}

std::optional<int> cpu_temp_c(const std::string& hwmon_base) {
    auto dir = find_hwmon(hwmon_base, "k10temp");
    if (!dir) {
        return std::nullopt;
    }
    if (auto t = read_temp_c((*dir / "temp1_input").string())) {
        return t;
    }
    if (auto t = read_temp_c((*dir / "Tctl_input").string())) {
        return t;
    }
    if (auto t = read_temp_c((*dir / "Tctl").string())) {
        return t;
    }
    return std::nullopt;
}

std::optional<int> battery_pct(const std::string& ps_base) {
    std::error_code ec;
    fs::directory_iterator it(ps_base, ec);
    if (ec) {
        return std::nullopt;
    }
    for (const auto& entry : it) {
        std::string fn = entry.path().filename().string();
        if (fn.rfind("BAT", 0) != 0) {
            continue;
        }
        auto v = read_long((entry.path() / "capacity").string());
        if (v && *v >= 0 && *v <= 100) {
            return static_cast<int>(*v);
        }
    }
    return std::nullopt;
}

std::optional<bool> ac_online(const std::string& ps_base) {
    std::error_code ec;
    fs::directory_iterator it(ps_base, ec);
    if (ec) {
        return std::nullopt;
    }
    for (const auto& entry : it) {
        std::string fn = entry.path().filename().string();
        if (fn.rfind("AC", 0) != 0) {
            continue;
        }
        auto v = read_long((entry.path() / "online").string());
        if (v) {
            return *v != 0;
        }
    }
    return std::nullopt;
}

std::string power_source(const std::string& ps_base) {
    // Scan /sys/class/power_supply for clues about the active power source.
    // Without EC register access we cannot distinguish USB-C (slow/fast) from
    // DC-In, so any AC-online state is reported as "dc_in".

    std::error_code ec;
    fs::directory_iterator it(ps_base, ec);
    if (ec) {
        return "unknown";
    }

    bool ac_online_val = false;
    bool ac_found = false;
    bool battery_discharging = false;
    bool usb_type_found = false;

    for (const auto& entry : it) {
        if (!entry.is_directory(ec)) continue;
        std::string fn = entry.path().filename().string();

        // Read the "type" attribute to classify this power supply.
        std::ifstream type_file(entry.path() / "type");
        std::string ps_type;
        if (type_file >> ps_type) {
            if (ps_type == "Mains") {
                auto v = read_long((entry.path() / "online").string());
                if (v) {
                    ac_found = true;
                    ac_online_val = (*v != 0);
                }
            } else if (ps_type == "USB" || ps_type == "USB_CDP" || ps_type == "USB_ACA") {
                auto v = read_long((entry.path() / "online").string());
                if (v && *v != 0) {
                    usb_type_found = true;
                }
            } else if (ps_type == "Battery") {
                std::ifstream status_file(entry.path() / "status");
                std::string status;
                if (status_file >> status && status == "Discharging") {
                    battery_discharging = true;
                }
            }
        }
    }

    // Priority: USB type detected → usb_c_slow (best guess without wattage info)
    //           AC online → dc_in
    //           Battery discharging → battery
    //           Otherwise → unknown
    if (usb_type_found) {
        return "usb_c_slow";  // can't distinguish slow/fast without EC
    }
    if (ac_found && ac_online_val) {
        return "dc_in";
    }
    if (battery_discharging) {
        return "battery";
    }
    return "unknown";
}

GpuInfo gpu_info(const std::string& hwmon_base, const std::string& drm_base) {
    GpuInfo info;
    auto dir = find_hwmon(hwmon_base, "amdgpu");
    if (!dir) {
        return info;  // info.found stays false -> caller maps to SensorUnavailable
    }
    info.found = true;
    info.temp_c = read_temp_c((*dir / "temp1_input").string());
    if (auto w = read_long((*dir / "power1_average").string())) {
        info.power_w = static_cast<double>(*w) / 1'000'000.0;  // µW -> W
    }
    if (auto hz = read_long((*dir / "freq1_input").string())) {
        if (*hz > 0) {
            info.clock_mhz = static_cast<uint32_t>(*hz / 1'000'000);  // Hz -> MHz
        }
    }
    if (auto v = read_long((*dir / "mem_info_vram_used").string())) {
        if (*v >= 0) {
            info.vram_used_bytes = static_cast<uint64_t>(*v);  // already bytes
        }
    }
    if (auto v = read_long((*dir / "mem_info_vram_total").string())) {
        if (*v >= 0) {
            info.vram_total_bytes = static_cast<uint64_t>(*v);
        }
    }

    // GPU busy % from the drm device node (card*, skip connector nodes with '-').
    std::error_code ec;
    fs::directory_iterator it(drm_base, ec);
    if (!ec) {
        for (const auto& entry : it) {
            std::string fn = entry.path().filename().string();
            if (fn.rfind("card", 0) != 0 || fn.find('-') != std::string::npos) {
                continue;
            }
            auto busy = read_long((entry.path() / "device" / "gpu_busy_percent").string());
            if (busy) {
                info.util_pct = static_cast<double>(*busy);
                break;
            }
        }
    }
    return info;
}

}  // namespace procfs