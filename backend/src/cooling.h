// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
#pragma once

#include "shared.h"
#include "platform/platform.h"

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>

// FrostBayController — BLE interface to the Frost Bay external cooling device.
//
// Responsibilities:
//   - Connect to CoolingSystem_ONEC1 over BLE via SimpleBLE
//   - Parse device status buffers (≥41 bytes, fixed-offset layout)
//   - Read-modify-write control fields (mode, fan%, pump%, RGB, curve)
//   - Cache latest state; provide snapshot via read_state()
//   - Handle connection lifecycle transparently: connect, reconnect
//
// Connection strategy:
//   - At boot we connect ONLY to an already-paired device. BlueZ keeps the
//     Frost Bay device object across boots, so the steady state performs no
//     radio discovery at all. Scanning churns BlueZ device objects for every
//     nearby Bluetooth device, so we avoid it unless it is needed.
//   - The scan fallback is armed by ensure_started() on the first explicit
//     cooling command (e.g. `xmaxctl cooling status`). First-ever use scans,
//     finds the cooler, and connecting implicitly pairs it — subsequent boots
//     take the silent paired path.
//   - The device's 0xFFE1 characteristic has NO notify support (no CCCD);
//     we normally run in polling mode (tick() performs the GATT read, like
//     the validated Python reference implementation). If firmware ever
//     exposes notify, we subscribe and tick() skips reads while fresh.
//   - The cooler also advertises a HID-over-GATT service (0x1812). Whenever
//     it is connected, bluetoothd attaches the HOG input profile and the
//     cooler's ~1Hz telemetry reports reach the kernel as phantom
//     consumer-control (volume-down) key events. Two layers silence them:
//     the packaged udev rule 60-xmax-frostbay.rules (LIBINPUT_IGNORE_DEVICE —
//     covers libinput consumers: mutter/GNOME, KDE, wlroots), and this
//     controller taking an exclusive EVIOCGRAB on the phantom evdev node for
//     the lifetime of the connection — which additionally starves raw-evdev
//     consumers (keyd & co) that ignore udev properties. If another daemon
//     already holds the grab, the controller escalates to unbinding the HID
//     driver, which unregisters the input device for everyone.
//
// Design:
//   - Follows existing controller pattern: constructor takes Platform&,
//     mutex-guarded state, tick() called by MetricsPoller.
//   - SimpleBLE objects live behind an opaque pimpl (Ble) so SimpleBLE headers
//     don't leak into every consumer of cooling.h. Only the connection-loop
//     thread mutates them; GATT callers copy the (shared_ptr-based, cheap)
//     handles out under ble_mutex_ and serialize operations under gatt_mutex_.
//   - Teardown happens on the owning thread — stop() just clears running_
//     and joins: the connection loop tears down BLE, the phantom-HID watcher
//     (Linux) releases its evdev grab. There is no cross-thread teardown.
//   - Missing/non-present device is never an error: connected_ stays false,
//     tick() is a no-op, read_state() returns default state.
//
// Thread safety: all public methods are safe to call from any thread.

#ifdef __linux__
// Locate the evdev node ("/dev/input/eventN") of the input device whose
// sysfs `name` file exactly matches `name` (the phantom HID node BlueZ
// creates for the cooler). `sysfs_root` is injectable for tests.
// Returns nullopt when the device is absent.
auto find_input_event_node(const std::string& name,
                           const std::string& sysfs_root = "/sys") -> std::optional<std::string>;
#endif

class FrostBayController {
public:
    explicit FrostBayController(Platform& platform);
    ~FrostBayController();

    // ---- Lifecycle ----
    // Start the background connection loop (paired-device connects only — no
    // radio discovery). Non-blocking; safe to call even when the Frost Bay is
    // powered off or no Bluetooth hardware exists.
    void start();

    // Ensure the loop is running AND arm the scan fallback. Called on the
    // first cooling transport/D-Bus command: machines without a Frost Bay
    // never run discovery unless the user explicitly asks for cooling.
    void ensure_started();

    // Stop the connection thread and disconnect BLE. Teardown runs on the
    // loop thread; this blocks until the thread has exited.
    void stop();

    auto is_running() const -> bool;

    // ---- State ----
    auto is_connected() const -> bool;
    auto read_state() -> FrostBayState;

    // ---- Control (read-modify-write over BLE) ----
    // All return Result<void>. No-op + warning log if not connected.
    auto set_mode(int mode) -> Result<void>;
    auto set_fan_percent(int pct) -> Result<void>;
    auto set_pump_percent(int pct) -> Result<void>;
    // Exact write: all six RGB fields are always sent, including zeros and
    // enable=false (write_settings() instead treats 0 as "leave untouched").
    auto set_rgb(bool enable, int mode, int level, int r, int g, int b) -> Result<void>;
    auto set_curve(const std::vector<FrostBayState::CurvePoint>& curve) -> Result<void>;

    // Bulk write: patch only the non-default fields from `changes`.
    // This is the transport-layer entry point — `set_cooling` calls this.
    auto write_settings(const FrostBayState& changes) -> Result<void>;

    // ---- Tick (called by MetricsPoller every 2s) ----
    // Polling mode: GATT-reads device status each tick. With an active and
    // fresh notify subscription the read is skipped. No-op if not connected.
    void tick();

private:
    // Protocol constants (decoded from vendor traffic, verified on-device)
    static constexpr uint8_t WRITE_CMD_STATUS = 0x02;
    static constexpr int READ_MIN_LEN = 41;
    static constexpr int WRITE_BUF_LEN = 57;
    static constexpr int FRAG_PAYLOAD_LEN = 19;  // 20-byte fragment = 1 header + 19 payload
    static constexpr int MODE_OFF = 0;
    static constexpr int MODE_INTELLIFAN = 254;
    static constexpr int MODE_FIXED_SPEED = 255;

