#include "profiles.h"
#include <nlohmann/json.hpp>
#include <fstream>
#include <iostream>
#include <algorithm>
#include <cctype>

using json = nlohmann::json;

ProfileStorage load_profiles(const std::filesystem::path& profiles_path) {
    ProfileStorage storage;

    // Register builtin curves
    for (auto& curve : get_builtin_fan_curves()) {
        storage.builtin_curves.insert(curve.id);
        storage.fan_curves[curve.id] = curve;
    }

    if (!std::filesystem::exists(profiles_path)) {
        // Create empty profiles file (builtins are already in storage)
        save_profiles(profiles_path, storage);
        return storage;
    }

    try {
        std::ifstream file(profiles_path);
        if (!file.is_open()) {
            std::cerr << "Failed to open profiles file: " << profiles_path << std::endl;
            save_profiles(profiles_path, storage);
            return storage;
        }

        json j = json::parse(file);

        // Load fan curves (skip builtins — they are set in code)
        if (j.contains("fan_curves") && j["fan_curves"].is_object()) {
            for (auto& [slug, curve_json] : j["fan_curves"].items()) {
                if (storage.builtin_curves.count(slug)) {
                    continue;  // Builtin curves cannot be overwritten by file
                }
                FanCurve curve;
                curve.id = slug;
                if (curve_json.contains("name") && curve_json["name"].is_string()) {
                    curve.name = curve_json["name"].get<std::string>();
                }
                if (curve_json.contains("points") && curve_json["points"].is_array()) {
                    for (auto& point_json : curve_json["points"]) {
                        if (point_json.contains("temp_c") && point_json.contains("speed_pct")) {
                            FanCurvePoint point;
                            point.temp_c = point_json["temp_c"].get<int>();
                            point.speed_pct = point_json["speed_pct"].get<int>();
                            curve.points.push_back(point);
                        }
                    }
                }
                storage.fan_curves[slug] = curve;
            }
        }

        // Load profiles
        if (j.contains("profiles") && j["profiles"].is_object()) {
            for (auto& [slug, profile_json] : j["profiles"].items()) {
                Profile profile;
                profile.id = slug;
                if (profile_json.contains("name") && profile_json["name"].is_string()) {
                    profile.name = profile_json["name"].get<std::string>();
                }
                if (profile_json.contains("tdp") && profile_json["tdp"].is_object()) {
                    auto& tdp = profile_json["tdp"];
                    if (tdp.contains("stapm")) profile.stapm_w = tdp["stapm"].get<int>();
                    if (tdp.contains("fast")) profile.fast_w = tdp["fast"].get<int>();
                    if (tdp.contains("slow")) profile.slow_w = tdp["slow"].get<int>();
                }
                if (profile_json.contains("fan_curve")) {
                    if (profile_json["fan_curve"].is_string()) {
                        profile.fan_curve = profile_json["fan_curve"].get<std::string>();
                    }
                }
                storage.profiles[slug] = profile;
            }
        }

    } catch (const json::exception& e) {
        std::cerr << "Profiles file corrupted, using empty storage: " << e.what() << std::endl;
        // Keep builtins, clear only user data
        storage.profiles.clear();
        for (auto it = storage.fan_curves.begin(); it != storage.fan_curves.end(); ) {
            if (storage.builtin_curves.count(it->first) == 0) {
                it = storage.fan_curves.erase(it);
            } else {
                ++it;
            }
        }
        save_profiles(profiles_path, storage);
    }

    return storage;
}

bool save_profiles(const std::filesystem::path& profiles_path, const ProfileStorage& storage) {
    try {
        json j;

        // Save fan curves (skip builtins — they are in code, not persisted to disk)
        json fan_curves_json;
        for (auto& [slug, curve] : storage.fan_curves) {
            if (storage.builtin_curves.count(slug)) {
                continue;  // Builtins are not persisted to file
            }
            json curve_json;
            curve_json["name"] = curve.name;
            json points_json = json::array();
            for (auto& point : curve.points) {
                points_json.push_back({{"temp_c", point.temp_c}, {"speed_pct", point.speed_pct}});
            }
            curve_json["points"] = points_json;
            fan_curves_json[slug] = curve_json;
        }
        j["fan_curves"] = fan_curves_json;

        // Save profiles
        json profiles_json;
        for (auto& [slug, profile] : storage.profiles) {
            json profile_json;
            profile_json["name"] = profile.name;
            profile_json["tdp"] = {{"stapm", profile.stapm_w}, {"fast", profile.fast_w}, {"slow", profile.slow_w}};
            if (profile.fan_curve.has_value()) {
                profile_json["fan_curve"] = profile.fan_curve.value();
            } else {
                profile_json["fan_curve"] = nullptr;
            }
            profiles_json[slug] = profile_json;
        }
        j["profiles"] = profiles_json;

        // Ensure directory exists
        std::filesystem::create_directories(profiles_path.parent_path());

        std::ofstream file(profiles_path);
        if (!file.is_open()) {
            return false;
        }

        file << j.dump(2);
        return true;

    } catch (const std::exception& e) {
        std::cerr << "Failed to save profiles: " << e.what() << std::endl;
        return false;
    }
}

std::string generate_slug(const std::string& name, const std::map<std::string, bool>& existing_slugs) {
    std::string slug;
    slug.reserve(name.size());

    for (char c : name) {
        if (std::isalnum(c)) {
            slug += std::tolower(c);
        } else if (c == ' ' || c == '-' || c == '_') {
            if (!slug.empty() && slug.back() != '-') {
                slug += '-';
            }
        }
    }

    // Remove trailing hyphen
    while (!slug.empty() && slug.back() == '-') {
        slug.pop_back();
    }

    if (slug.empty()) {
        slug = "profile";
    }

    // Handle collisions
    std::string base_slug = slug;
    int counter = 2;
    while (existing_slugs.find(slug) != existing_slugs.end()) {
        slug = base_slug + "-" + std::to_string(counter);
        counter++;
    }

    return slug;
}

