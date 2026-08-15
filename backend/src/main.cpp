#include "platform/platform.h"
#include "config.h"
#include "profiles.h"
#include "fan.h"
#include "tdp.h"
#include "metrics.h"
#include "power.h"
#include "adaptive.h"
#include "button.h"
#include "process.h"
#include "transport.h"
#include "tray.h"

#include <atomic>
#include <chrono>
#include <csignal>
#include <exception>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <memory>
#include <mutex>

#ifdef _WIN32
#include <windows.h>
#include <shellapi.h>
#endif

namespace fs = std::filesystem;

#ifdef _WIN32
// Check if current process is running elevated
static bool is_elevated() {
    BOOL is_admin = FALSE;
    SID_IDENTIFIER_AUTHORITY nt_authority = SECURITY_NT_AUTHORITY;
    PSID admin_group = nullptr;

    if (AllocateAndInitializeSid(&nt_authority, 2,
                                  SECURITY_BUILTIN_DOMAIN_RID,
                                  DOMAIN_ALIAS_RID_ADMINS,
                                  0, 0, 0, 0, 0, 0, &admin_group)) {
        if (!CheckTokenMembership(nullptr, admin_group, &is_admin)) {
            is_admin = FALSE;
        }
        FreeSid(admin_group);
    }

    return is_admin != FALSE;
}

// Re-launch self as elevated using ShellExecute
static bool relaunch_elevated(int argc, char* argv[]) {
    // Get current executable path
    wchar_t exe_path[MAX_PATH];
    if (GetModuleFileNameW(nullptr, exe_path, MAX_PATH) == 0) {
        return false;
    }

    // Build command line from argv
    std::wstring cmd_line;
    for (int i = 1; i < argc; ++i) {
        if (i > 1) cmd_line += L" ";
        // Convert char* to wchar_t*
        int len = MultiByteToWideChar(CP_UTF8, 0, argv[i], -1, nullptr, 0);
        if (len > 0) {
            std::wstring arg(len, L'\0');
            MultiByteToWideChar(CP_UTF8, 0, argv[i], -1, &arg[0], len);
            cmd_line += arg;
        }
    }

    // Launch elevated with "runas" verb
    HINSTANCE result = ShellExecuteW(
        nullptr,
        L"runas",
        exe_path,
        cmd_line.empty() ? nullptr : cmd_line.c_str(),
        nullptr,
        SW_SHOWNORMAL
    );

    // ShellExecute returns > 32 on success
    return (intptr_t)result > 32;
}
#endif

// Global flag for signal handling
static std::atomic<bool> g_shutdown_requested{false};
static Platform* g_platform = nullptr;

// Crash logging
static fs::path g_crash_log_path;
static std::mutex g_crash_mutex;

static void write_crash_log(const std::string& message) {
    std::lock_guard lock(g_crash_mutex);
    try {
        std::ofstream log(g_crash_log_path, std::ios::app);
        if (log.is_open()) {
            auto now = std::chrono::system_clock::now();
            auto time = std::chrono::system_clock::to_time_t(now);
            char buf[64];
            std::strftime(buf, sizeof(buf), "%Y-%m-%d %H:%M:%S", std::localtime(&time));
            log << "[" << buf << "] " << message << "\n";
        }
    } catch (...) {
        // Can't log a crash log failure
    }
}

// Signal handler for graceful shutdown
static void signal_handler(int) {
    g_shutdown_requested.store(true);
    if (g_platform) {
        g_platform->quit_message_loop();
    }
}

// Terminate handler for uncaught exceptions
static void on_terminate() {
    std::string msg = "Uncaught exception";
    try {
        auto eptr = std::current_exception();
        if (eptr) {
            std::rethrow_exception(eptr);
        }
    } catch (const std::exception& e) {
        msg = std::string("Uncaught exception: ") + e.what();
    } catch (...) {
        msg = "Uncaught non-standard exception";
    }
    write_crash_log(msg);
    std::abort();
}

