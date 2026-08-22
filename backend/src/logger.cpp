#include "logger.h"

#include <iostream>
#include <chrono>
#include <iomanip>
#include <sstream>

namespace {
    bool g_debug_enabled = false;

    // Format: [YYYY-MM-DD HH:MM:SS.mmm] [LEVEL] message
    std::string format_log_message(LogLevel level, std::string_view message) {
        auto now = std::chrono::system_clock::now();
        auto time = std::chrono::system_clock::to_time_t(now);
        auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(
            now.time_since_epoch()) % 1000;

        std::tm tm_buf;
#ifdef _WIN32
        localtime_s(&tm_buf, &time);
#else
        localtime_r(&time, &tm_buf);
#endif

        std::ostringstream oss;
        oss << std::put_time(&tm_buf, "%Y-%m-%d %H:%M:%S");
        oss << "." << std::setfill('0') << std::setw(3) << ms.count();

        const char* level_str = "UNKNOWN";
        switch (level) {
            case LogLevel::Debug: level_str = "DEBUG"; break;
            case LogLevel::Info:  level_str = "INFO";  break;
            case LogLevel::Warn:  level_str = "WARN";  break;
            case LogLevel::Error: level_str = "ERROR"; break;
        }

        return std::string("[") + oss.str() + "] [" + level_str + "] " + std::string(message);
    }
}

void init_logger(bool debug_enabled) {
    g_debug_enabled = debug_enabled;
}

void log_debug(std::string_view message) {
    if (!g_debug_enabled) return;
    std::cerr << format_log_message(LogLevel::Debug, message) << std::endl;
}

void log_info(std::string_view message) {
    std::cout << format_log_message(LogLevel::Info, message) << std::endl;
}

void log_warn(std::string_view message) {
    std::cerr << format_log_message(LogLevel::Warn, message) << std::endl;
}

void log_error(std::string_view message) {
    std::cerr << format_log_message(LogLevel::Error, message) << std::endl;
}
