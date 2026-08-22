#include "profiles.h"
#include "logger.h"
#include <nlohmann/json.hpp>
#include <fstream>
#include <algorithm>
#include <cctype>

using json = nlohmann::json;

// Helper: parse power state string to enum
static std::optional<PowerState::Source> parse_power_state(const std::string& s) {
    if (s == "battery") return PowerState::Source::Battery;
    if (s == "usb_c_slow") return PowerState::Source::UsbCSlow;
    if (s == "usb_c_fast") return PowerState::Source::UsbCFast;
    if (s == "dc_in") return PowerState::Source::DcIn;
    return std::nullopt;
}

// Helper: convert power state enum to string
static std::string power_state_to_string(PowerState::Source s) {
    switch (s) {
        case PowerState::Source::Battery:  return "battery";
        case PowerState::Source::UsbCSlow: return "usb_c_slow";
        case PowerState::Source::UsbCFast: return "usb_c_fast";
        case PowerState::Source::DcIn:     return "dc_in";
        default:                           return "";
    }
}

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
            log_error("Failed to open profiles file: " + profiles_path.string());
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

                // Parse type (required)
                if (profile_json.contains("type") && profile_json["type"].is_string()) {
                    std::string type_str = profile_json["type"].get<std::string>();
                    profile.type = (type_str == "adaptive") ? ProfileType::Adaptive : ProfileType::Fixed;
                }

                // Parse power_state (optional)
                if (profile_json.contains("power_state") && profile_json["power_state"].is_string()) {
                    profile.power_state = parse_power_state(profile_json["power_state"].get<std::string>());
                }

                // Parse is_default (optional, defaults to false)
                if (profile_json.contains("is_default") && profile_json["is_default"].is_boolean()) {
                    profile.is_default = profile_json["is_default"].get<bool>();
                }

                if (profile.type == ProfileType::Fixed) {
                    // Fixed profile fields
                    if (profile_json.contains("tdp") && profile_json["tdp"].is_object()) {
                        auto& tdp = profile_json["tdp"];
                        if (tdp.contains("stapm")) profile.stapm_w = tdp["stapm"].get<int>();
                        if (tdp.contains("fast")) profile.fast_w = tdp["fast"].get<int>();
                        if (tdp.contains("slow")) profile.slow_w = tdp["slow"].get<int>();
                    }
                    if (profile_json.contains("fan_curve") && profile_json["fan_curve"].is_string()) {
                        profile.fan_curve = profile_json["fan_curve"].get<std::string>();
                    }
                } else {
                    // Adaptive profile fields
                    if (profile_json.contains("tuning") && profile_json["tuning"].is_string()) {
                        profile.tuning = profile_json["tuning"].get<std::string>();
                    }
                    if (profile_json.contains("target_temp_c") && profile_json["target_temp_c"].is_number_integer()) {
                        profile.target_temp_c = profile_json["target_temp_c"].get<int>();
                    }
                    if (profile_json.contains("tdp_max_w") && profile_json["tdp_max_w"].is_number_integer()) {
                        profile.tdp_max_w = profile_json["tdp_max_w"].get<int>();
                    }
                    if (profile_json.contains("fan_max_pct") && profile_json["fan_max_pct"].is_number_integer()) {
                        profile.fan_max_pct = profile_json["fan_max_pct"].get<int>();
                    }
                }

                storage.profiles[slug] = profile;
            }
        }

    } catch (const json::exception& e) {
        log_error("Profiles file corrupted, using empty storage: " + std::string(e.what()));
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
            profile_json["type"] = (profile.type == ProfileType::Adaptive) ? "adaptive" : "fixed";

            if (profile.power_state.has_value()) {
                profile_json["power_state"] = power_state_to_string(profile.power_state.value());
            } else {
                profile_json["power_state"] = nullptr;
            }

            profile_json["is_default"] = profile.is_default;

            if (profile.type == ProfileType::Fixed) {
                profile_json["tdp"] = {{"stapm", profile.stapm_w}, {"fast", profile.fast_w}, {"slow", profile.slow_w}};
                if (profile.fan_curve.has_value()) {
                    profile_json["fan_curve"] = profile.fan_curve.value();
                } else {
                    profile_json["fan_curve"] = nullptr;
                }
            } else {
                profile_json["tuning"] = profile.tuning;
                profile_json["target_temp_c"] = profile.target_temp_c;
                profile_json["tdp_max_w"] = profile.tdp_max_w;
                profile_json["fan_max_pct"] = profile.fan_max_pct;
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
        log_error("Failed to save profiles: " + std::string(e.what()));
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
    if (profile.type == ProfileType::Fixed) {
        // Determine TDP ceiling: power state max if assigned, otherwise global max
        int tdp_ceiling = profile.power_state.has_value()
            ? power_state_max_tdp(profile.power_state.value())
            : 120;

        // Validate TDP values against ceiling
        if (profile.stapm_w < 6 || profile.stapm_w > tdp_ceiling ||
            profile.fast_w < 6 || profile.fast_w > tdp_ceiling ||
            profile.slow_w < 6 || profile.slow_w > tdp_ceiling) {
            return "TDP values must be between 6 and " + std::to_string(tdp_ceiling) + "W";
        }

        // Fan curve is mandatory for fixed profiles
        if (!profile.fan_curve.has_value()) {
            return "A fan curve is required for fixed profiles";
        }

        // Validate fan curve reference exists
        if (storage.fan_curves.find(profile.fan_curve.value()) == storage.fan_curves.end()) {
            return "Fan curve not found: " + profile.fan_curve.value();
        }
    } else {
        // Adaptive profile validation
        if (profile.tuning != "silent" && profile.tuning != "default" && profile.tuning != "performance") {
            return "Tuning must be 'silent', 'default', or 'performance'";
        }
        if (profile.target_temp_c < 50 || profile.target_temp_c > 100) {
            return "Target temperature must be between 50 and 100°C";
        }

        int tdp_ceiling = profile.power_state.has_value()
            ? power_state_max_tdp(profile.power_state.value())
            : 120;

        if (profile.tdp_max_w < 6 || profile.tdp_max_w > tdp_ceiling) {
            return "TDP max must be between 6 and " + std::to_string(tdp_ceiling) + "W";
        }
        if (profile.fan_max_pct < 0 || profile.fan_max_pct > 100) {
            return "Fan max must be between 0 and 100%";
        }
    }

    // Manage is_default for power state assignment
    if (profile.power_state.has_value()) {
        auto ps = profile.power_state.value();

        // Check if there's already a default for this power state
        bool has_existing_default = false;
        for (auto& [existing_slug, existing_profile] : storage.profiles) {
            if (existing_slug == profile.id) continue;  // Skip self on update
            if (existing_profile.power_state.has_value() &&
                existing_profile.power_state.value() == ps &&
                existing_profile.is_default) {
                has_existing_default = true;
                break;
            }
        }

        // If no existing default, this profile becomes the default
        if (!has_existing_default) {
            profile.is_default = true;
        }

        // If this profile is being set as default, clear default from others with same power state
        if (profile.is_default) {
            for (auto& [existing_slug, existing_profile] : storage.profiles) {
                if (existing_slug == profile.id) continue;
                if (existing_profile.power_state.has_value() &&
                    existing_profile.power_state.value() == ps &&
                    existing_profile.is_default) {
                    storage.profiles[existing_slug].is_default = false;
                }
            }
        }
    } else {
        // No power state assigned — clear is_default
        profile.is_default = false;
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
    auto it = storage.profiles.find(slug);
    if (it == storage.profiles.end()) {
        return "Profile not found";
    }

    // If deleting the default profile for a power state, promote another profile
    auto& deleted = it->second;
    if (deleted.power_state.has_value() && deleted.is_default) {
        auto ps = deleted.power_state.value();
        // Find another profile with the same power state to promote
        for (auto& [existing_slug, existing_profile] : storage.profiles) {
            if (existing_slug == slug) continue;
            if (existing_profile.power_state.has_value() &&
                existing_profile.power_state.value() == ps) {
                storage.profiles[existing_slug].is_default = true;
                break;  // Promote the first one found
            }
        }
    }

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