bool validate_fan_curve(const FanCurve& curve, std::string& error) {
    if (curve.points.size() < 2) {
        error = "Fan curve must have at least 2 points";
        return false;
    }

    if (curve.points.size() > 10) {
        error = "Fan curve must have at most 10 points";
        return false;
    }

    // Check points are sorted by temperature
    for (size_t i = 1; i < curve.points.size(); i++) {
        if (curve.points[i].temp_c <= curve.points[i-1].temp_c) {
            error = "Fan curve points must be sorted by ascending temperature";
            return false;
        }
    }

    // Check speed values are in range
    for (auto& point : curve.points) {
        if (point.speed_pct < 0 || point.speed_pct > 100) {
            error = "Fan speed must be between 0 and 100";
            return false;
        }
    }

    return true;
}

// Helper: case-insensitive string comparison
static bool icase_eq(const std::string& a, const std::string& b) {
    return a.size() == b.size() &&
           std::equal(a.begin(), a.end(), b.begin(),
               [](unsigned char c, unsigned char d) { return std::tolower(c) == std::tolower(d); });
}

std::optional<std::string> save_fan_curve(ProfileStorage& storage, FanCurve curve) {
    std::string error;
    if (!validate_fan_curve(curve, error)) {
        return error;
    }

    // Generate slug if creating new curve
    if (curve.id.empty()) {
        std::map<std::string, bool> existing;
        for (auto& [slug, _] : storage.fan_curves) {
            existing[slug] = true;
        }
        curve.id = generate_slug(curve.name, existing);
    }

    // Reject if the resulting slug collides with a builtin (case-insensitive)
    for (const auto& builtin_id : storage.builtin_curves) {
        if (icase_eq(curve.id, builtin_id)) {
            return "Cannot create a fan curve with that name — it conflicts with a built-in curve";
        }
    }

    storage.fan_curves[curve.id] = curve;
    return std::nullopt;
}

std::optional<std::string> delete_fan_curve(ProfileStorage& storage, const std::string& slug) {
    // Builtin curves cannot be deleted
    if (is_builtin_curve(slug, storage)) {
        return "Built-in fan curves cannot be deleted";
    }

    // Check if curve exists
    if (storage.fan_curves.find(slug) == storage.fan_curves.end()) {
        return "Fan curve not found";
    }

    // Check if curve is referenced by any profile
    for (auto& [profile_slug, profile] : storage.profiles) {
        if (profile.fan_curve.has_value() && profile.fan_curve.value() == slug) {
            return "Fan curve is used by profile: " + profile.name;
        }
    }

    storage.fan_curves.erase(slug);
    return std::nullopt;
}

std::optional<std::string> save_profile(ProfileStorage& storage, Profile profile) {
    // Validate TDP values
    if (profile.stapm_w < 6 || profile.stapm_w > 120 ||
        profile.fast_w < 6 || profile.fast_w > 120 ||
        profile.slow_w < 6 || profile.slow_w > 120) {
        return "TDP values must be between 6 and 120W";
    }

    // Fan curve is mandatory — FE must always provide one
    if (!profile.fan_curve.has_value()) {
        return "A fan curve is required for all profiles";
    }

    // Validate fan curve reference exists
    if (storage.fan_curves.find(profile.fan_curve.value()) == storage.fan_curves.end()) {
        return "Fan curve not found: " + profile.fan_curve.value();
    }

    // Generate slug if creating new profile
    if (profile.id.empty()) {
        std::map<std::string, bool> existing;
        for (auto& [slug, _] : storage.profiles) {
            existing[slug] = true;
        }
        profile.id = generate_slug(profile.name, existing);
    }

    storage.profiles[profile.id] = profile;
    return std::nullopt;
}

std::optional<std::string> delete_profile(ProfileStorage& storage, const std::string& slug) {
    // Check if profile exists
    if (storage.profiles.find(slug) == storage.profiles.end()) {
        return "Profile not found";
    }

    // Note: In a real implementation, we'd need to check power state references
    // but that requires access to the Config, which is passed separately

    storage.profiles.erase(slug);
    return std::nullopt;
}

std::vector<FanCurve> get_builtin_fan_curves() {
    FanCurve default_curve;
    default_curve.id = "default";
    default_curve.name = "Default";
    default_curve.points = {
        {45, 35},
        {55, 45},
        {65, 60},
        {75, 75},
        {85, 85},
        {90, 100}
    };
    return {default_curve};
}

bool is_builtin_curve(const std::string& slug, const ProfileStorage& storage) {
    return storage.builtin_curves.count(slug) > 0;
}

int interpolate_fan_speed(const FanCurve& curve, int temp_c) {
    if (curve.points.empty()) {
        return 0;
    }

    // Below first point: use first point's speed
    if (temp_c <= curve.points.front().temp_c) {
        return curve.points.front().speed_pct;
    }

    // Above last point: use last point's speed
    if (temp_c >= curve.points.back().temp_c) {
        return curve.points.back().speed_pct;
    }

    // Find the two points to interpolate between
    for (size_t i = 1; i < curve.points.size(); i++) {
        if (temp_c <= curve.points[i].temp_c) {
            auto& p1 = curve.points[i-1];
            auto& p2 = curve.points[i];

            // Linear interpolation
            double t = static_cast<double>(temp_c - p1.temp_c) / (p2.temp_c - p1.temp_c);
            return static_cast<int>(p1.speed_pct + t * (p2.speed_pct - p1.speed_pct));
        }
    }

    return curve.points.back().speed_pct;
}
