// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
#include "cooling.h"
#include "logger.h"

#include <simpleble/SimpleBLE.h>

#include <algorithm>
#include <cerrno>
#include <chrono>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <optional>
#include <string>

#ifdef __linux__
#include <fcntl.h>
#include <linux/input.h>
#include <sys/ioctl.h>
#include <unistd.h>
#endif

using namespace std::chrono_literals;

// ---- Helpers ----

// Read a big-endian uint16_t from a byte buffer at offset.
static uint16_t read_be16(const std::vector<uint8_t>& buf, int offset) {
    if (offset + 1 >= static_cast<int>(buf.size())) return 0;
    return static_cast<uint16_t>((static_cast<uint16_t>(buf[offset]) << 8) | buf[offset + 1]);
}

// Write a uint8_t to a byte buffer at offset.
static void patch_u8(std::vector<uint8_t>& buf, int offset, uint8_t value) {
    if (offset >= 0 && offset < static_cast<int>(buf.size())) {
        buf[offset] = value;
    }
}

// Frost Bay BLE characteristic UUID: 0xFFE1
static const char* kCharUuid = "0000ffe1-0000-1000-8000-00805f9b34fb";
// Device identifier (advertised name)
static const char* kDeviceName = "CoolingSystem_ONEC1";

#ifdef __linux__
// ---- Phantom-HID lookup helpers ----
// BlueZ's HOG plugin turns the cooler's HID-over-GATT service (0x1812) into a
// uhid input device literally named after the peripheral. Find its sysfs
// directory by scanning <sysfs_root>/class/input/*/name.

static auto find_input_sysfs_dir(const std::string& name, const std::string& sysfs_root)
    -> std::optional<std::filesystem::path> {
    namespace fs = std::filesystem;
    std::error_code ec;
    fs::path input_root = fs::path(sysfs_root) / "class" / "input";
    auto it = fs::directory_iterator(input_root, ec);
    for (; !ec && it != fs::directory_iterator(); it.increment(ec)) {
        std::ifstream name_file(it->path() / "name");
        std::string dev_name;
        if (!std::getline(name_file, dev_name)) continue;
        if (dev_name == name) return it->path();
    }
    return std::nullopt;
}

auto find_input_event_node(const std::string& name, const std::string& sysfs_root)
    -> std::optional<std::string> {
    namespace fs = std::filesystem;
    auto input_dir = find_input_sysfs_dir(name, sysfs_root);
    if (!input_dir) return std::nullopt;

    // The eventN child (symlink to a directory on real sysfs) names the
    // /dev/input node.
    std::error_code ec;
    auto it = fs::directory_iterator(*input_dir, ec);
    for (; !ec && it != fs::directory_iterator(); it.increment(ec)) {
        std::string fname = it->path().filename().string();
        if (fname.rfind("event", 0) == 0) {
            return std::string("/dev/input/") + fname;
        }
    }
    return std::nullopt;
}
#endif  // __linux__

// SimpleBLE objects, hidden behind the opaque Ble pimpl declared in cooling.h.
// Only the connection-loop thread mutates membership; other threads copy the
// (shared_ptr-based) handles out under ble_mutex_ and use them under
// gatt_mutex_. Copies keep the underlying objects alive even if the loop
// disconnects concurrently.
struct FrostBayController::Ble {
    std::optional<SimpleBLE::Adapter> adapter;
    std::optional<SimpleBLE::Peripheral> peripheral;
    std::string service_uuid;
    bool use_write_command = true;   // 0xFFE1 supports write-without-response?
};

// ===== Construction / Destruction =====

FrostBayController::FrostBayController(Platform& platform)
    : platform_(platform)
{
}

FrostBayController::~FrostBayController() {
    stop();
}

// ===== Lifecycle =====

void FrostBayController::start() {
    std::lock_guard lock(lifecycle_mutex_);
    if (running_.load()) return;

    running_.store(true);
    connect_thread_ = std::thread(&FrostBayController::connection_loop, this);
#ifdef __linux__
    phantom_thread_ = std::thread(&FrostBayController::phantom_hid_watch, this);
#endif
}

void FrostBayController::ensure_started() {
    std::lock_guard lock(lifecycle_mutex_);

    bool newly_armed = !scan_enabled_.exchange(true);
    if (!running_.load()) {
        running_.store(true);
        connect_thread_ = std::thread(&FrostBayController::connection_loop, this);
#ifdef __linux__
        phantom_thread_ = std::thread(&FrostBayController::phantom_hid_watch, this);
#endif
        newly_armed = false;  // fresh loop reads scan_enabled_ anyway
    }
    if (newly_armed) {
        log_info("[frostbay] Cooling command received — BLE scan fallback armed");
    }

    // Cut any in-progress backoff short so the scan path runs promptly.
    wake_loop();
}

