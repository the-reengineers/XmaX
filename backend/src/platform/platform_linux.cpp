#include "platform.h"
#include "../logger.h"
#include "linux/ec.h"
#include "linux/procfs.h"
#include "linux/ryzenadj.h"

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cerrno>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include <fcntl.h>
#include <sys/file.h>
#include <unistd.h>

#include <poll.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>

namespace fs = std::filesystem;

namespace {

// Opaque per-handle state (Linux equivalents of the Windows handles).
// Transport handles/servers are filled in during Step 1.
struct LinuxChildProcess {
    uint64_t pid = 0;
};

struct LinuxTrayHandle {
    bool active = false;
};

struct LinuxInstanceLock {
    int fd = -1;
};

// Path for the single-instance lock file. Prefer the per-user runtime dir
// (private, tmpfs-style); fall back to /tmp with the uid disambiguator.
fs::path lock_path() {
    if (const char* rt = std::getenv("XDG_RUNTIME_DIR"); rt && *rt) {
        return fs::path(rt) / "xmax.lock";
    }
    return fs::path("/tmp") / ("xmax-" + std::to_string(getuid()) + ".lock");
}

// Opaque transport server state (AF_UNIX socket). The handle carries BOTH the
// listening socket and the accepted client socket because the pipe_* methods
// operate on the server handle (not a PeerId), mirroring the Win32 named-pipe.
// fd values are atomic so the connection thread (accept/read/write) and the
// stop thread (close_server) can signal/teardown without a data race.
struct LinuxTransportServer {
    std::atomic<int> listen_fd{-1};
    std::atomic<int> client_fd{-1};
};

// Path for the client control socket. Prefer $XDG_RUNTIME_DIR (per-user private
// tmpfs); fall back to /tmp with the uid disambiguator, matching lock_path().
fs::path socket_path() {
    if (const char* rt = std::getenv("XDG_RUNTIME_DIR"); rt && *rt) {
        return fs::path(rt) / "xmax.sock";
    }
    return fs::path("/tmp") / ("xmax-" + std::to_string(getuid()) + ".sock");
}

// Close a socket fd exactly once. The exchange-to--1 idiom guarantees a single
// closer even when close_server races with a blocked accept/read on another
// thread (the loser reads -1 and skips), preventing a double-close.
void close_socket_fd(std::atomic<int>& fd) {
    int owned = fd.exchange(-1);
    if (owned >= 0) {
        ::shutdown(owned, SHUT_RDWR);
        ::close(owned);
    }
}

// Read a peer process's real uid from /proc/<pid>/status ("Uid:" line).
// Returns (uid_t)-1 if the process is gone or the line is malformed.
uid_t read_proc_uid(uint64_t pid) {
    std::ifstream status("/proc/" + std::to_string(pid) + "/status");
    std::string line;
    while (std::getline(status, line)) {
        if (line.rfind("Uid:", 0) == 0) {
            std::istringstream iss(line);
            std::string label;
            uid_t real_uid = static_cast<uid_t>(-1);
            iss >> label >> real_uid;
            return real_uid;
        }
    }
    return static_cast<uid_t>(-1);
}

// Battery charge-limit EC register (mirrors PowerController::EC_CHARGE_LIMIT).
constexpr uint16_t kEcChargeLimit = 0x04A3;

// systemd unit that backs auto-start on Linux.
constexpr const char* kSystemdUnit = "xmaxd";

// Run /usr/bin/systemctl with fixed, non-user-controlled arguments. Returns
// true iff the command exits with status 0. spawn failure -> false.
bool run_systemctl(const std::vector<std::string>& args) {
    std::string cmd = "/usr/bin/systemctl";
    for (const auto& a : args) {
        cmd += ' ';
        cmd += a;
    }
    return std::system(cmd.c_str()) == 0;
}

// Run /usr/bin/systemctl and capture the trimmed first line of stdout.
// Returns nullopt if the spawn failed or produced no output.
std::optional<std::string> systemctl_output(const std::vector<std::string>& args) {
    std::string cmd = "/usr/bin/systemctl";
    for (const auto& a : args) {
        cmd += ' ';
        cmd += a;
    }
    FILE* pipe = popen(cmd.c_str(), "r");
    if (!pipe) {
        return std::nullopt;
    }
    std::string out;
    char buf[128];
    while (fgets(buf, sizeof(buf), pipe)) {
        out += buf;
    }
    int rc = pclose(pipe);
    if (rc == -1 || out.empty()) {
        return std::nullopt;
    }
    while (!out.empty() && (out.back() == '\n' || out.back() == '\r' || out.back() == ' ')) {
        out.pop_back();
    }
    return out;
}

// Strix Halo MP1 SMU opcodes for TDP limits (same value for read and write;
// mirrors TdpController's private constants). Argument/result is milliwatts.
constexpr uint32_t kSmuStapm = 0x14;  // STAPM
constexpr uint32_t kSmuFast  = 0x15;  // Fast
constexpr uint32_t kSmuSlow  = 0x16;  // Slow

}  // namespace

