#include "config.h"
#include <nlohmann/json.hpp>
#include <fstream>
#include <iostream>

using json = nlohmann::json;

Config get_default_config() {
    Config config;
    config.language = "auto";
    config.theme = "system";
    config.persist = false;
    config.charge_limit_pct = 100;
    config.auto_start = false;
    config.auto_tune = std::nullopt;
    config.power_state_profiles.battery = {"", 25};
    config.power_state_profiles.usb_c_slow = {"", 35};
    config.power_state_profiles.usb_c_fast = {"", 45};
    config.power_state_profiles.dc_in = {"", 55};
    return config;
}

Config load_config(const std::filesystem::path& config_path) {
    Config config = get_default_config();

    if (!std::filesystem::exists(config_path)) {
        // Create default config file
        config.session_persist = config.persist;  // Initialize session_persist
        save_config(config_path, config);
        return config;
    }

    try {
        std::ifstream file(config_path);
        if (!file.is_open()) {
            std::cerr << "Failed to open config file: " << config_path << std::endl;
            save_config(config_path, config);
            return config;
        }

        json j = json::parse(file);

        // Parse fields with defaults
        if (j.contains("language") && j["language"].is_string()) {
            config.language = j["language"].get<std::string>();
        }
        if (j.contains("theme") && j["theme"].is_string()) {
            config.theme = j["theme"].get<std::string>();
        }
        if (j.contains("persist") && j["persist"].is_boolean()) {
            config.persist = j["persist"].get<bool>();
        }
        if (j.contains("charge_limit_pct") && j["charge_limit_pct"].is_number_integer()) {
            config.charge_limit_pct = j["charge_limit_pct"].get<int>();
        }
        if (j.contains("auto_start") && j["auto_start"].is_boolean()) {
            config.auto_start = j["auto_start"].get<bool>();
        }

        // Parse auto_tune
        if (j.contains("auto_tune") && j["auto_tune"].is_object()) {
            AutoTuneConfig auto_tune;
            auto& at = j["auto_tune"];
            if (at.contains("enabled") && at["enabled"].is_boolean()) {
                auto_tune.enabled = at["enabled"].get<bool>();
            }
            if (at.contains("tuning") && at["tuning"].is_string()) {
                auto_tune.tuning = at["tuning"].get<std::string>();
            }
            if (at.contains("target_temp_c") && at["target_temp_c"].is_number_integer()) {
                auto_tune.target_temp_c = at["target_temp_c"].get<int>();
            }
            if (at.contains("tdp_max_w") && at["tdp_max_w"].is_number_integer()) {
                auto_tune.tdp_max_w = at["tdp_max_w"].get<int>();
            }
            if (at.contains("fan_max_pct") && at["fan_max_pct"].is_number_integer()) {
                auto_tune.fan_max_pct = at["fan_max_pct"].get<int>();
            }
            config.auto_tune = auto_tune;
        }

        // Parse power state profiles
        if (j.contains("power_state_profiles") && j["power_state_profiles"].is_object()) {
            auto& psp = j["power_state_profiles"];

            auto parse_power_state = [](const json& parent, const std::string& key, PowerStateProfile& profile) {
                if (parent.contains(key) && parent[key].is_object()) {
                    auto& ps = parent[key];
                    if (ps.contains("profile") && ps["profile"].is_string()) {
                        profile.profile = ps["profile"].get<std::string>();
                    }
                    if (ps.contains("tdp_max_w") && ps["tdp_max_w"].is_number_integer()) {
                        profile.tdp_max_w = ps["tdp_max_w"].get<int>();
                    }
                }
            };

            parse_power_state(psp, "battery", config.power_state_profiles.battery);
            parse_power_state(psp, "usb_c_slow", config.power_state_profiles.usb_c_slow);
            parse_power_state(psp, "usb_c_fast", config.power_state_profiles.usb_c_fast);
            parse_power_state(psp, "dc_in", config.power_state_profiles.dc_in);
        }

        // Parse home layout
        if (j.contains("home_layout") && j["home_layout"].is_object()) {
            auto& hl = j["home_layout"];
            if (hl.contains("widget_order") && hl["widget_order"].is_array()) {
                config.home_layout.widget_order.clear();
                for (const auto& item : hl["widget_order"]) {
                    if (item.is_string()) {
                        config.home_layout.widget_order.push_back(item.get<std::string>());
                    }
                }
            }
            if (hl.contains("widget_visibility") && hl["widget_visibility"].is_object()) {
                config.home_layout.widget_visibility.clear();
                for (auto it = hl["widget_visibility"].begin(); it != hl["widget_visibility"].end(); ++it) {
                    if (it.value().is_boolean()) {
                        config.home_layout.widget_visibility[it.key()] = it.value().get<bool>();
                    }
                }
            }
            if (hl.contains("columns") && hl["columns"].is_number_integer()) {
                config.home_layout.columns = hl["columns"].get<int>();
            }
        }

        // Initialize session_persist from persist (session_persist is in-memory only)
        config.session_persist = config.persist;

        // Validate and fix
        if (validate_config(config)) {
            save_config(config_path, config);
        }

    } catch (const json::exception& e) {
        std::cerr << "Config file corrupted, using defaults: " << e.what() << std::endl;
        config = get_default_config();
        config.session_persist = config.persist;  // Initialize session_persist
        save_config(config_path, config);
    }

    return config;
}