void FrostBayController::stop() {
    {
        std::lock_guard lock(lifecycle_mutex_);
        if (!running_.exchange(false)) return;
    }

    wake_loop();

    // The loop thread performs all BLE teardown before exiting — there is
    // deliberately no disconnect from this thread (that race used to
    // double-free the peripheral handle). The phantom-HID watcher releases
    // its own grab before exiting.
    if (connect_thread_.joinable()) {
        connect_thread_.join();
    }
#ifdef __linux__
    if (phantom_thread_.joinable()) {
        phantom_thread_.join();
    }
#endif
}

auto FrostBayController::is_running() const -> bool {
    return running_.load();
}

void FrostBayController::wake_loop() {
    {
        std::lock_guard lock(cv_mutex_);
        wake_now_ = true;
    }
    cv_.notify_all();
}

void FrostBayController::interruptible_sleep(std::chrono::milliseconds d) {
    std::unique_lock lock(cv_mutex_);
    cv_.wait_for(lock, d, [this] { return !running_.load() || wake_now_; });
    wake_now_ = false;
}

// ===== State =====

auto FrostBayController::is_connected() const -> bool {
    return connected_.load();
}

auto FrostBayController::read_state() -> FrostBayState {
    std::lock_guard lock(mutex_);
    return state_;
}

// ===== Protocol: parse_status =====
// Mirrors the validated Python reference implementation — reads fixed offsets from a ≥41-byte buffer,
// decodes big-endian 16-bit fields, returns FrostBayState.

auto FrostBayController::parse_status(const std::vector<uint8_t>& buf) -> FrostBayState {
    FrostBayState s;
    s.connected = true;

    if (static_cast<int>(buf.size()) < READ_MIN_LEN) {
        s.connected = false;
        return s;
    }

    s.fw_version    = static_cast<int>(buf[R_VERSION]);
    s.mode          = static_cast<int>(buf[R_MODE]);
    s.fan_speed_percent  = static_cast<int>(buf[R_FAN_PERCENT]);
    s.fan_speed_rpm      = read_be16(buf, R_FAN_SPEED);
    s.pump_speed_percent = static_cast<int>(buf[R_PUMP_PERCENT]);
    s.pump_speed_rpm     = read_be16(buf, R_PUMP_SPEED);
    s.water_flow         = read_be16(buf, R_WATER_FLOW);
    s.in_water_temp_c    = static_cast<int>(buf[R_IN_TEMP]);
    s.out_water_temp_c   = static_cast<int>(buf[R_OUT_TEMP]);
    s.status_flag        = static_cast<int>(buf[R_STATUS_FLAG]);
    s.rgb_mode    = static_cast<int>(buf[R_RGB_MODE]);
    s.rgb_enable  = (buf[R_RGB_ENABLE] == 1);
    s.rgb_level   = static_cast<int>(buf[R_RGB_LEVEL]);
    s.rgb_r = static_cast<int>(buf[R_RGB_R]);
    s.rgb_g = static_cast<int>(buf[R_RGB_G]);
    s.rgb_b = static_cast<int>(buf[R_RGB_B]);

    // Parse 9-point curve (t1,f1 … t9,f9 at offsets 23–40)
    s.curve.clear();
    for (int i = 0; i < 9; ++i) {
        int t_off = R_CURVE_T1 + i * 2;
        int f_off = t_off + 1;
        if (f_off < static_cast<int>(buf.size())) {
            s.curve.push_back({static_cast<int>(buf[t_off]), static_cast<int>(buf[f_off])});
        }
    }

    // Derived: is_running = pump speed > 1 && status flag not a fault
    s.is_running = (s.pump_speed_rpm > 1)
                && (s.status_flag != 2)
                && (s.status_flag != 64);

    return s;
}

// ===== Protocol: build_write_buffer =====
// Mirrors the validated Python reference implementation. Constructs the 57-byte logical write buffer:
//   [0] = 0x02 (command byte)
//   [1..] seeded from the current read buffer (read-modify-write), shifted by one
//   Patches only the non-zero/non-default fields from `changes`
//   Curve fields (t1..f9 at offsets 22..39) are only written when mode == 254

