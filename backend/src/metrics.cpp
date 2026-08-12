#include "metrics.h"

#include <iostream>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#endif

// EC register addresses for power state and charge limit
static constexpr uint16_t EC_POWER_STATE   = 0x04FE;
static constexpr uint16_t EC_CHARGE_LIMIT  = 0x04A3;

// Power state decode values (from EC 0x04FE)
static constexpr uint8_t POWER_STATE_BATTERY     = 1;
static constexpr uint8_t POWER_STATE_USB_C_SLOW  = 8;  // or 9
static constexpr uint8_t POWER_STATE_USB_C_FAST  = 2;  // or 3
static constexpr uint8_t POWER_STATE_DC_IN       = 4;  // or 5 or 0x85

MetricsPoller::MetricsPoller(Platform& platform, FanController& fan_ctrl, TdpController& tdp_ctrl)
    : platform_(platform)
    , fan_ctrl_(fan_ctrl)
    , tdp_ctrl_(tdp_ctrl)
{
}

MetricsPoller::~MetricsPoller() {
    stop();
}

void MetricsPoller::start() {
    if (running_.load()) {
        return;  // Already running
    }

    running_.store(true);
    poll_thread_ = std::thread(&MetricsPoller::poll_loop, this);
}

void MetricsPoller::stop() {
    if (!running_.load()) {
        return;  // Not running
    }

    running_.store(false);
    if (poll_thread_.joinable()) {
        poll_thread_.join();
    }
}

auto MetricsPoller::get_metrics() -> Metrics {
    std::lock_guard lock(metrics_mutex_);
    return current_metrics_;
}

auto MetricsPoller::is_running() const -> bool {
    return running_.load();
}

void MetricsPoller::poll_loop() {
    while (running_.load()) {
        // Poll all sensor groups
        poll_cpu();
        poll_gpu();
        poll_ram();
        poll_fan();
        poll_power();

        // Update timestamp
        {
            std::lock_guard lock(metrics_mutex_);
            current_metrics_.ts = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
        }

        // Sleep for polling interval
        std::this_thread::sleep_for(POLL_INTERVAL);
    }
}