int main(int argc, char* argv[]) {
#ifdef _WIN32
    // Check if running elevated; if not, re-launch self as elevated
    if (!is_elevated()) {
        std::cout << "Requesting elevated permissions...\n";
        if (relaunch_elevated(argc, argv)) {
            // Successfully launched elevated instance, exit this one
            return 0;
        } else {
            // Failed to elevate (user cancelled or other error)
            // Exit immediately - backend requires elevation for hardware access
            std::cerr << "Error: Elevated permissions required. Exiting.\n";
            return 1;
        }
    }
#endif

    // Create platform instance
    auto platform = create_platform();
    g_platform = platform.get();

    // Set up crash logging
    g_crash_log_path = platform->data_dir() / "backend_crash.log";
    try {
        fs::create_directories(platform->data_dir());
        // Overwrite on startup
        std::ofstream log(g_crash_log_path, std::ios::trunc);
        auto now = std::chrono::system_clock::now();
        auto time = std::chrono::system_clock::to_time_t(now);
        char buf[64];
        std::strftime(buf, sizeof(buf), "%Y-%m-%d %H:%M:%S", std::localtime(&time));
        log << "[" << buf << "] Session started\n";
    } catch (...) {}

    // Install terminate handler for uncaught exceptions
    std::set_terminate(on_terminate);

    try {
        // Initialize hardware connections (PawnIO, ADLX, Task Scheduler)
        // Failures are non-fatal -- hardware methods will return errors at call time
        bool hw_init = platform->init_hardware();
        if (!hw_init) {
            std::cerr << "Warning: Some hardware initialization failed (see warnings above)" << std::endl;
        }

        // Set up signal handlers for graceful shutdown
        std::signal(SIGINT, signal_handler);
        std::signal(SIGTERM, signal_handler);

        // Single instance check
        auto lock_result = platform->single_instance_lock();
        if (!lock_result) {
            std::cerr << "Another instance is already running" << std::endl;
            return 1;
        }

        // Create data directory
        auto data_path = platform->data_dir();
        fs::create_directories(data_path);

        // Load config and profiles
        auto config_path = data_path / "config.json";
        auto profiles_path = data_path / "profiles.json";

        Config config = load_config(config_path);
        ProfileStorage profiles = load_profiles(profiles_path);

        std::cout << "XmaX Backend starting..." << std::endl;
        std::cout << "Data directory: " << data_path.string() << std::endl;
        std::cout << "Config: persist=" << (config.persist ? "true" : "false") << std::endl;

        // Create controllers
        auto fan = std::make_unique<FanController>(*platform);
        auto tdp = std::make_unique<TdpController>(*platform);
        auto poller = std::make_unique<MetricsPoller>(*platform, *fan, *tdp);
        auto power = std::make_unique<PowerController>(*platform);
        auto adaptive = std::make_unique<AdaptiveController>(*tdp, *fan);
        auto button = std::make_unique<ButtonMonitor>(*platform);
        auto process_mgr = std::make_unique<ProcessManager>(*platform);
        auto tray = std::make_unique<TrayManager>(*platform);

        // Detect current power state
        power->update_power_state();
        auto current_power_state = power->current_state();
        std::cout << "Power state: " << static_cast<int>(current_power_state) << std::endl;

        // Helper to get TDP ceiling for a power state from config
        auto get_power_state_tdp = [&](PowerState::Source state) -> int {
            switch (state) {
                case PowerState::Source::Battery:
                    return config.power_state_profiles.battery.tdp_max_w;
                case PowerState::Source::UsbCSlow:
                    return config.power_state_profiles.usb_c_slow.tdp_max_w;
                case PowerState::Source::UsbCFast:
                    return config.power_state_profiles.usb_c_fast.tdp_max_w;
                case PowerState::Source::DcIn:
                    return config.power_state_profiles.dc_in.tdp_max_w;
                default:
                    return 55; // Safe default for unknown state
            }
        };

        // If session_persist=true, apply settings from config
        if (config.session_persist) {
            std::cout << "Applying persisted settings..." << std::endl;

            // Set initial power state TDP ceiling for adaptive controller
            int initial_tdp_ceiling = get_power_state_tdp(current_power_state);
            adaptive->set_power_state_ceiling(initial_tdp_ceiling);
            std::cout << "Initial power state TDP ceiling: " << initial_tdp_ceiling << "W" << std::endl;

            // Apply charge limit
            if (config.charge_limit_pct >= 75 && config.charge_limit_pct <= 100) {
                auto cl_result = power->write_charge_limit(static_cast<uint8_t>(config.charge_limit_pct));
                if (!cl_result) {
                    std::cerr << "Failed to apply charge limit" << std::endl;
                }
            }

            // Get power state profile
            std::string profile_slug;
            int tdp_max_w = 55;  // OneXConsole default for Battery (normal)
            switch (current_power_state) {
                case PowerState::Source::Battery:
                    profile_slug = config.power_state_profiles.battery.profile;
                    tdp_max_w = config.power_state_profiles.battery.tdp_max_w;
                    break;
                case PowerState::Source::UsbCSlow:
                    profile_slug = config.power_state_profiles.usb_c_slow.profile;
                    tdp_max_w = config.power_state_profiles.usb_c_slow.tdp_max_w;
                    break;
                case PowerState::Source::UsbCFast:
                    profile_slug = config.power_state_profiles.usb_c_fast.profile;
                    tdp_max_w = config.power_state_profiles.usb_c_fast.tdp_max_w;
                    break;
                case PowerState::Source::DcIn:
                    profile_slug = config.power_state_profiles.dc_in.profile;
                    tdp_max_w = config.power_state_profiles.dc_in.tdp_max_w;
                    break;
                default:
                    break;
            }

            // Apply profile if configured
            if (!profile_slug.empty()) {
                auto it = profiles.profiles.find(profile_slug);
                if (it != profiles.profiles.end()) {
                    const auto& profile = it->second;

                    // Apply TDP limits
                    auto tdp_result = tdp->write_tdp(profile.stapm_w, profile.fast_w, profile.slow_w);
                    if (!tdp_result) {
                        std::cerr << "Failed to apply TDP limits" << std::endl;
                    }

                    // Apply fan curve
                    if (profile.fan_curve.has_value()) {
                        auto curve_it = profiles.fan_curves.find(profile.fan_curve.value());
                        if (curve_it != profiles.fan_curves.end()) {
                            (void)fan->set_mode(FanState::Mode::Curve);
                            fan->set_curve(curve_it->second);
                        }
                    } else {
                        (void)fan->set_mode(FanState::Mode::Auto);
                    }

                    std::cout << "Applied profile: " << profile.name << std::endl;
                }
            }

            // Restore adaptive controller if was active
            if (config.auto_tune.has_value() && config.auto_tune->enabled) {
                const auto& at = config.auto_tune.value();
                TuningPreset preset = TuningPreset::Default;
                if (at.tuning == "silent") preset = TuningPreset::Silent;
                else if (at.tuning == "performance") preset = TuningPreset::Performance;

                adaptive->activate(preset, at.target_temp_c, at.tdp_max_w, at.fan_max_pct);
                std::cout << "Restored adaptive controller: " << at.tuning << std::endl;
            }
        } else {
            std::cout << "Persist disabled -- hardware at BIOS defaults" << std::endl;
        }

        // Start background threads
        poller->start();
        std::cout << "Metrics poller started" << std::endl;

        adaptive->start();
        std::cout << "Adaptive controller started" << std::endl;

        button->init_app_fun_en();
        button->start();
        std::cout << "Button monitor started" << std::endl;

        process_mgr->start_monitor();
        std::cout << "Process monitor started" << std::endl;

        // Create transport service and start listening BEFORE spawning frontend,
        // so the pipe is ready when the frontend tries to connect.
        auto transport = std::make_unique<TransportService>(
            *platform, *poller, *fan, *tdp, *power, *adaptive, *button,
            config, profiles, config_path, profiles_path
        );
        transport->start();
        std::cout << "Transport server started" << std::endl;

        // Spawn frontend (look for XmaX.exe next to xmaxsvc.exe)
        auto exe_path = fs::path(platform->self_exe_path()).parent_path() / "XmaX.exe";
        if (fs::exists(exe_path)) {
            auto spawn_result = process_mgr->spawn(exe_path);
            if (!spawn_result) {
                std::cerr << "Failed to spawn frontend" << std::endl;
            } else {
                std::cout << "Frontend spawned (hidden)" << std::endl;
            }
        } else {
            std::cerr << "Frontend executable not found: " << exe_path.string() << std::endl;
        }

        // Wire up callbacks
        // Power state change → update adaptive controller TDP ceiling (if session_persist enabled)
        power->on_state_change([&](PowerState::Source new_state, PowerState::Source /*old_state*/) {
            if (transport->is_session_persist()) {
                int new_tdp_ceiling = get_power_state_tdp(new_state);
                adaptive->set_power_state_ceiling(new_tdp_ceiling);
                std::cout << "[power] State changed, new TDP ceiling: " << new_tdp_ceiling << "W" << std::endl;

                // Send power_mode_change event to frontend
                Event evt;
                evt.event = "power_mode_change";
                evt.data = "{}";
                transport->send_event(evt);
            } else {
                std::cout << "[power] State changed (session_persist disabled, skipping HW update)" << std::endl;
            }
        });

        // Button press → send toggle event to frontend (frontend manages its own visibility)
        button->on_visibility_change([&](bool /*visible*/) {
            std::cout << "[button] Visibility change callback" << std::endl;
            Event evt;
            evt.event = "show_toggle";
            evt.data = "{}";
            transport->send_event(evt);
        });

        // Tray left-click → send toggle event to frontend (frontend manages its own visibility)
        tray->on_toggle([&]() {
            std::cout << "[tray] Toggle callback fired" << std::endl;
            Event evt;
            evt.event = "show_toggle";
            evt.data = "{}";
            transport->send_event(evt);
        });

        // Tray quit → shutdown
        tray->on_quit([&]() {
            g_shutdown_requested.store(true);
            platform->quit_message_loop();
        });

        // Start tray icon
        auto tray_result = tray->start();
        if (!tray_result) {
            std::cerr << "Failed to create tray icon" << std::endl;
        } else {
            std::cout << "Tray icon created" << std::endl;
        }

        std::cout << "XmaX Backend ready" << std::endl;

        // Enter message loop (blocks until quit)
        platform->run_message_loop();

        std::cout << "XmaX Backend shutting down..." << std::endl;

        // Stop everything
        transport->stop();
        tray->stop();
        process_mgr->stop_monitor();
        button->stop();
        adaptive->stop();
        poller->stop();

        // Release single instance lock
        platform->release_instance_lock(*lock_result);

        std::cout << "XmaX Backend stopped" << std::endl;
        return 0;
    } catch (const std::exception& e) {
        write_crash_log(std::string("Fatal exception: ") + e.what());
        return 1;
    } catch (...) {
        write_crash_log("Fatal unknown exception");
        return 1;
    }
}
