#include "config.h"
#include "logger.h"
#include <nlohmann/json.hpp>
#include <fstream>

using json = nlohmann::json;

Config get_default_config() {
    Config config;
    config.language = "auto";
    config.theme = "system";
    config.persist = false;
    config.charge_limit_pct = 100;
    config.auto_start = false;
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
            log_error("Failed to open config file: " + config_path.string());
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

        // Parse home layout
        if (j.contains("home_layout") && j["home_layout"].is_object()) {
            auto& hl = j["home_layout"];

            // New format: widgets array with per-widget size
            if (hl.contains("widgets") && hl["widgets"].is_array()) {
                config.home_layout.widgets.clear();
                for (const auto& item : hl["widgets"]) {
                    if (item.is_object()) {
                        WidgetEntry entry;
                        if (item.contains("id") && item["id"].is_string()) {
                            entry.id = item["id"].get<std::string>();
                        }
                        if (item.contains("col_span") && item["col_span"].is_number_integer()) {
                            entry.col_span = item["col_span"].get<int>();
                        }
                        if (item.contains("row_span") && item["row_span"].is_number_integer()) {
                            entry.row_span = item["row_span"].get<int>();
                        }
                        if (!entry.id.empty()) {
                            config.home_layout.widgets.push_back(entry);
                        }
                    }
                }
            }

            if (hl.contains("columns") && hl["columns"].is_number_integer()) {
                config.home_layout.columns = hl["columns"].get<int>();
            }
            if (hl.contains("column_width") && hl["column_width"].is_number_integer()) {
                config.home_layout.column_width = hl["column_width"].get<int>();
            }
            if (hl.contains("window_height_rows") && hl["window_height_rows"].is_number_integer()) {
                config.home_layout.window_height_rows = hl["window_height_rows"].get<int>();
            }

            // Parse hidden widgets array
            if (hl.contains("hidden_widgets") && hl["hidden_widgets"].is_array()) {
                config.home_layout.hidden_widgets.clear();
                for (const auto& item : hl["hidden_widgets"]) {
                    if (item.is_string()) {
                        config.home_layout.hidden_widgets.push_back(item.get<std::string>());
                    }
                }
            }
        }

        // Initialize session_persist from persist (session_persist is in-memory only)
        config.session_persist = config.persist;

        // Validate and fix
        if (validate_config(config)) {
            save_config(config_path, config);
        }

    } catch (const json::exception& e) {
        log_error("Config file corrupted, using defaults: " + std::string(e.what()));
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

        // Home layout
        json widgets_array = json::array();
        for (const auto& w : config.home_layout.widgets) {
            widgets_array.push_back({
                {"id", w.id},
                {"col_span", w.col_span},
                {"row_span", w.row_span}
            });
        }

        json hidden_widgets_array = json::array();
        for (const auto& id : config.home_layout.hidden_widgets) {
            hidden_widgets_array.push_back(id);
        }

        j["home_layout"] = {
            {"widgets", widgets_array},
            {"hidden_widgets", hidden_widgets_array},
            {"columns", config.home_layout.columns},
            {"column_width", config.home_layout.column_width},
            {"window_height_rows", config.home_layout.window_height_rows}
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
        log_error("Failed to save config: " + std::string(e.what()));
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

    // Validate home layout columns (3-4 only)
    if (config.home_layout.columns != 3 && config.home_layout.columns != 4) {
        config.home_layout.columns = 3;
        modified = true;
    }

    // Validate widget entries
    for (auto& w : config.home_layout.widgets) {
        if (w.col_span < 1 || w.col_span > config.home_layout.columns) {
            w.col_span = 1;
            modified = true;
        }
        if (w.row_span < 1) {
            w.row_span = 1;
            modified = true;
        }
    }

    return modified;
}
