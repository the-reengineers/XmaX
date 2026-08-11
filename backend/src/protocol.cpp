#include "protocol.h"
#include <nlohmann/json.hpp>

using json = nlohmann::json;

std::string error_code_to_string(ErrorCode code) {
    switch (code) {
        case ErrorCode::TdpOutOfRange: return "tdp_out_of_range";
        case ErrorCode::FanSpeedInvalid: return "fan_speed_invalid";
        case ErrorCode::ChargeLimitInvalid: return "charge_limit_invalid";
        case ErrorCode::FanCurveInvalid: return "fan_curve_invalid";
        case ErrorCode::UnknownCommand: return "unknown_command";
        case ErrorCode::ParseError: return "parse_error";
        case ErrorCode::HardwareBusy: return "hardware_busy";
        case ErrorCode::SensorUnavailable: return "sensor_unavailable";
        case ErrorCode::ChargeLimitWriteFail: return "charge_limit_write_fail";
        case ErrorCode::ProfileNotFound: return "profile_not_found";
        case ErrorCode::FanCurveNotFound: return "fan_curve_not_found";
        case ErrorCode::FanCurveInUse: return "fan_curve_in_use";
        case ErrorCode::ProfileInUse: return "profile_in_use";
        case ErrorCode::PersistDisabled: return "persist_disabled";
        default: return "unknown";
    }
}

std::optional<Command> parse_command(const std::string& json_line) {
    try {
        auto j = json::parse(json_line);

        if (!j.contains("type") || j["type"] != "command") {
            return std::nullopt;
        }

        if (!j.contains("method") || !j["method"].is_string()) {
            return std::nullopt;
        }

        if (!j.contains("id") || !j["id"].is_string()) {
            return std::nullopt;
        }

        Command cmd;
        cmd.method = j["method"].get<std::string>();
        cmd.id = j["id"].get<std::string>();

        // Extract payload from "params" field (JSON-RPC style)
        if (j.contains("params") && j["params"].is_object()) {
            cmd.payload = j["params"].dump();
        } else {
            cmd.payload = "{}";
        }

        return cmd;
    } catch (const json::exception& e) {
        return std::nullopt;
    }
}

std::string serialize_response(const Response& response) {
    json j;
    j["type"] = "response";
    j["id"] = response.id;
    j["ok"] = response.ok;

    if (response.ok && response.data.has_value()) {
        j["data"] = json::parse(response.data.value());
    } else if (!response.ok && response.error.has_value()) {
        j["error"] = error_code_to_string(response.error.value());
    }

    return j.dump() + "\n";
}

std::string serialize_event(const Event& event) {
    json j;
    j["type"] = "event";
    j["event"] = event.event;
    j["data"] = json::parse(event.data);

    return j.dump() + "\n";
}

std::string serialize_error(const ErrorMessage& error) {
    json j;
    j["type"] = "error";
    j["error"] = error_code_to_string(error.error);

    return j.dump() + "\n";
}

std::string serialize_metrics(const Metrics& metrics) {
    json j;

    // CPU
    j["cpu"]["util_pct"] = metrics.cpu.util_pct;
    j["cpu"]["clock_mhz"] = metrics.cpu.clock_mhz;
    if (metrics.cpu.temp_c.has_value()) {
        j["cpu"]["temp_c"] = metrics.cpu.temp_c.value();
    } else {
        j["cpu"]["temp_c"] = nullptr;
    }
    if (metrics.cpu.package_watts.has_value()) {
        j["cpu"]["package_watts"] = metrics.cpu.package_watts.value();
    } else {
        j["cpu"]["package_watts"] = nullptr;
    }

    // GPU
    j["gpu"]["util_pct"] = metrics.gpu.util_pct;
    j["gpu"]["clock_mhz"] = metrics.gpu.clock_mhz;
    if (metrics.gpu.temp_c.has_value()) {
        j["gpu"]["temp_c"] = metrics.gpu.temp_c.value();
    } else {
        j["gpu"]["temp_c"] = nullptr;
    }
    if (metrics.gpu.power_w.has_value()) {
        j["gpu"]["power_w"] = metrics.gpu.power_w.value();
    } else {
        j["gpu"]["power_w"] = nullptr;
    }
    if (metrics.gpu.vram_used_mb.has_value()) {
        j["gpu"]["vram_used_mb"] = metrics.gpu.vram_used_mb.value();
    } else {
        j["gpu"]["vram_used_mb"] = nullptr;
    }
    if (metrics.gpu.vram_total_mb.has_value()) {
        j["gpu"]["vram_total_mb"] = metrics.gpu.vram_total_mb.value();
    } else {
        j["gpu"]["vram_total_mb"] = nullptr;
    }

    // RAM
    j["ram"]["used_gb"] = metrics.ram.used_gb;
    j["ram"]["total_gb"] = metrics.ram.total_gb;
    j["ram"]["avail_gb"] = metrics.ram.avail_gb;
    j["ram"]["load_pct"] = metrics.ram.load_pct;

    // Fan
    std::string fan_mode;
    switch (metrics.fan.mode) {
        case FanState::Mode::Auto: fan_mode = "auto"; break;
        case FanState::Mode::Manual: fan_mode = "manual"; break;
        case FanState::Mode::Curve: fan_mode = "curve"; break;
    }
    j["fan"]["mode"] = fan_mode;
    j["fan"]["speed_pct"] = metrics.fan.speed_pct;
    j["fan"]["rpm"] = metrics.fan.rpm;

    // Power
    std::string power_mode;
    switch (metrics.power.mode) {
        case PowerState::Source::Battery: power_mode = "battery"; break;
        case PowerState::Source::UsbCSlow: power_mode = "usb_c_slow"; break;
        case PowerState::Source::UsbCFast: power_mode = "usb_c_fast"; break;
        case PowerState::Source::DcIn: power_mode = "dc_in"; break;
        case PowerState::Source::Unknown: power_mode = "unknown"; break;
    }
    j["power"]["mode"] = power_mode;
    j["power"]["label"] = metrics.power.label;
    if (metrics.power.battery_pct.has_value()) {
        j["power"]["battery_pct"] = metrics.power.battery_pct.value();
    } else {
        j["power"]["battery_pct"] = nullptr;
    }
    if (metrics.power.charge_limit_pct.has_value()) {
        j["power"]["charge_limit_pct"] = metrics.power.charge_limit_pct.value();
    } else {
        j["power"]["charge_limit_pct"] = nullptr;
    }

    j["ts"] = metrics.ts;

    return j.dump() + "\n";
}
