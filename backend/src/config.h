#pragma once

#include <filesystem>
#include <optional>
#include <string>

// Configuration structures

struct AutoTuneConfig {
    bool enabled = false;
    std::string tuning = "default";  // "silent", "default", "performance"
    int target_temp_c = 85;
    int tdp_max_w = 55;
    int fan_max_pct = 100;
};

struct PowerStateProfile {
    std::string profile;  // Slug reference
    int tdp_max_w = 25;
};

struct PowerStateProfiles {
    PowerStateProfile battery;
    PowerStateProfile usb_c_slow;
    PowerStateProfile usb_c_fast;
    PowerStateProfile dc_in;
};

struct Config {
    std::string language = "auto";
    std::string theme = "system";
    bool persist = false;
    bool session_persist = false;  // In-memory only, not serialized to JSON. Initialized from persist on startup.
    int charge_limit_pct = 100;
    bool auto_start = false;
    std::optional<AutoTuneConfig> auto_tune;
    PowerStateProfiles power_state_profiles;
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