class LinuxPlatform : public Platform {
public:
    LinuxPlatform() = default;
    ~LinuxPlatform() override = default;

    bool init_hardware() override {
        // Probe optional hardware backends. Both are non-fatal: when absent,
        // per-call methods degrade to SensorUnavailable.
        xmax_ec::init();
        bool ryzenadj_ok = ryzenadj::init();
        if (!xmax_ec::available()) {
            log_warn("[hw] EC backend unavailable (ec_sys/Super-I/O) -- controls need root");
        }
        if (!ryzenadj_ok) {
            log_warn("[hw] libryzenadj unavailable -- TDP control disabled");
        }
        return true;
    }

    // ===== Transport =====
    // AF_UNIX socket at $XDG_RUNTIME_DIR/xmax.sock (mode 0600), served with the
    // existing JSON line protocol. Reuses TransportService unchanged.

    auto listen() -> Result<TransportServer> override {
        const fs::path path = socket_path();
        if (path.native().size() >= sizeof(sockaddr_un::sun_path)) {
            log_error("[transport] socket path too long: " + path.string());
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        int fd = ::socket(AF_UNIX, SOCK_STREAM, 0);
        if (fd < 0) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        sockaddr_un addr{};
        addr.sun_family = AF_UNIX;
        std::strncpy(addr.sun_path, path.c_str(), sizeof(addr.sun_path) - 1);

        // Remove any stale socket left by a crashed run. Safe: the single-
        // instance flock guarantees no other live instance owns this path.
        ::unlink(path.c_str());

        if (::bind(fd, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) != 0) {
            ::close(fd);
            return std::unexpected(ErrorCode::HardwareBusy);
        }
        // Mode 0600 so only this user can connect (verify_peer checks uid too).
        ::chmod(path.c_str(), 0600);

        if (::listen(fd, 16) != 0) {
            ::close(fd);
            ::unlink(path.c_str());
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        auto state = std::make_unique<LinuxTransportServer>();
        state->listen_fd.store(fd);

        TransportServer server;
        server.handle = state.get();
        transport_servers_.push_back(std::move(state));
        return server;
    }

    auto accept_connection(TransportServer& server) -> Result<PeerId> override {
        auto* state = static_cast<LinuxTransportServer*>(server.handle);
        if (!state) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }
        int lfd = state->listen_fd.load();
        if (lfd < 0) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        // 1s timed wait so the caller can re-check its running flag between
        // attempts (mirrors the Win32 overlapped-connect timeout). close_server
        // on another thread shutdown()s this fd, which wakes the poll.
        pollfd pfd{lfd, POLLIN, 0};
        int pr = ::poll(&pfd, 1, 1000);
        if (pr <= 0 || !(pfd.revents & (POLLIN | POLLERR | POLLHUP | POLLNVAL))) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        int cfd = ::accept(lfd, nullptr, nullptr);
        if (cfd < 0) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }
        state->client_fd.store(cfd);

        // Identify the peer via SO_PEERCRED (pid/uid/gid of the connecting process).
        ucred cred{};
        socklen_t cred_len = sizeof(cred);
        if (::getsockopt(cfd, SOL_SOCKET, SO_PEERCRED, &cred, &cred_len) != 0) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        PeerId peer;
        peer.process_id = static_cast<uint64_t>(cred.pid);
        return peer;
    }

