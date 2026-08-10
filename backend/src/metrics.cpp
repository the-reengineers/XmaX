#include "metrics.h"

#include <iostream>

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

    // TODO: Implement CPU metrics polling
    // - CPU utilization: GetSystemTimes delta (Windows) or /proc/stat (Linux)
    // - CPU clock: WMI Win32_Processor (Windows) or /proc/cpuinfo (Linux)
    // - CPU temperature: EC 0x0470 via WMI (Windows) or hwmon (Linux)
    // - CPU package power: via TdpController (SMU mailbox)

    // For now, leave CPU metrics as defaults (0/nullopt)
    // These will be implemented when Platform interface is extended
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
    } else {
        // GPU metrics unavailable -- set to defaults
        current_metrics_.gpu = GpuMetrics{};
    }
}

void MetricsPoller::poll_ram() {
    std::lock_guard lock(metrics_mutex_);

    // TODO: Implement RAM metrics polling
    // - Windows: GlobalMemoryStatusEx
    // - Linux: sysinfo() or /proc/meminfo

    // For now, leave RAM metrics as defaults (0)
    // These will be implemented when Platform interface is extended
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
