#include "metrics.h"
#include "logger.h"

#include <iostream>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wbemidl.h>
#else
#include "platform/linux/procfs.h"
#endif

// EC register addresses for power state and charge limit
static constexpr uint16_t EC_POWER_STATE   = 0x04FE;
static constexpr uint16_t EC_CHARGE_LIMIT  = 0x04A3;

// Power state decode values (from EC 0x04FE)
static constexpr uint8_t POWER_STATE_BATTERY     = 1;
static constexpr uint8_t POWER_STATE_USB_C_SLOW  = 8;  // or 9
static constexpr uint8_t POWER_STATE_USB_C_FAST  = 2;  // or 3
static constexpr uint8_t POWER_STATE_DC_IN       = 4;  // or 5 or 0x85

#ifdef _WIN32
// Read battery percentage via WMI Win32_Battery (driver-free)
static auto battery_pct_wmi() -> std::optional<int> {
    HRESULT hr;
    IWbemServices* cimv2_svc = nullptr;
    std::optional<int> result;

    hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) {
        return std::nullopt;
    }

    IWbemLocator* locator = nullptr;
    hr = CoCreateInstance(CLSID_WbemLocator, nullptr, CLSCTX_INPROC_SERVER,
                          IID_IWbemLocator, reinterpret_cast<void**>(&locator));
    if (FAILED(hr)) {
        CoUninitialize();
        return std::nullopt;
    }

    hr = locator->ConnectServer((BSTR)L"ROOT\\CIMV2", nullptr, nullptr, nullptr, 0, nullptr, nullptr, &cimv2_svc);
    locator->Release();
    if (FAILED(hr)) {
        CoUninitialize();
        return std::nullopt;
    }

    hr = CoSetProxyBlanket(cimv2_svc, RPC_C_AUTHN_WINNT, RPC_C_AUTHZ_NONE, nullptr,
                           RPC_C_AUTHN_LEVEL_CALL, RPC_C_IMP_LEVEL_IMPERSONATE, nullptr, EOAC_NONE);
    if (FAILED(hr)) {
        cimv2_svc->Release();
        CoUninitialize();
        return std::nullopt;
    }

    IEnumWbemClassObject* enumerator = nullptr;
    hr = cimv2_svc->ExecQuery(
        (BSTR)L"WQL",
        (BSTR)L"SELECT EstimatedChargeRemaining FROM Win32_Battery",
        WBEM_FLAG_FORWARD_ONLY | WBEM_FLAG_RETURN_IMMEDIATELY,
        nullptr, &enumerator);

    if (SUCCEEDED(hr) && enumerator) {
        IWbemClassObject* obj = nullptr;
        ULONG ret = 0;
        hr = enumerator->Next(WBEM_INFINITE, 1, &obj, &ret);

        if (SUCCEEDED(hr) && ret > 0 && obj) {
            VARIANT v;
            VariantInit(&v);
            hr = obj->Get((BSTR)L"EstimatedChargeRemaining", 0, &v, nullptr, nullptr);

            if (SUCCEEDED(hr) && (v.vt == VT_I4 || v.vt == VT_UI2 || v.vt == VT_I2)) {
                int pct = (v.vt == VT_I4) ? v.lVal : v.iVal;
                if (pct >= 0 && pct <= 100) {
                    result = pct;
                }
            }
            VariantClear(&v);
            obj->Release();
        }
        enumerator->Release();
    }

    cimv2_svc->Release();
    CoUninitialize();
    return result;
}
#endif

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

        // Update fan curve if in Curve mode
        // This must be called periodically to apply the fan curve
        {
            std::lock_guard lock(metrics_mutex_);
            fan_ctrl_.tick(current_metrics_.cpu.temp_c, current_metrics_.gpu.temp_c);
        }

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
#else
    // CPU utilization via /proc/stat (prev-idle/prev-total deltas; 0 on first sample)
    static procfs::CpuUtil cpu_util;
    current_metrics_.cpu.util_pct = cpu_util.sample();