    auto verify_peer(PeerId peer) -> Result<PeerInfo> override {
        PeerInfo info;

        // Resolve the peer's executable path via procfs (peer may have exited;
        // an empty path then fails verification).
        char buf[4096];
        const std::string exe_link = "/proc/" + std::to_string(peer.process_id) + "/exe";
        ssize_t n = ::readlink(exe_link.c_str(), buf, sizeof(buf) - 1);
        if (n > 0) {
            info.executable_path = std::string(buf, n);
        }

        // Trust only this user's processes. The socket is mode 0600 in
        // $XDG_RUNTIME_DIR, so a successful connect already implies same-user;
        // confirming the uid gives a positive identification.
        info.verified = (!info.executable_path.empty()
                         && read_proc_uid(peer.process_id) == static_cast<uid_t>(getuid()));

        log_debug("[verify_peer] client: " + info.executable_path
                  + " (verified=" + std::string(info.verified ? "true" : "false") + ")");
        return info;
    }

    auto read_data(PeerId, char*, size_t) -> Result<size_t> override {
        // Unused stub — transport uses pipe_read/pipe_write instead (matches Win32).
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    auto write_data(PeerId, const char*, size_t) -> Result<void> override {
        // Unused stub — transport uses pipe_read/pipe_write instead (matches Win32).
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    void close_connection(PeerId) override {
        // Unused stub — transport uses pipe_disconnect instead (matches Win32).
    }

    void close_server(TransportServer& server) override {
        auto* state = static_cast<LinuxTransportServer*>(server.handle);
        if (state) {
            close_socket_fd(state->client_fd);
            close_socket_fd(state->listen_fd);
            server.handle = nullptr;
        }
    }

    auto pipe_read(TransportServer& server, char* buffer, size_t size) -> Result<size_t> override {
        auto* state = static_cast<LinuxTransportServer*>(server.handle);
        if (!state) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }
        int cfd = state->client_fd.load();
        if (cfd < 0) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }

        ssize_t n;
        do {
            n = ::read(cfd, buffer, size);
        } while (n < 0 && errno == EINTR);

        if (n > 0) {
            return static_cast<size_t>(n);
        }
        // n == 0 (EOF) or a hard error — treat as client disconnect.
        return std::unexpected(ErrorCode::SensorUnavailable);
    }

