#pragma once

#include "shared.h"

#include <filesystem>
#include <optional>
#include <set>
#include <string>
#include <vector>
#include <map>

// Fan curve point
struct FanCurvePoint {
    int temp_c;
    int speed_pct;
};

// Fan curve definition
struct FanCurve {
    std::string id;  // Slug
    std::string name;
    std::vector<FanCurvePoint> points;
};

// Profile type
enum class ProfileType { Fixed, Adaptive };

// Profile definition
struct Profile {
    std::string id;  // Slug
    std::string name;
    ProfileType type = ProfileType::Fixed;
    std::optional<PowerState::Source> power_state;  // Assigned power state (multiple profiles can share a state)
    bool is_default = false;  // If true, this is the default profile for its power_state

    // Fixed profile fields (used when type == Fixed)
    int stapm_w = 0;
    int fast_w = 0;
    int slow_w = 0;
    std::optional<std::string> fan_curve;  // Slug reference or nullopt

    // Adaptive profile fields (used when type == Adaptive)
    std::string tuning = "default";     // "silent", "default", "performance"
    int target_temp_c = 85;
    int tdp_max_w = 55;
    int fan_max_pct = 100;
};

// Profile storage
struct ProfileStorage {
    std::map<std::string, Profile> profiles;
    std::map<std::string, FanCurve> fan_curves;
    // Built-in curve IDs that cannot be deleted or overwritten by user input.
    std::set<std::string> builtin_curves;
};

// Load profiles from file
// Returns empty storage if file doesn't exist or is corrupted
ProfileStorage load_profiles(const std::filesystem::path& profiles_path);

// Save profiles to file
bool save_profiles(const std::filesystem::path& profiles_path, const ProfileStorage& storage);

// Generate slug from name
std::string generate_slug(const std::string& name, const std::map<std::string, bool>& existing_slugs);

// Validate fan curve
bool validate_fan_curve(const FanCurve& curve, std::string& error);

// Add or update fan curve
// Returns error message if validation fails
std::optional<std::string> save_fan_curve(ProfileStorage& storage, FanCurve curve);

// Delete fan curve
// Returns error if curve is referenced by any profile
std::optional<std::string> delete_fan_curve(ProfileStorage& storage, const std::string& slug);

// Add or update profile
std::optional<std::string> save_profile(ProfileStorage& storage, Profile profile);

// Delete profile
// Returns error if profile is referenced by any power state
std::optional<std::string> delete_profile(ProfileStorage& storage, const std::string& slug);

// Interpolate fan speed from temperature using curve
int interpolate_fan_speed(const FanCurve& curve, int temp_c);

// Get all builtin fan curves (hardcoded, immutable defaults).
std::vector<FanCurve> get_builtin_fan_curves();

// Check if a curve slug is builtin (case-insensitive comparison against builtin IDs).
bool is_builtin_curve(const std::string& slug, const ProfileStorage& storage);