void MetricsPoller::poll_cpu() {
    std::lock_guard lock(metrics_mutex_);

#ifdef _WIN32
    // CPU utilization via GetSystemTimes
    static FILETIME prev_idle = {}, prev_kernel = {}, prev_user = {};
    static bool has_prev = false;

    FILETIME idle, kernel, user;
    if (GetSystemTimes(&idle, &kernel, &user)) {
        if (has_prev) {
            // Convert FILETIME to uint64_t (100-nanosecond intervals)
            auto to_uint64 = [](const FILETIME& ft) -> uint64_t {
                return (static_cast<uint64_t>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
            };

            uint64_t idle_diff = to_uint64(idle) - to_uint64(prev_idle);
            uint64_t kernel_diff = to_uint64(kernel) - to_uint64(prev_kernel);
            uint64_t user_diff = to_uint64(user) - to_uint64(prev_user);

            // Kernel time includes idle time
            uint64_t total = kernel_diff + user_diff;
            if (total > 0) {
                uint64_t busy = (kernel_diff - idle_diff) + user_diff;
                current_metrics_.cpu.util_pct = static_cast<float>(busy * 100) / static_cast<float>(total);
            }
        }

        prev_idle = idle;
        prev_kernel = kernel;
        prev_user = user;
        has_prev = true;
    }

    // CPU clock speed via WMI (Win32_Processor.MaxClockSpeed)
    // TODO: Implement WMI query for CPU clock

    // CPU temperature via EC register 0x0470
    auto temp_result = platform_.ec_read(0x0470);
    if (temp_result) {
        current_metrics_.cpu.temp_c = static_cast<int>(temp_result.value());
    }

    // CPU package power via SMU mailbox
    // TODO: Implement SMU query for package power
#endif
}

void MetricsPoller::poll_gpu() {
    std::lock_guard lock(metrics_mutex_);

    // Poll GPU metrics via platform-specific API (ADLX on Windows, sysfs on Linux)
    auto result = platform_.gpu_metrics();
    if (result) {
        const auto& telemetry = result.value();
        current_metrics_.gpu.util_pct = telemetry.util_pct;
        current_metrics_.gpu.clock_mhz = telemetry.clock_mhz;
        current_metrics_.gpu.temp_c = telemetry.temp_c;
        current_metrics_.gpu.power_w = telemetry.power_w;
        current_metrics_.gpu.vram_used_mb = telemetry.vram_used_mb;
        current_metrics_.gpu.vram_total_mb = telemetry.vram_total_mb;
        std::cout << "[metrics] GPU metrics OK: util=" << telemetry.util_pct
                  << " clock=" << telemetry.clock_mhz << "MHz"
                  << " temp=" << (telemetry.temp_c.has_value() ? std::to_string(telemetry.temp_c.value()) : "null")
                  << std::endl;
    } else {
        // GPU metrics unavailable -- set to defaults
        current_metrics_.gpu = GpuMetrics{};
        std::cout << "[metrics] GPU metrics unavailable (ADLX may have failed to initialize)" << std::endl;
    }
}

void MetricsPoller::poll_ram() {
    std::lock_guard lock(metrics_mutex_);

#ifdef _WIN32
    MEMORYSTATUSEX mem_status;
    mem_status.dwLength = sizeof(MEMORYSTATUSEX);
    if (GlobalMemoryStatusEx(&mem_status)) {
        // Convert bytes to GB
        constexpr double bytes_per_gb = 1024.0 * 1024.0 * 1024.0;
        current_metrics_.ram.total_gb = static_cast<float>(mem_status.ullTotalPhys / bytes_per_gb);
        current_metrics_.ram.avail_gb = static_cast<float>(mem_status.ullAvailPhys / bytes_per_gb);
        current_metrics_.ram.used_gb = current_metrics_.ram.total_gb - current_metrics_.ram.avail_gb;
        current_metrics_.ram.load_pct = static_cast<float>(mem_status.dwMemoryLoad);
    }
#endif
}

void MetricsPoller::poll_fan() {
    std::lock_guard lock(metrics_mutex_);

    // Poll fan state from FanController
    current_metrics_.fan = fan_ctrl_.read_state();
}

void MetricsPoller::poll_power() {
    std::lock_guard lock(metrics_mutex_);

    // Poll power state from EC register 0x04FE
    auto power_result = platform_.ec_read(EC_POWER_STATE);
    if (power_result) {
        uint8_t value = power_result.value();

        // Decode power state
        if (value == POWER_STATE_BATTERY) {
            current_metrics_.power.mode = PowerState::Source::Battery;
            current_metrics_.power.label = "Battery only";
        } else if (value == POWER_STATE_USB_C_SLOW || value == 9) {
            current_metrics_.power.mode = PowerState::Source::UsbCSlow;
            current_metrics_.power.label = "USB-C (65W class)";
        } else if (value == POWER_STATE_USB_C_FAST || value == 3) {
            current_metrics_.power.mode = PowerState::Source::UsbCFast;
            current_metrics_.power.label = "USB-C (100W class)";
        } else if (value == POWER_STATE_DC_IN || value == 5 || value == 0x85) {
            current_metrics_.power.mode = PowerState::Source::DcIn;
            current_metrics_.power.label = "DC-In (dedicated charger)";
        } else {
            current_metrics_.power.mode = PowerState::Source::Unknown;
            current_metrics_.power.label = "Unknown";
        }
    } else {
        current_metrics_.power.mode = PowerState::Source::Unknown;
        current_metrics_.power.label = "Unknown";
        std::cout << "[metrics] Power state EC read failed (WMI may have failed)" << std::endl;
    }

    // Poll charge limit from EC register 0x04A3
    auto charge_result = platform_.ec_read(EC_CHARGE_LIMIT);
    if (charge_result) {
        current_metrics_.power.charge_limit_pct = static_cast<int>(charge_result.value());
    } else {
        current_metrics_.power.charge_limit_pct = std::nullopt;
    }

    // Battery percentage would come from a separate source (e.g., WMI or sysfs)
    // For now, leave as nullopt
}