auto FrostBayController::build_write_buffer(
    const std::vector<uint8_t>& current, const FrostBayState& changes)
    -> std::vector<uint8_t>
{
    std::vector<uint8_t> buf(WRITE_BUF_LEN, 0);

    // [0] = command byte
    buf[0] = WRITE_CMD_STATUS;

    // Seed from current read buffer (shifted by one: write[k] = read[k+1])
    // Copy up to min(current.size()-1, WRITE_BUF_LEN-1) bytes
    int copy_len = std::min(static_cast<int>(current.size()) - 1, WRITE_BUF_LEN - 1);
    if (copy_len > 0) {
        std::memcpy(buf.data() + 1, current.data() + 1, static_cast<size_t>(copy_len));
    }

    // Patch writable fields from `changes` (only non-default values)

    // mode (always write if changes.mode != 0)
    if (changes.mode != 0) {
        patch_u8(buf, W_MODE, static_cast<uint8_t>(changes.mode));
    }

    // fan_speed_percent (only honored in mode 255, but we still write it)
    if (changes.fan_speed_percent != 0) {
        patch_u8(buf, W_FAN_PERCENT, static_cast<uint8_t>(changes.fan_speed_percent));
    }

    // pump_speed_percent
    if (changes.pump_speed_percent != 0) {
        patch_u8(buf, W_PUMP_PERCENT, static_cast<uint8_t>(changes.pump_speed_percent));
    }

    // RGB fields
    if (changes.rgb_mode != 0 || changes.rgb_enable) {
        patch_u8(buf, W_RGB_MODE, static_cast<uint8_t>(changes.rgb_mode));
    }
    if (changes.rgb_enable) {
        patch_u8(buf, W_RGB_ENABLE, 1);
    }
    if (changes.rgb_level != 0) {
        patch_u8(buf, W_RGB_LEVEL, static_cast<uint8_t>(changes.rgb_level));
    }
    if (changes.rgb_r != 0) {
        patch_u8(buf, W_RGB_R, static_cast<uint8_t>(changes.rgb_r));
    }
    if (changes.rgb_g != 0) {
        patch_u8(buf, W_RGB_G, static_cast<uint8_t>(changes.rgb_g));
    }
    if (changes.rgb_b != 0) {
        patch_u8(buf, W_RGB_B, static_cast<uint8_t>(changes.rgb_b));
    }

    // Curve fields: only written when mode == 254.
    // Determine effective mode: changes.mode if set, else current mode from read buffer.
    int effective_mode = changes.mode != 0 ? changes.mode
        : (static_cast<int>(current.size()) > R_MODE ? static_cast<int>(current[R_MODE]) : 0);
    if (effective_mode == MODE_INTELLIFAN && !changes.curve.empty()) {
        for (size_t i = 0; i < changes.curve.size() && i < 9; ++i) {
            int t_off = W_CURVE_T1 + static_cast<int>(i) * 2;
            int f_off = t_off + 1;
            if (f_off < WRITE_BUF_LEN) {
                buf[t_off] = static_cast<uint8_t>(changes.curve[i].temp_c);
                buf[f_off] = static_cast<uint8_t>(changes.curve[i].speed_pct);
            }
        }
    }

    return buf;
}

// ===== Protocol: fragment =====
// Mirrors the validated Python reference implementation. Splits 57-byte logical buffer into 3 fragments
// of 20 bytes each (1-byte sequence header + 19 bytes payload).
// header(n) = 0x0c | (n << 4) → n=1:0x1c n=2:0x2c n=3:0x3c

auto FrostBayController::fragment(const std::vector<uint8_t>& logical)
    -> std::vector<std::vector<uint8_t>>
{
    std::vector<std::vector<uint8_t>> fragments;

    int remaining = static_cast<int>(logical.size());
    int offset = 0;
    int frag_num = 1;

    while (remaining > 0 && frag_num <= 3) {
        int payload_len = std::min(remaining, FRAG_PAYLOAD_LEN);
        std::vector<uint8_t> frag;
        frag.reserve(1 + static_cast<size_t>(payload_len));

        // Sequence header
        uint8_t header = static_cast<uint8_t>(0x0c | (frag_num << 4));
        frag.push_back(header);

        // Payload
        frag.insert(frag.end(), logical.begin() + offset, logical.begin() + offset + payload_len);

        fragments.push_back(std::move(frag));
        offset += payload_len;
        remaining -= payload_len;
        frag_num++;
    }

    return fragments;
}

// ===== GATT primitives =====

