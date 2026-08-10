#pragma once

#include "shared.h"
#include <string>
#include <optional>
#include <variant>

// Message types for IPC protocol
// Format: Newline-delimited JSON (\n terminated)

// Command: FE → BE
struct Command {
    std::string method;
    std::string id;  // Request ID for correlation

    // Command-specific payload (varies by method)
    // Will be parsed by specific handlers
    std::string payload;  // Raw JSON string for now
};

// Response: BE → FE
struct Response {
    std::string id;  // Echoed from request
    bool ok;
    std::optional<std::string> data;   // JSON string on success
    std::optional<ErrorCode> error;    // Error code on failure
};

// Event: BE → FE (unsolicited)
struct Event {
    std::string event;  // Event name
    std::string data;   // JSON string
};

// Error: BE → FE (for malformed input)
struct ErrorMessage {
    ErrorCode error;
};

// Parse incoming JSON line into Command
// Returns std::nullopt on parse error
std::optional<Command> parse_command(const std::string& json_line);

// Serialize Response to JSON string (with newline)
std::string serialize_response(const Response& response);

// Serialize Event to JSON string (with newline)
std::string serialize_event(const Event& event);

// Serialize ErrorMessage to JSON string (with newline)
std::string serialize_error(const ErrorMessage& error);

// Serialize Metrics to JSON string
std::string serialize_metrics(const Metrics& metrics);