    auto pipe_write(TransportServer& server, const char* data, size_t size) -> Result<void> override {
        auto* state = static_cast<LinuxTransportServer*>(server.handle);
        if (!state) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }
        int cfd = state->client_fd.load();
        if (cfd < 0) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        size_t written = 0;
        while (written < size) {
            // MSG_NOSIGNAL so a closed peer yields EPIPE instead of SIGPIPE.
            ssize_t n = ::send(cfd, data + written, size - written, MSG_NOSIGNAL);
            if (n > 0) {
                written += static_cast<size_t>(n);
                continue;
            }
            if (n < 0 && errno == EINTR) {
                continue;
            }
            return std::unexpected(ErrorCode::HardwareBusy);
        }
        return {};
    }

    void pipe_flush(TransportServer&) override {
        // No-op: AF_UNIX stream socket writes are delivered immediately.
    }

    void pipe_disconnect(TransportServer& server) override {
        auto* state = static_cast<LinuxTransportServer*>(server.handle);
        if (state) {
            close_socket_fd(state->client_fd);
        }
    }

    // ===== Hardware: EC / SMU / charge limit =====

    auto ec_read(uint16_t reg) -> Result<uint8_t> override {
        auto value = xmax_ec::read(reg);
        if (!value) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }
        return *value;
    }

    auto ec_write(uint16_t reg, uint8_t val) -> Result<void> override {
        if (!xmax_ec::write(reg, val)) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }
        return {};
    }

    auto smu_send(uint32_t msg, uint32_t arg) -> Result<uint32_t> override {
        // Route SMU mailbox calls to ryzenadj. arg is already in milliwatts
        // (TdpController converts W -> mW before calling).
        bool ok = false;
        switch (msg) {
            case kSmuStapm: ok = ryzenadj::set_stapm_limit(arg); break;
            case kSmuFast:  ok = ryzenadj::set_fast_limit(arg);  break;
            case kSmuSlow:  ok = ryzenadj::set_slow_limit(arg);  break;
            default:
                return std::unexpected(ErrorCode::SensorUnavailable);
        }
        if (!ok) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }
        return arg;  // echo the set value (Win32 returns the mailbox response)
    }

    auto smu_read_safe(uint32_t cmd_id) -> Result<uint32_t> override {
        // Read TDP limits via ryzenadj (returns milliwatts; TdpController
        // converts back to W). There is no safe mailbox read on Strix Halo, so
        // this maps to the library getters rather than a mailbox send.
        std::optional<uint32_t> mw;
        switch (cmd_id) {
            case kSmuStapm: mw = ryzenadj::get_stapm_limit(); break;
            case kSmuFast:  mw = ryzenadj::get_fast_limit();  break;
            case kSmuSlow:  mw = ryzenadj::get_slow_limit();  break;
            default:
                return std::unexpected(ErrorCode::SensorUnavailable);
        }
        if (!mw) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }
        return *mw;
    }

    auto charge_limit_write(uint8_t percent) -> Result<void> override {
        // Battery charge limit lives in EC register 0x04A3 on this platform.
        // Failure surfaces as an error here; the transport layer maps it to
        // ChargeLimitWriteFail for the client.
        return ec_write(kEcChargeLimit, percent);
    }

    auto cpu_clock_mhz() -> uint32_t override {
        // scaling_cur_freq is the running (live) clock; cpuinfo_cur_freq is a
        // fallback on kernels/drivers that don't export the former. Values are
        // in kHz; the parser converts to MHz.
        if (auto mhz = procfs::cpu_clock_mhz("/sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq")) {
            return *mhz;
        }
        if (auto mhz = procfs::cpu_clock_mhz("/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_cur_freq")) {
            return *mhz;
        }
        return 0;  // 0 = unavailable
    }

    auto gpu_metrics() -> Result<GpuTelemetry> override {
        auto info = procfs::gpu_info();
        if (!info.found) {
            // No amdgpu hwmon chip -> treat as "no AMD GPU" (matches the
            // Windows ADLX-unavailable path); the poller logs a warning.
            return std::unexpected(ErrorCode::SensorUnavailable);
        }
        GpuTelemetry t;
        t.util_pct = info.util_pct.value_or(0.0);
        t.clock_mhz = info.clock_mhz.value_or(0);
        t.temp_c = info.temp_c;
        t.power_w = info.power_w;
        t.vram_used_bytes = info.vram_used_bytes;
        t.vram_total_bytes = info.vram_total_bytes;
        return t;
    }

    auto system_metrics() -> Result<SystemTelemetry> override {
        // SmartShift / PowerDistribution have no Linux driver equivalent;
        // report a successful empty result so the poller leaves those fields
        // nullopt rather than faking values.
        return SystemTelemetry{};
    }

    // ===== UMA (Variable Graphics Memory) =====
    // BIOS-level, not runtime-settable on Linux — unsupported by design.

    auto uma_supported() -> Result<bool> override {
        return false;
    }

    auto uma_available_options() -> Result<std::vector<UmaOption>> override {
        return std::vector<UmaOption>{};
    }

    auto uma_current_option() -> Result<UmaOption> override {
        return std::unexpected(ErrorCode::SensorUnavailable);
    }

    auto uma_set_option(const std::string&) -> Result<void> override {
        return std::unexpected(ErrorCode::SensorUnavailable);
    }

    // ===== Process management =====
    // No windowed frontend on Linux — the UI is the GJS Shell extension.

    auto spawn_frontend(const fs::path&, bool) -> Result<ChildProcess> override {
        return std::unexpected(ErrorCode::SensorUnavailable);
    }

    auto show_window(ChildProcess&, bool) -> Result<void> override {
        return std::unexpected(ErrorCode::SensorUnavailable);
    }

    auto wait_for_process(ChildProcess&) -> Result<int> override {
        return std::unexpected(ErrorCode::SensorUnavailable);
    }

    void terminate_process(ChildProcess&) override {}

    // ===== Tray (top-bar icon belongs to the GJS extension) =====
    // Report success with an inert handle so TrayManager stays a no-op.

    auto tray_icon(TrayConfig) -> Result<TrayHandle> override {
        auto* state = new LinuxTrayHandle{};
        TrayHandle handle;
        handle.handle = state;
        return handle;
    }

    auto update_tray_tooltip(TrayHandle&, const std::string&) -> Result<void> override {
        return {};
    }

    void remove_tray_icon(TrayHandle& handle) override {
        delete static_cast<LinuxTrayHandle*>(handle.handle);
        handle.handle = nullptr;
    }

    // ===== System =====

    auto data_dir() -> fs::path override {
        // Config + profiles are user configuration, so $XDG_CONFIG_HOME.
        if (const char* xdg_config = std::getenv("XDG_CONFIG_HOME"); xdg_config && *xdg_config) {
            return fs::path(xdg_config) / "xmax";
        }
        if (const char* home = std::getenv("HOME"); home && *home) {
            return fs::path(home) / ".config" / "xmax";
        }
        return fs::path("/tmp/xmax");
    }

    auto self_exe_path() -> fs::path override {
        static const fs::path cached = [] {
            char buf[4096];
            ssize_t n = ::readlink("/proc/self/exe", buf, sizeof(buf) - 1);
            if (n > 0) {
                buf[n] = '\0';
                return fs::path(buf);
            }
            return fs::path{};
        }();
        return cached;
    }

    auto single_instance_lock() -> Result<InstanceLock> override {
        const fs::path path = lock_path();
        int fd = ::open(path.c_str(), O_CREAT | O_RDWR, 0644);
        if (fd < 0) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }
        if (::flock(fd, LOCK_EX | LOCK_NB) != 0) {
            ::close(fd);
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        auto* state = new LinuxInstanceLock{fd};
        InstanceLock lock;
        lock.handle = state;
        return lock;
    }

    void release_instance_lock(InstanceLock& lock) override {
        if (auto* state = static_cast<LinuxInstanceLock*>(lock.handle)) {
            if (state->fd >= 0) {
                ::flock(state->fd, LOCK_UN);
                ::close(state->fd);
            }
            delete state;
            lock.handle = nullptr;
        }
    }

    // ===== Auto-start =====
    // Maps to enabling/disabling the xmaxd systemd unit (start at boot). The
    // polkit gate lives in the D-Bus layer (org.xmax.set-auto-start), which is
    // the only caller; the socket path never reaches here. The exe_path argument
    // is unused on Linux (Windows uses it for the Task Scheduler shortcut).

    auto set_auto_start(bool enabled, const fs::path& /*exe_path*/) -> Result<void> override {
        const std::vector<std::string> args{enabled ? "enable" : "disable", kSystemdUnit};
        if (!run_systemctl(args)) {
            log_error("[auto-start] systemctl " + std::string(enabled ? "enable" : "disable")
                      + " " + kSystemdUnit + " failed");
            return std::unexpected(ErrorCode::SensorUnavailable);
        }
        return {};
    }

    auto is_auto_start_enabled() -> Result<bool> override {
        auto out = systemctl_output({"is-enabled", kSystemdUnit});
        if (!out) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }
        // "enabled" is the only value that means start-at-boot is set up.
        return *out == "enabled";
    }

    // ===== Message loop =====
    // Block until quit_message_loop() is called (main.cpp's signal handler
    // invokes quit_message_loop on SIGINT/SIGTERM).

    void run_message_loop() override {
        std::unique_lock lock(mutex_);
        cv_.wait(lock, [this] { return quit_.load(); });
    }

    void quit_message_loop() override {
        quit_.store(true);
        cv_.notify_all();
    }

private:
    // Owns each LinuxTransportServer for the lifetime of the process. They are
    // never freed during runtime (only here at destruction, after all threads
    // have joined), so close_server/pipe_disconnect on one thread can never
    // race a `delete` on another — the fds themselves are closed exactly once
    // via the atomic exchange in close_socket_fd().
    std::vector<std::unique_ptr<LinuxTransportServer>> transport_servers_;

    std::atomic<bool> quit_{false};
    std::mutex mutex_;
    std::condition_variable cv_;
};

std::unique_ptr<Platform> create_platform() {
    return std::make_unique<LinuxPlatform>();
}