// Send each fragment as a write-without-response (fallback: write-with-response)
// on 0xFFE1. Caller must hold gatt_mutex_.
auto FrostBayController::write_fragments_locked(const std::vector<uint8_t>& logical) -> bool {
    std::optional<SimpleBLE::Peripheral> peri;
    std::string svc;
    bool use_cmd = true;
    {
        std::lock_guard lock(ble_mutex_);
        if (!ble_ || !ble_->peripheral) {
            log_warn("[frostbay] write_fragments: not connected");
            return false;
        }
        peri = *ble_->peripheral;
        svc = ble_->service_uuid;
        use_cmd = ble_->use_write_command;
    }

    auto frags = fragment(logical);
    for (size_t i = 0; i < frags.size(); ++i) {
        SimpleBLE::ByteArray data(frags[i].begin(), frags[i].end());
        try {
            if (use_cmd) {
                peri->write_command(svc, kCharUuid, data);
            } else {
                peri->write_request(svc, kCharUuid, data);
            }
        } catch (const std::exception& e) {
            log_error("[frostbay] write failed for fragment "
                      + std::to_string(i + 1) + "/" + std::to_string(frags.size())
                      + ": " + e.what());
            return false;
        }
    }

    return true;
}

// GATT read → parse → update cached state. Caller must hold gatt_mutex_.
auto FrostBayController::read_status_locked() -> bool {
    if (!connected_.load()) return false;

    std::optional<SimpleBLE::Peripheral> peri;
    std::string svc;
    {
        std::lock_guard lock(ble_mutex_);
        if (!ble_ || !ble_->peripheral) return false;
        peri = *ble_->peripheral;
        svc = ble_->service_uuid;
    }

    try {
        SimpleBLE::ByteArray data = peri->read(svc, kCharUuid);

        if (static_cast<int>(data.size()) < READ_MIN_LEN) {
            log_warn("[frostbay] short status read ("
                     + std::to_string(data.size()) + " < " + std::to_string(READ_MIN_LEN) + ")");
            return false;
        }

        std::lock_guard lock(mutex_);
        last_read_buffer_ = data;
        state_ = parse_status(data);
        last_notification_ = std::chrono::steady_clock::now();
        return true;
    } catch (const std::exception& e) {
        log_error("[frostbay] status read failed: " + std::string(e.what()));
        return false;
    }
}

auto FrostBayController::read_device_status() -> bool {
    std::lock_guard gatt(gatt_mutex_);
    return read_status_locked();
}

// ===== Control =====
// All setters are thin wrappers around write_settings(): one atomic
// read-modify-write sequence under gatt_mutex_.

auto FrostBayController::set_mode(int mode) -> Result<void> {
    FrostBayState changes;
    changes.mode = mode;
    return write_settings(changes);
}

auto FrostBayController::set_fan_percent(int pct) -> Result<void> {
    FrostBayState changes;
    changes.fan_speed_percent = pct;
    return write_settings(changes);
}

auto FrostBayController::set_pump_percent(int pct) -> Result<void> {
    FrostBayState changes;
    changes.pump_speed_percent = pct;
    return write_settings(changes);
}

