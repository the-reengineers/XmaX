#pragma once

#include <systemd/sd-bus.h>

#include <atomic>
#include <thread>

class TransportService;
class Platform;

// DbusService — exposes org.xmax.Service on the D-Bus bus (system by default;
// user/session bus for headless dev via XMAX_DBUS_BUS=user).
//
// Each method delegates to TransportService::dispatch() so the CLI, the AF_UNIX
// socket, the test suite, and the GJS Shell extension share one command path.
// Only auto-start maps straight to platform.set_auto_start()/is_auto_start_enabled()
// (there is no transport command for it).
//
// Privileged methods (SetTdp/SetFan/SetChargeLimit/SetAutoStart) are gated by a
// polkit CheckAuthorization round-trip on org.freedesktop.PolicyKit1. Set
// XMAX_NO_POLKIT=1 to skip that check (dev/test only).
//
// Thread model: a single background thread drives sd_bus_process/sd_bus_wait and
// emits the periodic MetricsChanged signal, so all sd_bus_* calls happen on one
// thread. start()/stop() are safe to call from the main thread.
class DbusService {
public:
    DbusService(TransportService& transport, Platform& platform);
    ~DbusService();

    // Connect, request the bus name, register the object, start the loop thread.
    // Returns false (non-fatal) if the name could not be acquired — e.g. running
    // unprivileged without the D-Bus /usr/share/dbus-1/system.d policy installed.
    bool start();

    // Stop the loop thread and release the bus and the name.
    void stop();

    auto is_running() const -> bool;

private:
    void run();

    // sd-bus vtable callbacks (dispatched via userdata = this).
    static int on_ping(sd_bus_message* m, void* userdata, sd_bus_error* ret_error);
    static int on_get_metrics(sd_bus_message* m, void* userdata, sd_bus_error* ret_error);
    static int on_set_tdp(sd_bus_message* m, void* userdata, sd_bus_error* ret_error);
    static int on_set_fan(sd_bus_message* m, void* userdata, sd_bus_error* ret_error);
    static int on_set_charge_limit(sd_bus_message* m, void* userdata, sd_bus_error* ret_error);
    static int on_get_charge_limit(sd_bus_message* m, void* userdata, sd_bus_error* ret_error);
    static int on_set_auto_start(sd_bus_message* m, void* userdata, sd_bus_error* ret_error);

    // Profile + fan-curve methods (JSON payloads, same shape as the socket protocol).
    static int on_get_profiles(sd_bus_message* m, void* userdata, sd_bus_error* ret_error);
    static int on_set_profile(sd_bus_message* m, void* userdata, sd_bus_error* ret_error);
    static int on_save_profile(sd_bus_message* m, void* userdata, sd_bus_error* ret_error);
    static int on_delete_profile(sd_bus_message* m, void* userdata, sd_bus_error* ret_error);
    static int on_get_fan_curves(sd_bus_message* m, void* userdata, sd_bus_error* ret_error);
    static int on_save_fan_curve(sd_bus_message* m, void* userdata, sd_bus_error* ret_error);
    static int on_delete_fan_curve(sd_bus_message* m, void* userdata, sd_bus_error* ret_error);

    static int on_get_version(sd_bus* bus, const char* path, const char* interface,
                              const char* property, sd_bus_message* reply,
                              void* userdata, sd_bus_error* ret_error);
    static int on_get_auto_start(sd_bus* bus, const char* path, const char* interface,
                                 const char* property, sd_bus_message* reply,
                                 void* userdata, sd_bus_error* ret_error);

    // polkit CheckAuthorization for privileged methods.
    bool authorize(sd_bus_message* m, const char* action_id);

    // Emit the MetricsChanged(a{sv}) signal ({"metrics": "<json>"}).
    void emit_metrics_changed();

    TransportService& transport_;
    Platform& platform_;

    sd_bus* bus_ = nullptr;       // Service connection (owns the name).
    sd_bus* auth_bus_ = nullptr;  // Separate connection for polkit calls.
    bool no_polkit_ = false;      // XMAX_NO_POLKIT dev/test seam.

    std::thread thread_;
    std::atomic<bool> running_{false};
};