#endif

    // CPU clock speed (platform-generic: WMI on Windows, cpufreq on Linux)
    current_metrics_.cpu.clock_mhz = platform_.cpu_clock_mhz();

    // CPU temperature via EC register 0x0470
    auto temp_result = platform_.ec_read(0x0470);
    if (temp_result) {
        current_metrics_.cpu.temp_c = static_cast<int>(temp_result.value());
    }

    // CPU package power via SMU PM table
    // TODO: Requires specific PM table command ID (not documented in tdp_test.cpp)
    // The blob's ioctl_send_smu_command reads from PM table but we need the command ID
    // for package power specifically (different from TDP limits)

    // SmartShift and PowerDistribution via ADLX (Linux reports an empty result)
    auto sys_result = platform_.system_metrics();
    if (sys_result) {
        const auto& sys = sys_result.value();
        current_metrics_.cpu.smart_shift = sys.smart_shift;
        current_metrics_.cpu.apu_shift_value = sys.apu_shift_value;
        current_metrics_.cpu.apu_shift_limit = sys.apu_shift_limit;
    }
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
        current_metrics_.gpu.vram_used_bytes = telemetry.vram_used_bytes;
        current_metrics_.gpu.vram_total_bytes = telemetry.vram_total_bytes;
    } else {
        // GPU metrics unavailable -- set to defaults
        current_metrics_.gpu = GpuMetrics{};
        log_warn("[metrics] GPU metrics unavailable (ADLX may have failed to initialize)");
    }
}

void MetricsPoller::poll_ram() {
    std::lock_guard lock(metrics_mutex_);

#ifdef _WIN32
    MEMORYSTATUSEX mem_status;
    mem_status.dwLength = sizeof(MEMORYSTATUSEX);
    if (GlobalMemoryStatusEx(&mem_status)) {
        // Store raw bytes
        current_metrics_.ram.total_bytes = mem_status.ullTotalPhys;
        current_metrics_.ram.avail_bytes = mem_status.ullAvailPhys;
        current_metrics_.ram.used_bytes = current_metrics_.ram.total_bytes - current_metrics_.ram.avail_bytes;
        current_metrics_.ram.load_pct = static_cast<float>(mem_status.dwMemoryLoad);
    }
#else
    auto mem = procfs::read_meminfo("/proc/meminfo");
    if (mem) {
        current_metrics_.ram.total_bytes = mem->total_bytes;
        current_metrics_.ram.avail_bytes = mem->avail_bytes;
        current_metrics_.ram.used_bytes = mem->used_bytes;
        current_metrics_.ram.load_pct = mem->load_pct;
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
#ifndef _WIN32
        // EC unavailable — fall back to sysfs power_supply on Linux.
        std::string src = procfs::power_source();
        if (src == "battery") {
            current_metrics_.power.mode = PowerState::Source::Battery;
            current_metrics_.power.label = "Battery only";
        } else if (src == "usb_c_slow") {
            current_metrics_.power.mode = PowerState::Source::UsbCSlow;
            current_metrics_.power.label = "USB-C (65W class)";
        } else if (src == "usb_c_fast") {
            current_metrics_.power.mode = PowerState::Source::UsbCFast;
            current_metrics_.power.label = "USB-C (100W class)";
        } else if (src == "dc_in") {
            current_metrics_.power.mode = PowerState::Source::DcIn;
            current_metrics_.power.label = "DC-In (dedicated charger)";
        } else {
            current_metrics_.power.mode = PowerState::Source::Unknown;
            current_metrics_.power.label = "Unknown";
        }
        log_debug("[metrics] Power state from sysfs: " + src);
#else
        current_metrics_.power.mode = PowerState::Source::Unknown;
        current_metrics_.power.label = "Unknown";
        log_warn("[metrics] Power state EC read failed (WMI may have failed)");
#endif
    }

    // Poll charge limit from EC register 0x04A3
    auto charge_result = platform_.ec_read(EC_CHARGE_LIMIT);
    if (charge_result) {
        current_metrics_.power.charge_limit_pct = static_cast<int>(charge_result.value());
    } else {
        current_metrics_.power.charge_limit_pct = std::nullopt;
    }

#ifdef _WIN32
    // Battery percentage via WMI Win32_Battery (driver-free)
    current_metrics_.power.battery_pct = battery_pct_wmi();
#else
    // Battery percentage via sysfs BAT*/capacity
    current_metrics_.power.battery_pct = procfs::battery_pct();
#endif
}