// Exact RGB write: unlike write_settings() (where 0 means "leave untouched"),
// all six fields are always written — including zero components and
// enable=false. Mirrors the Python reference, where an explicitly provided
// value of 0 is written as 0 (put() only skips None).
auto FrostBayController::set_rgb(bool enable, int mode, int level, int r, int g, int b) -> Result<void> {
    if (!connected_.load()) {
        log_warn("[frostbay] set_rgb: not connected");
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    std::lock_guard gatt(gatt_mutex_);

    if (!read_status_locked()) {
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    std::vector<uint8_t> current;
    {
        std::lock_guard lock(mutex_);
        current = last_read_buffer_;
    }

    // Seed from current device state (preserves mode/fan/pump/curve), then
    // force every RGB byte.
    FrostBayState seed;
    auto buf = build_write_buffer(current, seed);
    patch_u8(buf, W_RGB_MODE, static_cast<uint8_t>(mode));
    patch_u8(buf, W_RGB_ENABLE, enable ? 1 : 0);
    patch_u8(buf, W_RGB_LEVEL, static_cast<uint8_t>(level));
    patch_u8(buf, W_RGB_R, static_cast<uint8_t>(r));
    patch_u8(buf, W_RGB_G, static_cast<uint8_t>(g));
    patch_u8(buf, W_RGB_B, static_cast<uint8_t>(b));

    if (!write_fragments_locked(buf)) {
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    {
        std::lock_guard lock(mutex_);
        state_.rgb_enable = enable;
        state_.rgb_mode = mode;
        state_.rgb_level = level;
        state_.rgb_r = r;
        state_.rgb_g = g;
        state_.rgb_b = b;
    }

    return {};
}

auto FrostBayController::set_curve(const std::vector<FrostBayState::CurvePoint>& curve) -> Result<void> {
    FrostBayState changes;
    changes.curve = curve;
    return write_settings(changes);
}

auto FrostBayController::write_settings(const FrostBayState& changes) -> Result<void> {
    if (!connected_.load()) {
        log_warn("[frostbay] write_settings: not connected");
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    // Serialize the whole read-modify-write against tick() reads and other
    // setters — the device reassembles fragments, so concurrent GATT traffic
    // must not interleave with a write burst.
    std::lock_guard gatt(gatt_mutex_);

    if (!read_status_locked()) {
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    std::vector<uint8_t> current;
    {
        std::lock_guard lock(mutex_);
        current = last_read_buffer_;
    }

    auto write_buf = build_write_buffer(current, changes);
    if (!write_fragments_locked(write_buf)) {
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    // Update cached state with changed fields
    {
        std::lock_guard lock(mutex_);
        if (changes.mode != 0) state_.mode = changes.mode;
        if (changes.fan_speed_percent != 0) state_.fan_speed_percent = changes.fan_speed_percent;
        if (changes.pump_speed_percent != 0) state_.pump_speed_percent = changes.pump_speed_percent;
        if (changes.rgb_mode != 0 || changes.rgb_enable) {
            state_.rgb_enable = changes.rgb_enable;
            state_.rgb_mode = changes.rgb_mode;
        }
        if (changes.rgb_level != 0) state_.rgb_level = changes.rgb_level;
        if (changes.rgb_r != 0) state_.rgb_r = changes.rgb_r;
        if (changes.rgb_g != 0) state_.rgb_g = changes.rgb_g;
        if (changes.rgb_b != 0) state_.rgb_b = changes.rgb_b;
        if (!changes.curve.empty()) state_.curve = changes.curve;
    }

    return {};
}

// ===== Tick =====

void FrostBayController::tick() {
    if (!connected_.load()) return;

    // With an active notify subscription the state is kept fresh by the
    // callback; only read as a fallback if nothing arrived for ~3s (tick is
    // 2s). In polling mode (the norm — 0xFFE1 has no notify) read every tick.
    if (notify_active_.load()) {
        std::lock_guard lock(mutex_);
        auto elapsed_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - last_notification_).count();
        if (elapsed_ms <= 3000) return;
    }

    read_device_status();
}

// ===== BLE: connect_to_device =====
// Paired-device connect first (silent — no radio discovery); the scan
// fallback only runs once ensure_started() armed it (first cooling command).

auto FrostBayController::connect_to_device() -> bool {
    // Enumerate adapters on every attempt: cheap, and survives bluetoothd
    // restarts and adapter replug.
    std::vector<SimpleBLE::Adapter> adapters;
    try {
        adapters = SimpleBLE::Adapter::get_adapters();
    } catch (const std::exception& e) {
        if (!no_adapter_warned_) {
            log_warn("[frostbay] BlueZ adapter enumeration failed: " + std::string(e.what()));
            no_adapter_warned_ = true;
        }
        return false;
    }
    if (adapters.empty()) {
        if (!no_adapter_warned_) {
            log_warn("[frostbay] No BLE adapters found");
            no_adapter_warned_ = true;
        }
        return false;
    }
    no_adapter_warned_ = false;

    SimpleBLE::Adapter adapter = adapters[0];

    // 1) Paired path (steady state). BlueZ retains the Frost Bay device
    //    object across boots, so connect directly — zero discovery traffic.
    std::optional<SimpleBLE::Peripheral> target;
    try {
        for (auto& p : adapter.get_paired_peripherals()) {
            if (p.identifier() == kDeviceName) {
                target = p;
                break;
            }
        }
    } catch (const std::exception& e) {
        log_warn("[frostbay] paired-device enumeration failed: " + std::string(e.what()));
    }

    // 2) Scan fallback — first-ever use (device not yet paired). Bounded
    //    window, early exit on find, interruptible for fast shutdown.
    if (!target && scan_enabled_.load()) {
        try {
            adapter.scan_start();
            auto deadline = std::chrono::steady_clock::now() + 5s;
            while (running_.load() && !target
                   && std::chrono::steady_clock::now() < deadline) {
                for (auto& p : adapter.scan_get_results()) {
                    if (p.identifier() == kDeviceName) {
                        target = p;
                        break;
                    }
                }
                if (!target) interruptible_sleep(500ms);
            }
            adapter.scan_stop();
            if (target) {
                log_info("[frostbay] Scan found Frost Bay: " + target->identifier());
            }
        } catch (const std::exception& e) {
            log_error("[frostbay] scan failed: " + std::string(e.what()));
            try { adapter.scan_stop(); } catch (...) {}
        }
    }

    if (!target) {
        // Absent/powered-off is normal operation, not an error.
        return false;
    }

    // ---- Connect + GATT setup. On ANY failure the link is torn down again:
    // ---- a dangling BlueZ-level connection keeps the cooler streaming its
    // ---- phantom HID reports (the udev rule silences them, but only once
    // ---- the input device exists — see packaging/linux/60-xmax-frostbay.rules),
    // ---- so we must never leak one.
    bool link_up = false;
    try {
        target->connect();
        link_up = target->is_connected();
    } catch (const std::exception& e) {
        // Throttled: a powered-off-but-paired cooler fails here every cycle.
        if (consecutive_failures_++ == 0) {
            log_info("[frostbay] Connect failed: " + std::string(e.what()));
        } else {
            log_debug("[frostbay] Connect failed: " + std::string(e.what()));
        }
        return false;
    }
    if (!link_up) {
        consecutive_failures_++;
        return false;
    }
    consecutive_failures_ = 0;

    try {
        log_info("[frostbay] Connected to " + target->identifier());

        // Locate characteristic 0xFFE1 under whichever service exposes it.
        std::string svc_uuid;
        bool found_char = false;
        bool can_notify = false;
        bool can_write_cmd = true;
        for (auto& svc : target->services()) {
            for (auto& chr : svc.characteristics()) {
                std::string uuid = chr.uuid();
                if (uuid.find("ffe1") == std::string::npos) continue;
                svc_uuid = svc.uuid();
                can_notify = chr.can_notify();
                can_write_cmd = chr.can_write_command();
                found_char = true;
                break;
            }
            if (found_char) break;
        }

        if (!found_char) {
            log_error("[frostbay] Characteristic 0xFFE1 not found, disconnecting");
            try { target->disconnect(); } catch (...) {}
            return false;
        }

        // Publish handles before flipping connected_ (readers gate on it).
        {
            std::lock_guard lock(ble_mutex_);
            if (!ble_) ble_ = std::make_unique<Ble>();
            ble_->adapter = adapter;
            ble_->peripheral = *target;
            ble_->service_uuid = svc_uuid;
            ble_->use_write_command = can_write_cmd;
        }

        // Notify is OPTIONAL. Current Frost Bay firmware does not expose
        // notify/indicate on 0xFFE1 (BlueZ rejects the CCCD write), so the
        // normal mode is polling: tick() GATT-reads every poll interval,
        // exactly like the validated Python reference implementation.
        notify_active_.store(false);
        if (can_notify) {
            try {
                target->notify(svc_uuid, kCharUuid,
                    [this](const SimpleBLE::ByteArray& data) {
                        if (static_cast<int>(data.size()) < READ_MIN_LEN) return;
                        std::lock_guard lock(mutex_);
                        last_read_buffer_ = data;
                        state_ = parse_status(data);
                        last_notification_ = std::chrono::steady_clock::now();
                    });
                notify_active_.store(true);
                log_info("[frostbay] Subscribed to 0xFFE1 notifications");
            } catch (const std::exception& e) {
                log_info("[frostbay] notify subscribe failed (" + std::string(e.what())
                         + ") — using polling");
            }
        } else {
            log_info("[frostbay] 0xFFE1 has no notify support — using polling");
        }

        connected_.store(true);
        read_device_status();  // prime cached state (best effort)
        log_info("[frostbay] Frost Bay ready");
        return true;
    } catch (const std::exception& e) {
        log_error("[frostbay] Setup after connect failed: " + std::string(e.what()));
        try { target->disconnect(); } catch (...) {}
        return false;
    } catch (...) {
        log_error("[frostbay] Setup after connect failed: unknown exception");
        try { target->disconnect(); } catch (...) {}
        return false;
    }
}

// ===== BLE: disconnect_device =====
// Connection-loop thread ONLY. Waits out in-flight GATT ops (gatt_mutex_),
// then unsubscribes/disconnects and clears the cached state. Idempotent.

void FrostBayController::disconnect_device() {
    connected_.store(false);
    bool had_notify = notify_active_.exchange(false);

    std::lock_guard gatt(gatt_mutex_);

    std::optional<SimpleBLE::Adapter> adapter;
    std::optional<SimpleBLE::Peripheral> peri;
    std::string svc;
    {
        std::lock_guard lock(ble_mutex_);
        if (ble_) {
            adapter = std::move(ble_->adapter);
            if (ble_->peripheral) {
                peri = *ble_->peripheral;
                svc = ble_->service_uuid;
                ble_->peripheral.reset();
            }
            ble_->service_uuid.clear();
        }
    }

    if (peri) {
        if (had_notify && !svc.empty()) {
            try { peri->unsubscribe(svc, kCharUuid); } catch (...) {}
        }
        try {
            if (peri->is_connected()) {
                peri->disconnect();
            }
        } catch (...) {}
    }
    // peri is destroyed before adapter (declaration order).

    {
        std::lock_guard lock(mutex_);
        state_ = FrostBayState{};
        last_read_buffer_.clear();
    }
}

#ifdef __linux__
// ===== Phantom-HID neutralization =====
// The cooler's HID-over-GATT service makes bluetoothd create a uhid input
// device that emits its telemetry as volume-down key events (~1/s). The udev
// rule hides it from libinput; the exclusive grab below ALSO starves raw-evdev
// consumers (keyd & co), which ignore udev properties. All functions here run
// exclusively on the watcher thread (phantom_thread_), independently of the
// BLE connection flow — the node is caught within one scan interval (25ms)
// of appearing instead of after GATT setup completes (~2s later).

void FrostBayController::phantom_hid_watch() {
    log_debug("[frostbay] Phantom-HID watcher started");

    while (running_.load()) {
        auto dev = find_input_event_node(kDeviceName);

        if (phantom_hid_fd_ >= 0) {
            if (!dev) {
                release_phantom_hid();  // node gone (disconnect / HOG detach)
            } else {
                drain_phantom_hid();    // also detects a vanished node (ENODEV)
            }
        } else if (dev) {
            if (!phantom_hid_done_ && !grab_phantom_hid(*dev)) {
                phantom_hid_done_ = true;  // give up until the node cycles (warning already logged)
            }
        } else {
            phantom_hid_done_ = false;     // node absent: re-arm for its next appearance
        }

        // 25ms cadence: bluetoothd's HOG plugin creates the node ~1s after
        // the link comes up and raw-evdev consumers react within milliseconds
        // of that — every scan interval of exposure can leak a volume tick.
        // The sysfs scan is a few dozen tiny reads; negligible at this rate.
        interruptible_sleep(25ms);
    }

    release_phantom_hid();
    log_debug("[frostbay] Phantom-HID watcher stopped");
}

auto FrostBayController::grab_phantom_hid(const std::string& dev) -> bool {
    int fd = ::open(dev.c_str(), O_RDONLY | O_NONBLOCK);
    if (fd < 0) {
        log_warn("[frostbay] cannot open phantom HID node " + dev + ": " + std::strerror(errno));
        return false;
    }
    int one = 1;
    if (::ioctl(fd, EVIOCGRAB, &one) == 0) {
        phantom_hid_fd_ = fd;
        phantom_hid_path_ = dev;
        log_info("[frostbay] Phantom HID device neutralized — exclusive grab on " + dev
                 + " (its volume-key telemetry goes nowhere)");
        return true;
    }
    int err = errno;
    ::close(fd);
    if (err == EBUSY) {
        // Another raw-evdev daemon (keyd & co) grabbed it first — usually
        // within milliseconds of the node appearing. Escalate: unbind the
        // HID driver; the input device is unregistered underneath whoever
        // holds it. Side effect: bluetoothd's report writes start failing,
        // which may add bt_uhid_input lines to the journal while connected.
        if (unbind_phantom_hid()) return true;
    }
    log_warn("[frostbay] EVIOCGRAB on " + dev + " failed: " + std::strerror(err));
    return false;
}

auto FrostBayController::unbind_phantom_hid() -> bool {
    namespace fs = std::filesystem;
    auto input_dir = find_input_sysfs_dir(kDeviceName, "/sys");
    if (!input_dir) return false;

    std::error_code ec;
    // /sys/class/input/inputN resolves to .../uhid/<hid_id>/input/inputN
    fs::path real = fs::canonical(*input_dir, ec);
    if (ec) return false;
    fs::path hid_dir = real.parent_path().parent_path();
    std::string hid_id = hid_dir.filename().string();
    if (hid_id.empty() || hid_id.find(':') == std::string::npos) return false;

    // Unbind via the actual bound driver (normally hid-generic).
    std::string driver = "hid-generic";
    fs::path drv_link = fs::read_symlink(hid_dir / "driver", ec);
    if (!ec && !drv_link.filename().empty()) driver = drv_link.filename().string();

    fs::path unbind_path = fs::path("/sys/bus/hid/drivers") / driver / "unbind";
    int fd = ::open(unbind_path.c_str(), O_WRONLY);
    if (fd < 0) {
        log_warn("[frostbay] cannot open " + unbind_path.string() + ": " + std::strerror(errno));
        return false;
    }
    ssize_t n = ::write(fd, hid_id.data(), hid_id.size());
    ::close(fd);
    if (n != static_cast<ssize_t>(hid_id.size())) {
        log_warn("[frostbay] phantom HID unbind write failed: " + std::string(std::strerror(errno)));
        return false;
    }
    phantom_hid_unbind_ = true;
    log_info("[frostbay] Phantom HID neutralized via driver unbind (" + hid_id
             + " → " + driver + ") — device was grabbed by another daemon");
    return true;
}

void FrostBayController::drain_phantom_hid() {
    if (phantom_hid_fd_ < 0) return;

    struct input_event ev {};
    uint64_t batch = 0;
    for (;;) {
        ssize_t n = ::read(phantom_hid_fd_, &ev, sizeof(ev));
        if (n == static_cast<ssize_t>(sizeof(ev))) {
            ++batch;
            continue;
        }
        if (n < 0 && errno != EAGAIN && errno != EWOULDBLOCK) {
            // Node vanished underneath us (link drop / HOG detach). Release;
            // the watch loop re-neutralizes if it reappears.
            log_info("[frostbay] Phantom HID node gone — releasing grab");
            release_phantom_hid();
            return;
        }
        break;  // queue empty
    }

    phantom_hid_events_ += batch;
    if (batch > 0 && !phantom_hid_logged_) {
        phantom_hid_logged_ = true;
        log_info("[frostbay] Discarding phantom HID reports (" + std::to_string(batch)
                 + " input events in first batch — would be volume-down keys)");
    }
}

void FrostBayController::release_phantom_hid() {
    if (phantom_hid_fd_ >= 0) {
        int zero = 0;
        ::ioctl(phantom_hid_fd_, EVIOCGRAB, &zero);  // ungrab (close() alone would also release)
        ::close(phantom_hid_fd_);
        log_debug("[frostbay] Released phantom HID grab (" + phantom_hid_path_ + ", "
                  + std::to_string(phantom_hid_events_) + " events discarded)");
        phantom_hid_fd_ = -1;
        phantom_hid_path_.clear();
    }
    phantom_hid_done_ = false;
    phantom_hid_unbind_ = false;
    phantom_hid_events_ = 0;
    phantom_hid_logged_ = false;
}
#endif  // __linux__

// ===== BLE: connection_loop =====
// Runs in a dedicated thread and is the SOLE owner of BLE teardown.
// Failure pacing: paired-path attempts are silent local D-Bus calls (cap 30s);
// once the scan fallback is armed, back off further (cap 300s) to limit
// radio discovery churn.

void FrostBayController::connection_loop() {
    log_info("[frostbay] Connection loop started");

    // Short boot delay: let the rest of the daemon come up before we touch
    // BlueZ. Interruptible, so stop() is never held up by it.
    interruptible_sleep(3s);

    int backoff_s = 10;

    while (running_.load()) {
        if (!connected_.load()) {
            bool ok = false;
            try {
                ok = connect_to_device();
            } catch (const std::exception& e) {
                log_error("[frostbay] connect_to_device exception: " + std::string(e.what()));
            } catch (...) {
                log_error("[frostbay] connect_to_device: unknown exception");
            }

            if (!ok) {
                int cap = scan_enabled_.load() ? 300 : 30;
                if (backoff_s > cap) backoff_s = cap;
                interruptible_sleep(std::chrono::seconds(backoff_s));
                backoff_s = std::min(backoff_s * 2, 300);
                continue;
            }
            backoff_s = 10;
        }

        // Connected — watch the link until disconnect or shutdown.
        while (running_.load() && connected_.load()) {
            bool link_up = false;
            {
                std::lock_guard lock(ble_mutex_);
                if (ble_ && ble_->peripheral) {
                    try {
                        link_up = ble_->peripheral->is_connected();
                    } catch (...) {
                        link_up = false;
                    }
                }
            }
            if (!link_up) {
                log_info("[frostbay] Peripheral disconnected");
                connected_.store(false);
                break;
            }
            interruptible_sleep(1s);
        }

        disconnect_device();
    }

    disconnect_device();
    log_info("[frostbay] Connection loop stopped");
}