    // Read-layout offsets (device → host)
    static constexpr int R_VERSION = 2, R_MODE = 4, R_FAN_PERCENT = 5;
    static constexpr int R_FAN_SPEED = 6, R_PUMP_PERCENT = 8, R_PUMP_SPEED = 9;
    static constexpr int R_WATER_FLOW = 11, R_IN_TEMP = 13, R_OUT_TEMP = 14;
    static constexpr int R_STATUS_FLAG = 15, R_RGB_MODE = 16, R_RGB_ENABLE = 17;
    static constexpr int R_RGB_LEVEL = 19, R_RGB_R = 20, R_RGB_G = 21, R_RGB_B = 22;
    static constexpr int R_CURVE_T1 = 23;  // then alternating t,f … t9@39,f9@40

    // Write-layout offsets (== read offset − 1, cmd byte replaces 2 header bytes)
    static constexpr int W_MODE = R_MODE - 1;            // 3
    static constexpr int W_FAN_PERCENT = R_FAN_PERCENT - 1;  // 4
    static constexpr int W_PUMP_PERCENT = R_PUMP_PERCENT - 1;// 7
    static constexpr int W_RGB_MODE = R_RGB_MODE - 1;        // 15
    static constexpr int W_RGB_ENABLE = R_RGB_ENABLE - 1;    // 16
    static constexpr int W_RGB_LEVEL = R_RGB_LEVEL - 1;      // 18
    static constexpr int W_RGB_R = R_RGB_R - 1;              // 19
    static constexpr int W_RGB_G = R_RGB_G - 1;              // 20
    static constexpr int W_RGB_B = R_RGB_B - 1;              // 21
    static constexpr int W_CURVE_T1 = R_CURVE_T1 - 1;        // 22

    // ---- Internal helpers ----
    auto parse_status(const std::vector<uint8_t>& buf) -> FrostBayState;
    auto build_write_buffer(const std::vector<uint8_t>& current, const FrostBayState& changes)
        -> std::vector<uint8_t>;
    auto fragment(const std::vector<uint8_t>& logical) -> std::vector<std::vector<uint8_t>>;

    // ---- BLE ----
    // SimpleBLE handles, defined in cooling.cpp (compilation firewall —
    // keeps SimpleBLE includes out of this header).
    struct Ble;

    void connection_loop();                              // runs in connect_thread_
    auto connect_to_device() -> bool;                    // paired-first → optional scan → connect → setup
    void disconnect_device();                            // loop-thread-only teardown
    auto read_device_status() -> bool;                   // GATT read (takes gatt_mutex_)
    auto read_status_locked() -> bool;                   // GATT read (caller holds gatt_mutex_)
    auto write_fragments_locked(const std::vector<uint8_t>& logical) -> bool;
    void interruptible_sleep(std::chrono::milliseconds d);
    void wake_loop();

    // ---- Phantom-HID neutralization (Linux only) ----
    // A dedicated watcher thread owns everything below. It scans sysfs for
    // the uhid input node BlueZ creates for the cooler's HID-over-GATT
    // service and neutralizes it — exclusive EVIOCGRAB, so no consumer
    // (libinput or raw-evdev like keyd) sees the volume-key telemetry. If
    // another daemon grabbed first (EBUSY), escalate to a hid-generic unbind,
    // which unregisters the node underneath it. Running independently of the
    // connection loop means the node is caught within one scan interval of
    // appearing — even when the connection was initiated by someone else.
#ifdef __linux__
    void phantom_hid_watch();                               // runs in phantom_thread_
    auto grab_phantom_hid(const std::string& dev) -> bool;  // single grab/unbind attempt
    auto unbind_phantom_hid() -> bool;
    void drain_phantom_hid();                               // discard queued events
    void release_phantom_hid();                             // close fd / reset state
#endif

    Platform& platform_;

    // Cached device state (mutex_)
    mutable std::mutex mutex_;
    FrostBayState state_;
    std::vector<uint8_t> last_read_buffer_;             // raw buffer for read-modify-write
    std::chrono::steady_clock::time_point last_notification_{};

    // BLE handles: ble_mutex_ guards *ble_; only the loop thread changes
    // membership. GATT callers copy handles out and serialize the actual
    // operations under gatt_mutex_ (a full read-modify-write is atomic).
    mutable std::mutex ble_mutex_;
    std::unique_ptr<Ble> ble_;
    std::mutex gatt_mutex_;

    // Lifecycle
    std::mutex lifecycle_mutex_;                        // serializes start/ensure_started/stop
    std::thread connect_thread_;
#ifdef __linux__
    std::thread phantom_thread_;                        // phantom-HID watcher
#endif
    std::atomic<bool> running_{false};
    std::atomic<bool> connected_{false};
    std::atomic<bool> scan_enabled_{false};             // armed by ensure_started()
    std::atomic<bool> notify_active_{false};

    // Interruptible sleeping for the connection loop
    std::mutex cv_mutex_;
    std::condition_variable cv_;
    bool wake_now_ = false;

    // Loop-thread only: log throttling for repeated connect failures
    int consecutive_failures_ = 0;
    bool no_adapter_warned_ = false;

#ifdef __linux__
    // Watcher-thread only (phantom_hid_watch): phantom-HID state.
    int phantom_hid_fd_ = -1;             // open + grabbed evdev fd
    std::string phantom_hid_path_;
    bool phantom_hid_done_ = false;       // stop retrying until the node cycles
    bool phantom_hid_unbind_ = false;     // neutralized via driver unbind
    uint64_t phantom_hid_events_ = 0;     // discarded input events (diagnostics)
    bool phantom_hid_logged_ = false;     // first-batch log emitted
#endif
};
