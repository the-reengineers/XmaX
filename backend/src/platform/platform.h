#pragma once

#include <cstdint>
#include <expected>
#include <filesystem>
#include <functional>
#include <optional>
#include <string>
#include <vector>

#include "../shared.h"

// Result type for error handling
template<typename T>
using Result = std::expected<T, ErrorCode>;

// Platform-neutral types for transport and system operations

struct PeerId {
    uint64_t process_id = 0;
};

struct PeerInfo {
    std::string executable_path;
    bool verified = false;
};

struct TransportServer {
    // Opaque handle - implementation-specific
    void* handle = nullptr;
};

struct ChildProcess {
    uint64_t pid = 0;
    void* process_handle = nullptr;  // Platform-specific handle
};

struct TrayConfig {
    std::string icon_path;
    std::string tooltip;
    std::function<void()> on_left_click;
    std::function<void()> on_right_click;
};

struct TrayHandle {
    void* handle = nullptr;
};

struct InstanceLock {
    void* handle = nullptr;
};

// GPU metrics structure (returned by gpu_metrics())
struct GpuTelemetry {
    double util_pct = 0.0;
    uint32_t clock_mhz = 0;
    std::optional<int> temp_c;
    std::optional<double> power_w;
    std::optional<uint32_t> vram_used_mb;
    std::optional<uint32_t> vram_total_mb;
};

// Abstract platform interface
// All hardware and OS operations go through this interface
// Shared code depends only on this interface
class Platform {
public:
    virtual ~Platform() = default;

    // Initialize all hardware connections (call once after construction)
    // Returns true if all hardware initialized successfully, false if any failed
    // Failures are non-fatal -- hardware methods will return errors at call time
    virtual bool init_hardware() = 0;

    // ===== Transport =====

    // Create and start listening for incoming connections
    virtual auto listen() -> Result<TransportServer> = 0;

    // Verify the identity of a connected peer
    virtual auto verify_peer(PeerId peer) -> Result<PeerInfo> = 0;

    // Accept a new connection (blocking)
    // Returns peer ID on success
    virtual auto accept_connection(TransportServer& server) -> Result<PeerId> = 0;

    // Read data from a connected peer (blocking)
    // Returns bytes read
    virtual auto read_data(PeerId peer, char* buffer, size_t size) -> Result<size_t> = 0;

    // Write data to a connected peer
    virtual auto write_data(PeerId peer, const char* data, size_t size) -> Result<void> = 0;

    // Close connection to a peer
    virtual void close_connection(PeerId peer) = 0;

    // Stop listening and close server
    virtual void close_server(TransportServer& server) = 0;

    // ===== Pipe I/O (read/write on the transport server handle) =====

    // Read data from the transport server pipe handle.
    // Returns bytes read. Blocking call.
    virtual auto pipe_read(TransportServer& server, char* buffer, size_t size) -> Result<size_t> = 0;

    // Write data to the transport server pipe handle.
    virtual auto pipe_write(TransportServer& server, const char* data, size_t size) -> Result<void> = 0;

    // Disconnect the current client from the pipe (prepare for next connection).
    virtual void pipe_disconnect(TransportServer& server) = 0;

    // ===== Hardware: EC (Embedded Controller) =====

    // Read from EC register
    virtual auto ec_read(uint16_t reg) -> Result<uint8_t> = 0;

    // Write to EC register
    virtual auto ec_write(uint16_t reg, uint8_t val) -> Result<void> = 0;

    // ===== Hardware: SMU (System Management Unit) =====

    // Send command to SMU mailbox
    // Returns response value
    virtual auto smu_send(uint32_t msg, uint32_t arg) -> Result<uint32_t> = 0;

    // ===== Hardware: Charge Limit (Super I/O) =====

    // Write battery charge limit percentage (75-100)
    virtual auto charge_limit_write(uint8_t percent) -> Result<void> = 0;

    // ===== GPU Telemetry =====

    // Read GPU metrics via platform-specific API (ADLX on Windows, sysfs on Linux)
    virtual auto gpu_metrics() -> Result<GpuTelemetry> = 0;

    // ===== Process Management =====

    // Spawn frontend process
    virtual auto spawn_frontend(const std::filesystem::path& exe_path) -> Result<ChildProcess> = 0;

    // Show or hide frontend window
    virtual auto show_window(ChildProcess& process, bool visible) -> Result<void> = 0;

    // Wait for process to exit (blocking)
    // Returns exit code
    virtual auto wait_for_process(ChildProcess& process) -> Result<int> = 0;

    // Terminate process
    virtual void terminate_process(ChildProcess& process) = 0;

    // ===== System =====

    // Create system tray icon
    virtual auto tray_icon(TrayConfig config) -> Result<TrayHandle> = 0;

    // Update tray icon tooltip
    virtual auto update_tray_tooltip(TrayHandle& handle, const std::string& tooltip) -> Result<void> = 0;

    // Remove tray icon
    virtual void remove_tray_icon(TrayHandle& handle) = 0;

    // Get platform-specific data directory
    // Windows: %LOCALAPPDATA%\xmax\
    // Linux: $XDG_DATA_HOME/xmax/
    virtual auto data_dir() -> std::filesystem::path = 0;

    // Get path to the current executable (xmaxsvc/xmaxd)
    virtual auto self_exe_path() -> std::filesystem::path = 0;

    // Create single instance lock
    // Returns error if another instance is already running
    virtual auto single_instance_lock() -> Result<InstanceLock> = 0;

    // Release single instance lock
    virtual void release_instance_lock(InstanceLock& lock) = 0;

    // ===== System: Auto-start =====

    // Enable/disable auto-start at user login
    virtual auto set_auto_start(bool enabled, const std::filesystem::path& exe_path) -> Result<void> = 0;

    // Check if auto-start is enabled
    virtual auto is_auto_start_enabled() -> Result<bool> = 0;

    // ===== System: Message Loop =====

    // Run the platform-specific message loop (blocking).
    // Windows: GetMessage/DispatchMessage loop for tray icon.
    // Linux: epoll/glib main loop.
    // Returns when quit_message_loop() is called.
    virtual void run_message_loop() = 0;

    // Quit the message loop.
    virtual void quit_message_loop() = 0;
};

// Factory function to create platform-specific implementation
// Implemented in platform_win32.cpp or platform_linux.cpp
std::unique_ptr<Platform> create_platform();
