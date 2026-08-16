#pragma once

#include <filesystem>
#include <map>
#include <optional>
#include <string>
#include <vector>

// Configuration structures

struct WidgetEntry {
    std::string id;
    int col_span = 1;
    int row_span = 1;
};

struct HomeLayout {
    std::vector<WidgetEntry> widgets;
    int columns = 3;  // 3-4 columns
    int column_width = 140;  // Base column width in pixels (at 100% DPI)
    int window_height = 600;  // Window height in pixels (at 100% DPI)
};

struct Config {
    std::string language = "auto";
    std::string theme = "system";
    bool persist = false;
    bool session_persist = false;  // In-memory only, not serialized to JSON. Initialized from persist on startup.
    int charge_limit_pct = 100;
    bool auto_start = false;
    HomeLayout home_layout;
};

// Load config from file
// Returns default config if file doesn't exist or is corrupted
Config load_config(const std::filesystem::path& config_path);

// Save config to file
bool save_config(const std::filesystem::path& config_path, const Config& config);

// Get default config
Config get_default_config();

// Validate config and fix invalid values
// Returns true if config was modified
bool validate_config(Config& config);