bool save_config(const std::filesystem::path& config_path, const Config& config) {
    try {
        json j;
        j["language"] = config.language;
        j["theme"] = config.theme;
        j["persist"] = config.persist;
        j["charge_limit_pct"] = config.charge_limit_pct;
        j["auto_start"] = config.auto_start;

        if (config.auto_tune.has_value()) {
            json at;
            at["enabled"] = config.auto_tune->enabled;
            at["tuning"] = config.auto_tune->tuning;
            at["target_temp_c"] = config.auto_tune->target_temp_c;
            at["tdp_max_w"] = config.auto_tune->tdp_max_w;
            at["fan_max_pct"] = config.auto_tune->fan_max_pct;
            j["auto_tune"] = at;
        }

        auto& psp = config.power_state_profiles;
        j["power_state_profiles"] = {
            {"battery", {{"profile", psp.battery.profile}, {"tdp_max_w", psp.battery.tdp_max_w}}},
            {"usb_c_slow", {{"profile", psp.usb_c_slow.profile}, {"tdp_max_w", psp.usb_c_slow.tdp_max_w}}},
            {"usb_c_fast", {{"profile", psp.usb_c_fast.profile}, {"tdp_max_w", psp.usb_c_fast.tdp_max_w}}},
            {"dc_in", {{"profile", psp.dc_in.profile}, {"tdp_max_w", psp.dc_in.tdp_max_w}}}
        };

        // Home layout
        json widget_order = json::array();
        for (const auto& id : config.home_layout.widget_order) {
            widget_order.push_back(id);
        }
        json widget_visibility = json::object();
        for (const auto& [key, value] : config.home_layout.widget_visibility) {
            widget_visibility[key] = value;
        }
        j["home_layout"] = {
            {"widget_order", widget_order},
            {"widget_visibility", widget_visibility},
            {"columns", config.home_layout.columns}
        };

        // Ensure directory exists
        std::filesystem::create_directories(config_path.parent_path());

        std::ofstream file(config_path);
        if (!file.is_open()) {
            return false;
        }

        file << j.dump(2);
        return true;

    } catch (const std::exception& e) {
        std::cerr << "Failed to save config: " << e.what() << std::endl;
        return false;
    }
}

bool validate_config(Config& config) {
    bool modified = false;

    // Validate language
    if (config.language != "auto" && config.language != "en" && config.language != "zh") {
        config.language = "auto";
        modified = true;
    }

    // Validate theme
    if (config.theme != "system" && config.theme != "light" && config.theme != "dark") {
        config.theme = "system";
        modified = true;
    }

    // Validate charge limit
    if (config.charge_limit_pct < 75 || config.charge_limit_pct > 100) {
        config.charge_limit_pct = 100;
        modified = true;
    }

    // Validate auto_tune
    if (config.auto_tune.has_value()) {
        auto& at = config.auto_tune.value();
        if (at.tuning != "silent" && at.tuning != "default" && at.tuning != "performance") {
            at.tuning = "default";
            modified = true;
        }
        if (at.target_temp_c < 50 || at.target_temp_c > 100) {
            at.target_temp_c = 85;
            modified = true;
        }
        if (at.tdp_max_w < 6 || at.tdp_max_w > 120) {
            at.tdp_max_w = 55;
            modified = true;
        }
        if (at.fan_max_pct < 0 || at.fan_max_pct > 100) {
            at.fan_max_pct = 100;
            modified = true;
        }
    }

    // Validate power state TDP limits
    auto validate_power_state = [&modified](PowerStateProfile& ps, int default_tdp) {
        if (ps.tdp_max_w < 6 || ps.tdp_max_w > 120) {
            ps.tdp_max_w = default_tdp;
            modified = true;
        }
    };

    validate_power_state(config.power_state_profiles.battery, 25);
    validate_power_state(config.power_state_profiles.usb_c_slow, 35);
    validate_power_state(config.power_state_profiles.usb_c_fast, 45);
    validate_power_state(config.power_state_profiles.dc_in, 55);

    // Validate home layout columns (3-4 only)
    if (config.home_layout.columns != 3 && config.home_layout.columns != 4) {
        config.home_layout.columns = 3;
        modified = true;
    }

    return modified;
}
