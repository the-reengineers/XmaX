#pragma once

#include <string>
#include <string_view>

// Log levels for backend logging
enum class LogLevel {
    Debug,
    Info,
    Warn,
    Error
};

// Initialize the logger. Call once at startup before any log calls.
// debug_enabled: if true, log_debug() writes to stderr; if false, log_debug() is suppressed.
void init_logger(bool debug_enabled);

// Logging API
// INFO/WARN/ERROR always go to stdout/stderr
// DEBUG goes to stderr only when init_logger(true) was called
void log_debug(std::string_view message);
void log_info(std::string_view message);
void log_warn(std::string_view message);
void log_error(std::string_view message);
