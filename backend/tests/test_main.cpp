#include <gtest/gtest.h>
#include <nlohmann/json.hpp>
#include "../src/protocol.h"
#include "../src/config.h"
#include "../src/profiles.h"
#include "../src/fan.h"
#include "../src/tdp.h"
#include "../src/metrics.h"
#include "../src/power.h"
#include "../src/adaptive.h"
#include "../src/button.h"
#include "../src/transport.h"
#include "../src/process.h"
#include "../src/tray.h"
#include "../src/platform/platform.h"
#include <filesystem>
#include <fstream>
#include <map>
#include <vector>
#include <chrono>

namespace fs = std::filesystem;

// ===== Mock Platform for unit tests =====

class MockPlatform : public Platform {
public:
    // In-memory EC registers
    std::map<uint16_t, uint8_t> ec_registers;

    // SMU call tracking
    struct SmuCall {
        uint32_t msg;
        uint32_t arg;
    };
    std::vector<SmuCall> smu_calls;
    std::map<uint32_t, uint32_t> smu_responses;  // msg → response value
    bool smu_should_fail = false;

    // GPU telemetry mock
    std::optional<GpuTelemetry> gpu_telemetry;
    bool gpu_should_fail = false;

    // Charge limit mock
    bool charge_limit_should_fail = false;
    std::optional<uint8_t> last_charge_limit_written;

    // Process management mock
    int spawn_count = 0;
    std::filesystem::path last_spawn_path;
    bool spawn_should_fail = false;

    struct ShowWindowCall {
        uint64_t pid;
        bool visible;
    };
    std::vector<ShowWindowCall> show_window_calls;
    bool show_window_should_fail = false;

    bool wait_returns_immediately = false;
    int wait_exit_code = 0;
    bool wait_should_fail = false;

    int terminate_count = 0;

    // Tray icon mock
    int tray_create_count = 0;
    int tray_remove_count = 0;
    std::string last_tray_tooltip;
    bool tray_should_fail = false;

    // EC access
    auto ec_read(uint16_t reg) -> Result<uint8_t> override {
        auto it = ec_registers.find(reg);
        if (it != ec_registers.end()) {
            return it->second;
        }
        return 0;  // Default to 0 for unset registers
    }

    auto ec_write(uint16_t reg, uint8_t val) -> Result<void> override {
        ec_registers[reg] = val;
        return {};
    }

    // SMU access
    auto smu_send(uint32_t msg, uint32_t arg) -> Result<uint32_t> override {
        smu_calls.push_back({msg, arg});
        if (smu_should_fail) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }
        auto it = smu_responses.find(msg);
        if (it != smu_responses.end()) {
            return it->second;
        }
        return 0;  // Default response
    }

    // Stub implementations for other methods (not used in tests)
    auto listen() -> Result<TransportServer> override { return std::unexpected(ErrorCode::HardwareBusy); }
    auto verify_peer(PeerId) -> Result<PeerInfo> override { return std::unexpected(ErrorCode::HardwareBusy); }
    auto accept_connection(TransportServer&) -> Result<PeerId> override { return std::unexpected(ErrorCode::HardwareBusy); }
    auto read_data(PeerId, char*, size_t) -> Result<size_t> override { return std::unexpected(ErrorCode::HardwareBusy); }
    auto write_data(PeerId, const char*, size_t) -> Result<void> override { return std::unexpected(ErrorCode::HardwareBusy); }
    void close_connection(PeerId) override {}
    void close_server(TransportServer&) override {}
    auto pipe_read(TransportServer&, char*, size_t) -> Result<size_t> override { return std::unexpected(ErrorCode::HardwareBusy); }
    auto pipe_write(TransportServer&, const char*, size_t) -> Result<void> override { return std::unexpected(ErrorCode::HardwareBusy); }
    void pipe_flush(TransportServer&) override {}
    void pipe_disconnect(TransportServer&) override {}
    auto charge_limit_write(uint8_t percent) -> Result<void> override {
        if (charge_limit_should_fail) {
            return std::unexpected(ErrorCode::ChargeLimitWriteFail);
        }
        last_charge_limit_written = percent;
        return {};
    }
    auto gpu_metrics() -> Result<GpuTelemetry> override {
        if (gpu_should_fail) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }
        if (gpu_telemetry.has_value()) {
            return gpu_telemetry.value();
        }
        return GpuTelemetry{};
    }
    auto spawn_frontend(const std::filesystem::path& path) -> Result<ChildProcess> override {
        spawn_count++;
        last_spawn_path = path;
        if (spawn_should_fail) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }
        ChildProcess child;
        child.pid = 1000 + spawn_count;
        child.process_handle = reinterpret_cast<void*>(static_cast<uintptr_t>(child.pid));
        return child;
    }
    auto show_window(ChildProcess& proc, bool visible) -> Result<void> override {
        show_window_calls.push_back({proc.pid, visible});
        if (show_window_should_fail) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }
        return {};
    }
    auto wait_for_process(ChildProcess&) -> Result<int> override {
        if (wait_should_fail) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }
        if (wait_returns_immediately) {
            return wait_exit_code;
        }
        // Simulate blocking by sleeping briefly
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
        return wait_exit_code;
    }
    void terminate_process(ChildProcess&) override {
        terminate_count++;
    }
    auto tray_icon(TrayConfig config) -> Result<TrayHandle> override {
        tray_create_count++;
        if (tray_should_fail) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }
        TrayHandle handle;
        handle.handle = reinterpret_cast<void*>(static_cast<uintptr_t>(tray_create_count));
        return handle;
    }
    auto update_tray_tooltip(TrayHandle&, const std::string& tooltip) -> Result<void> override {
        last_tray_tooltip = tooltip;
        if (tray_should_fail) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }
        return {};
    }
    void remove_tray_icon(TrayHandle&) override {
        tray_remove_count++;
    }
    auto data_dir() -> std::filesystem::path override { return ""; }
    auto self_exe_path() -> std::filesystem::path override { return ""; }
    auto single_instance_lock() -> Result<InstanceLock> override { return std::unexpected(ErrorCode::HardwareBusy); }
    void release_instance_lock(InstanceLock&) override {}
    auto set_auto_start(bool, const std::filesystem::path&) -> Result<void> override { return std::unexpected(ErrorCode::HardwareBusy); }
    auto is_auto_start_enabled() -> Result<bool> override { return std::unexpected(ErrorCode::HardwareBusy); }
    void run_message_loop() override {}
    void quit_message_loop() override {}
    bool init_hardware() override { return true; }
};

// ===== Protocol Tests =====

TEST(ProtocolTest, ParseValidCommand) {
    std::string json = R"({"type": "command", "method": "get_metrics", "id": "req_001"})";
    auto cmd = parse_command(json);

    ASSERT_TRUE(cmd.has_value());
    EXPECT_EQ(cmd->method, "get_metrics");
    EXPECT_EQ(cmd->id, "req_001");
    EXPECT_EQ(cmd->payload, "{}");
}

TEST(ProtocolTest, ParseCommandWithPayload) {
    std::string json = R"({"type": "command", "method": "set_fan", "id": "req_002", "params": {"mode": "auto"}})";
    auto cmd = parse_command(json);

    ASSERT_TRUE(cmd.has_value());
    EXPECT_EQ(cmd->method, "set_fan");
    EXPECT_EQ(cmd->id, "req_002");
    EXPECT_NE(cmd->payload.find("auto"), std::string::npos);
}

TEST(ProtocolTest, ParseMalformedJson) {
    std::string json = "{invalid json}";
    auto cmd = parse_command(json);
    EXPECT_FALSE(cmd.has_value());
}

TEST(ProtocolTest, ParseWrongType) {
    std::string json = R"({"type": "event", "event": "button_press", "data": {}})";
    auto cmd = parse_command(json);
    EXPECT_FALSE(cmd.has_value());
}

TEST(ProtocolTest, SerializeResponse) {
    Response resp;
    resp.id = "req_001";
    resp.ok = true;
    resp.data = R"({"stapm": 45})";

    std::string json = serialize_response(resp);

    EXPECT_NE(json.find("\"type\":\"response\""), std::string::npos);
    EXPECT_NE(json.find("\"id\":\"req_001\""), std::string::npos);
    EXPECT_NE(json.find("\"ok\":true"), std::string::npos);
    EXPECT_NE(json.find("\"data\""), std::string::npos);
    EXPECT_EQ(json.back(), '\n');
}

TEST(ProtocolTest, SerializeErrorResponse) {
    Response resp;
    resp.id = "req_002";
    resp.ok = false;
    resp.error = ErrorCode::TdpOutOfRange;

    std::string json = serialize_response(resp);

    EXPECT_NE(json.find("\"ok\":false"), std::string::npos);
    EXPECT_NE(json.find("\"error\":\"tdp_out_of_range\""), std::string::npos);
}

TEST(ProtocolTest, SerializeEvent) {
    Event evt;
    evt.event = "button_press";
    evt.data = R"({"count": 5})";

    std::string json = serialize_event(evt);

    EXPECT_NE(json.find("\"type\":\"event\""), std::string::npos);
    EXPECT_NE(json.find("\"event\":\"button_press\""), std::string::npos);
    EXPECT_EQ(json.back(), '\n');
}

TEST(ProtocolTest, RequestIdEchoed) {
    std::string json = R"({"type": "command", "method": "ping", "id": "test_id_123"})";
    auto cmd = parse_command(json);

    ASSERT_TRUE(cmd.has_value());

    Response resp;
    resp.id = cmd->id;
    resp.ok = true;
    resp.data = "{}";

    std::string response_json = serialize_response(resp);
    EXPECT_NE(response_json.find("test_id_123"), std::string::npos);
}

TEST(ProtocolTest, ErrorCodeToString) {
    EXPECT_EQ(error_code_to_string(ErrorCode::TdpOutOfRange), "tdp_out_of_range");
    EXPECT_EQ(error_code_to_string(ErrorCode::UnknownCommand), "unknown_command");
    EXPECT_EQ(error_code_to_string(ErrorCode::ParseError), "parse_error");
    EXPECT_EQ(error_code_to_string(ErrorCode::PersistDisabled), "persist_disabled");
}

// ===== Config Tests =====

class ConfigTest : public ::testing::Test {
protected:
    void SetUp() override {
        test_dir = fs::temp_directory_path() / "xmax_test_config";
        fs::create_directories(test_dir);
        config_path = test_dir / "config.json";
    }

    void TearDown() override {
        fs::remove_all(test_dir);
    }

    fs::path test_dir;
    fs::path config_path;
};

TEST_F(ConfigTest, LoadMissingFile) {
    Config config = load_config(config_path);
    EXPECT_EQ(config.language, "auto");
    EXPECT_EQ(config.theme, "system");
    EXPECT_FALSE(config.persist);
    EXPECT_EQ(config.charge_limit_pct, 100);
    EXPECT_TRUE(fs::exists(config_path));  // File should be created
}

TEST_F(ConfigTest, LoadValidConfig) {
    // Create valid config file
    std::ofstream file(config_path);
    file << R"({
        "language": "en",
        "theme": "dark",
        "persist": true,
        "charge_limit_pct": 85,
        "auto_start": true
    })";
    file.close();

    Config config = load_config(config_path);
    EXPECT_EQ(config.language, "en");
    EXPECT_EQ(config.theme, "dark");
    EXPECT_TRUE(config.persist);
    EXPECT_EQ(config.charge_limit_pct, 85);
    EXPECT_TRUE(config.auto_start);
}

TEST_F(ConfigTest, LoadCorruptedJson) {
    std::ofstream file(config_path);
    file << "{invalid json";
    file.close();

    Config config = load_config(config_path);
    EXPECT_EQ(config.language, "auto");  // Should use defaults
    EXPECT_EQ(config.charge_limit_pct, 100);
}

TEST_F(ConfigTest, ValidateConfig) {
    Config config;
    config.language = "invalid";
    config.theme = "invalid";
    config.charge_limit_pct = 150;

    bool modified = validate_config(config);
    EXPECT_TRUE(modified);
    EXPECT_EQ(config.language, "auto");
    EXPECT_EQ(config.theme, "system");
    EXPECT_EQ(config.charge_limit_pct, 100);
}

TEST_F(ConfigTest, ConfigRoundTrip) {
    Config original;
    original.language = "zh";
    original.theme = "light";
    original.persist = true;
    original.charge_limit_pct = 90;

    EXPECT_TRUE(save_config(config_path, original));

    Config loaded = load_config(config_path);
    EXPECT_EQ(loaded.language, original.language);
    EXPECT_EQ(loaded.theme, original.theme);
    EXPECT_EQ(loaded.persist, original.persist);
    EXPECT_EQ(loaded.charge_limit_pct, original.charge_limit_pct);
}

// ===== Profile Tests =====

class ProfileTest : public ::testing::Test {
protected:
    void SetUp() override {
        test_dir = fs::temp_directory_path() / "xmax_test_profiles";
        fs::create_directories(test_dir);
        profiles_path = test_dir / "profiles.json";
    }

    void TearDown() override {
        fs::remove_all(test_dir);
    }

    fs::path test_dir;
    fs::path profiles_path;
};

TEST_F(ProfileTest, GenerateSlug) {
    std::map<std::string, bool> existing;
    EXPECT_EQ(generate_slug("Gaming Profile", existing), "gaming-profile");
    EXPECT_EQ(generate_slug("Night Mode", existing), "night-mode");
    EXPECT_EQ(generate_slug("Max  Performance!", existing), "max-performance");
}

TEST_F(ProfileTest, SlugCollision) {
    std::map<std::string, bool> existing;
    std::string slug1 = generate_slug("Gaming", existing);
    existing[slug1] = true;

    std::string slug2 = generate_slug("Gaming", existing);
    EXPECT_EQ(slug1, "gaming");
    EXPECT_EQ(slug2, "gaming-2");

    existing[slug2] = true;
    std::string slug3 = generate_slug("Gaming", existing);
    EXPECT_EQ(slug3, "gaming-3");
}

TEST_F(ProfileTest, FanCurveValidation) {
    FanCurve curve;
    curve.id = "test";
    curve.name = "Test";

    // Less than 2 points
    curve.points = {{40, 20}};
    std::string error;
    EXPECT_FALSE(validate_fan_curve(curve, error));

    // More than 10 points
    curve.points.clear();
    for (int i = 0; i < 11; i++) {
        curve.points.push_back({40 + i * 5, 20 + i * 5});
    }
    EXPECT_FALSE(validate_fan_curve(curve, error));

    // Unsorted
    curve.points = {{60, 40}, {40, 20}};
    EXPECT_FALSE(validate_fan_curve(curve, error));

    // Speed out of range
    curve.points = {{40, 20}, {60, 150}};
    EXPECT_FALSE(validate_fan_curve(curve, error));

    // Valid
    curve.points = {{40, 20}, {60, 40}, {80, 80}};
    EXPECT_TRUE(validate_fan_curve(curve, error));
}

TEST_F(ProfileTest, FanCurveInterpolation) {
    FanCurve curve;
    curve.points = {{40, 20}, {60, 40}, {80, 80}};

    EXPECT_EQ(interpolate_fan_speed(curve, 30), 20);  // Below first
    EXPECT_EQ(interpolate_fan_speed(curve, 50), 30);  // Interpolated
    EXPECT_EQ(interpolate_fan_speed(curve, 70), 60);  // Interpolated
    EXPECT_EQ(interpolate_fan_speed(curve, 90), 80);  // Above last
}

TEST_F(ProfileTest, SaveAndLoadFanCurve) {
    ProfileStorage storage;
    FanCurve curve;
    curve.name = "Quiet";
    curve.points = {{40, 15}, {60, 25}, {75, 35}, {85, 40}};

    auto error = save_fan_curve(storage, curve);
    EXPECT_FALSE(error.has_value());
    // Fan curves include builtin curves
    auto user_curves = storage.fan_curves.size() - storage.builtin_curves.size();
    EXPECT_EQ(user_curves, 1);
    EXPECT_EQ(storage.fan_curves.count("quiet"), 1);

    EXPECT_TRUE(save_profiles(profiles_path, storage));

    ProfileStorage loaded = load_profiles(profiles_path);
    auto loaded_user_curves = loaded.fan_curves.size() - loaded.builtin_curves.size();
    EXPECT_EQ(loaded_user_curves, 1);
    EXPECT_EQ(loaded.fan_curves["quiet"].name, "Quiet");
    EXPECT_EQ(loaded.fan_curves["quiet"].points.size(), 4);
}

TEST_F(ProfileTest, DeleteFanCurveConstraint) {
    ProfileStorage storage;

    // Add fan curve
    FanCurve curve;
    curve.name = "Aggressive";
    curve.points = {{40, 30}, {80, 100}};
    save_fan_curve(storage, curve);

    // Add profile referencing it
    Profile profile;
    profile.name = "Gaming";
    profile.stapm_w = 45;
    profile.fast_w = 50;
    profile.slow_w = 45;
    profile.fan_curve = "aggressive";
    save_profile(storage, profile);

    // Try to delete fan curve
    auto error = delete_fan_curve(storage, "aggressive");
    EXPECT_TRUE(error.has_value());
    EXPECT_EQ(storage.fan_curves.size(), 1);  // Not deleted
}

TEST_F(ProfileTest, SaveAndLoadProfile) {
    ProfileStorage storage;

    // Add a fan curve first (mandatory for fixed profiles)
    FanCurve curve;
    curve.name = "Default";
    curve.points = {{40, 20}, {80, 80}};
    save_fan_curve(storage, curve);

    Profile profile;
    profile.name = "Performance";
    profile.stapm_w = 55;
    profile.fast_w = 65;
    profile.slow_w = 55;
    profile.fan_curve = "default";

    auto error = save_profile(storage, profile);
    EXPECT_FALSE(error.has_value());
    EXPECT_EQ(storage.profiles.size(), 1);

    EXPECT_TRUE(save_profiles(profiles_path, storage));

    ProfileStorage loaded = load_profiles(profiles_path);
    EXPECT_EQ(loaded.profiles.size(), 1);
    EXPECT_EQ(loaded.profiles["performance"].name, "Performance");
    EXPECT_EQ(loaded.profiles["performance"].stapm_w, 55);
}

TEST_F(ProfileTest, ProfileTdpValidation) {
    ProfileStorage storage;
    Profile profile;
    profile.name = "Invalid";
    profile.stapm_w = 150;  // Out of range
    profile.fast_w = 50;
    profile.slow_w = 45;

    auto error = save_profile(storage, profile);
    EXPECT_TRUE(error.has_value());
    EXPECT_EQ(storage.profiles.size(), 0);
}

TEST_F(ProfileTest, FirstProfileWithPowerStateBecomesDefault) {
    ProfileStorage storage;
    FanCurve curve;
    curve.name = "Default";
    curve.points = {{40, 20}, {80, 80}};
    save_fan_curve(storage, curve);

    Profile profile;
    profile.name = "Battery Profile";
    profile.power_state = PowerState::Source::Battery;
    profile.stapm_w = 25;
    profile.fast_w = 30;
    profile.slow_w = 25;
    profile.fan_curve = "default";

    auto error = save_profile(storage, profile);
    EXPECT_FALSE(error.has_value());
    EXPECT_TRUE(storage.profiles["battery-profile"].is_default);
}

TEST_F(ProfileTest, MultipleProfilesPerPowerState) {
    ProfileStorage storage;
    FanCurve curve;
    curve.name = "Default";
    curve.points = {{40, 20}, {80, 80}};
    save_fan_curve(storage, curve);

    // First profile auto-becomes default
    Profile p1;
    p1.name = "Battery Quiet";
    p1.power_state = PowerState::Source::Battery;
    p1.stapm_w = 15;
    p1.fast_w = 20;
    p1.slow_w = 15;
    p1.fan_curve = "default";
    save_profile(storage, p1);
    EXPECT_TRUE(storage.profiles["battery-quiet"].is_default);

    // Second profile for same state, is_default stays false
    Profile p2;
    p2.name = "Battery Performance";
    p2.power_state = PowerState::Source::Battery;
    p2.stapm_w = 25;
    p2.fast_w = 30;
    p2.slow_w = 25;
    p2.fan_curve = "default";
    save_profile(storage, p2);
    EXPECT_FALSE(storage.profiles["battery-performance"].is_default);
    EXPECT_TRUE(storage.profiles["battery-quiet"].is_default);  // Still default
}

TEST_F(ProfileTest, ExplicitlySetDefaultClearsOther) {
    ProfileStorage storage;
    FanCurve curve;
    curve.name = "Default";
    curve.points = {{40, 20}, {80, 80}};
    save_fan_curve(storage, curve);

    // First profile
    Profile p1;
    p1.name = "First";
    p1.power_state = PowerState::Source::DcIn;
    p1.stapm_w = 45;
    p1.fast_w = 50;
    p1.slow_w = 45;
    p1.fan_curve = "default";
    save_profile(storage, p1);
    EXPECT_TRUE(storage.profiles["first"].is_default);

    // Second profile, explicitly set as default
    Profile p2;
    p2.name = "Second";
    p2.power_state = PowerState::Source::DcIn;
    p2.is_default = true;
    p2.stapm_w = 55;
    p2.fast_w = 60;
    p2.slow_w = 55;
    p2.fan_curve = "default";
    save_profile(storage, p2);
    EXPECT_TRUE(storage.profiles["second"].is_default);
    EXPECT_FALSE(storage.profiles["first"].is_default);  // Cleared
}

TEST_F(ProfileTest, DeleteDefaultPromotesAnother) {
    ProfileStorage storage;
    FanCurve curve;
    curve.name = "Default";
    curve.points = {{40, 20}, {80, 80}};
    save_fan_curve(storage, curve);

    // First profile (becomes default)
    Profile p1;
    p1.name = "First";
    p1.power_state = PowerState::Source::Battery;
    p1.stapm_w = 15;
    p1.fast_w = 20;
    p1.slow_w = 15;
    p1.fan_curve = "default";
    save_profile(storage, p1);
    EXPECT_TRUE(storage.profiles["first"].is_default);

    // Second profile
    Profile p2;
    p2.name = "Second";
    p2.power_state = PowerState::Source::Battery;
    p2.stapm_w = 25;
    p2.fast_w = 30;
    p2.slow_w = 25;
    p2.fan_curve = "default";
    save_profile(storage, p2);
    EXPECT_FALSE(storage.profiles["second"].is_default);

    // Delete the default → second should be promoted
    auto err = delete_profile(storage, "first");
    EXPECT_FALSE(err.has_value());
    EXPECT_EQ(storage.profiles.size(), 1);
    EXPECT_TRUE(storage.profiles["second"].is_default);
}

TEST_F(ProfileTest, NoPowerStateClearsIsDefault) {
    ProfileStorage storage;
    FanCurve curve;
    curve.name = "Default";
    curve.points = {{40, 20}, {80, 80}};
    save_fan_curve(storage, curve);

    Profile profile;
    profile.name = "Standalone";
    profile.is_default = true;  // Set by user, but no power_state
    profile.stapm_w = 45;
    profile.fast_w = 50;
    profile.slow_w = 45;
    profile.fan_curve = "default";
    save_profile(storage, profile);

    // No power_state → is_default cleared
    EXPECT_FALSE(storage.profiles["standalone"].is_default);
}

TEST_F(ProfileTest, IsDefaultRoundTripJson) {
    ProfileStorage storage;
    FanCurve curve;
    curve.name = "Default";
    curve.points = {{40, 20}, {80, 80}};
    save_fan_curve(storage, curve);

    Profile p;
    p.name = "DC Default";
    p.power_state = PowerState::Source::DcIn;
    p.stapm_w = 45;
    p.fast_w = 50;
    p.slow_w = 45;
    p.fan_curve = "default";
    save_profile(storage, p);
    EXPECT_TRUE(storage.profiles["dc-default"].is_default);

    // Save to file and reload
    EXPECT_TRUE(save_profiles(profiles_path, storage));
    ProfileStorage loaded = load_profiles(profiles_path);
    EXPECT_TRUE(loaded.profiles["dc-default"].is_default);
}

// ===== FanController Tests =====

class FanControllerTest : public ::testing::Test {
protected:
    void SetUp() override {
        controller_ = std::make_unique<FanController>(platform_);

        // Set up a test fan curve
        test_curve_.id = "test-curve";
        test_curve_.name = "Test Curve";
        test_curve_.points = {{40, 20}, {60, 40}, {80, 80}};
    }

    MockPlatform platform_;
    std::unique_ptr<FanController> controller_;
    FanCurve test_curve_;
};

TEST_F(FanControllerTest, InitialModeIsAuto) {
    EXPECT_EQ(controller_->mode(), FanState::Mode::Auto);
}

TEST_F(FanControllerTest, SetModeToCurve) {
    auto result = controller_->set_mode(FanState::Mode::Curve);
    ASSERT_TRUE(result.has_value());
    EXPECT_EQ(controller_->mode(), FanState::Mode::Curve);

    // Verify EC register was written (0x044A = mode register)
    EXPECT_EQ(platform_.ec_registers[0x044A], 0x01);  // 1 = manual/curve mode
}

TEST_F(FanControllerTest, SetModeToAuto) {
    // First set to curve
    auto result1 = controller_->set_mode(FanState::Mode::Curve);
    ASSERT_TRUE(result1.has_value());
    EXPECT_EQ(platform_.ec_registers[0x044A], 0x01);

    // Then back to auto
    auto result = controller_->set_mode(FanState::Mode::Auto);
    ASSERT_TRUE(result.has_value());
    EXPECT_EQ(controller_->mode(), FanState::Mode::Auto);
    EXPECT_EQ(platform_.ec_registers[0x044A], 0x00);  // 0 = auto mode
}

TEST_F(FanControllerTest, SetCurve) {
    controller_->set_curve(test_curve_);
    auto curve = controller_->active_curve();
    ASSERT_TRUE(curve.has_value());
    EXPECT_EQ(curve->id, "test-curve");
    EXPECT_EQ(curve->points.size(), 3);
}

TEST_F(FanControllerTest, TickInAutoModeDoesNothing) {
    // In Auto mode, tick should not write to EC
    platform_.ec_registers.clear();
    controller_->tick(70, 60);

    // Duty register (0x044B) should not be written
    EXPECT_EQ(platform_.ec_registers.count(0x044B), 0);
}

TEST_F(FanControllerTest, TickInCurveModeWithoutCurveDoesNothing) {
    // Set to curve mode but don't set a curve
    auto result = controller_->set_mode(FanState::Mode::Curve);
    ASSERT_TRUE(result.has_value());
    platform_.ec_registers.clear();

    controller_->tick(70, 60);

    // Duty register should not be written (no curve set)
    EXPECT_EQ(platform_.ec_registers.count(0x044B), 0);
}

TEST_F(FanControllerTest, TickInterpolatesBelowFirstPoint) {
    auto result = controller_->set_mode(FanState::Mode::Curve);
    ASSERT_TRUE(result.has_value());
    controller_->set_curve(test_curve_);

    // Temp 30°C is below first point (40°C, 20%)
    controller_->tick(30, 25);

    // Should use first point's speed: 20%
    EXPECT_DOUBLE_EQ(controller_->last_speed_pct(), 20.0);

    // Verify duty cycle was written: 20% → ~51 (20 * 255 / 100 = 51)
    uint8_t expected_duty = static_cast<uint8_t>(std::round(20.0 * 255.0 / 100.0));
    EXPECT_EQ(platform_.ec_registers[0x044B], expected_duty);
}

TEST_F(FanControllerTest, TickInterpolatesBetweenPoints) {
    auto result = controller_->set_mode(FanState::Mode::Curve);
    ASSERT_TRUE(result.has_value());
    controller_->set_curve(test_curve_);

    // Temp 50°C is between (40, 20) and (60, 40) → should interpolate to 30%
    controller_->tick(50, 45);

    EXPECT_DOUBLE_EQ(controller_->last_speed_pct(), 30.0);

    uint8_t expected_duty = static_cast<uint8_t>(std::round(30.0 * 255.0 / 100.0));
    EXPECT_EQ(platform_.ec_registers[0x044B], expected_duty);
}

TEST_F(FanControllerTest, TickInterpolatesAboveLastPoint) {
    auto result = controller_->set_mode(FanState::Mode::Curve);
    ASSERT_TRUE(result.has_value());
    controller_->set_curve(test_curve_);

    // Temp 90°C is above last point (80°C, 80%)
    controller_->tick(90, 85);

    // Should use last point's speed: 80%
    EXPECT_DOUBLE_EQ(controller_->last_speed_pct(), 80.0);

    uint8_t expected_duty = static_cast<uint8_t>(std::round(80.0 * 255.0 / 100.0));
    EXPECT_EQ(platform_.ec_registers[0x044B], expected_duty);
}

TEST_F(FanControllerTest, TickUsesMaxOfCpuAndGpuTemp) {
    auto result = controller_->set_mode(FanState::Mode::Curve);
    ASSERT_TRUE(result.has_value());
    controller_->set_curve(test_curve_);

    // CPU 70°C, GPU 50°C → max is 70°C → interpolate between (60, 40) and (80, 80) → 60%
    controller_->tick(70, 50);
    EXPECT_DOUBLE_EQ(controller_->last_speed_pct(), 60.0);

    // CPU 45°C, GPU 75°C → max is 75°C → interpolate between (60, 40) and (80, 80) → 70%
    controller_->tick(45, 75);
    EXPECT_DOUBLE_EQ(controller_->last_speed_pct(), 70.0);
}

TEST_F(FanControllerTest, TickWithOnlyCpuTemp) {
    auto result = controller_->set_mode(FanState::Mode::Curve);
    ASSERT_TRUE(result.has_value());
    controller_->set_curve(test_curve_);

    // Only CPU temp available
    controller_->tick(50, std::nullopt);

    EXPECT_DOUBLE_EQ(controller_->last_speed_pct(), 30.0);
}

TEST_F(FanControllerTest, TickWithOnlyGpuTemp) {
    auto result = controller_->set_mode(FanState::Mode::Curve);
    ASSERT_TRUE(result.has_value());
    controller_->set_curve(test_curve_);

    // Only GPU temp available
    controller_->tick(std::nullopt, 70);

    EXPECT_DOUBLE_EQ(controller_->last_speed_pct(), 60.0);
}

TEST_F(FanControllerTest, TickWithNoTempDataDoesNothing) {
    auto result = controller_->set_mode(FanState::Mode::Curve);
    ASSERT_TRUE(result.has_value());
    controller_->set_curve(test_curve_);
    platform_.ec_registers.clear();

    // No temperature data
    controller_->tick(std::nullopt, std::nullopt);

    // Duty register should not be written
    EXPECT_EQ(platform_.ec_registers.count(0x044B), 0);
}

TEST_F(FanControllerTest, ReadStateFromEC) {
    // Set up EC registers
    platform_.ec_registers[0x044A] = 0x01;  // Curve mode
    platform_.ec_registers[0x044B] = 128;   // ~50% duty
    platform_.ec_registers[0x0476] = 0x0C;  // RPM high byte
    platform_.ec_registers[0x0477] = 0x80;  // RPM low byte → 0x0C80 = 3200

    FanState state = controller_->read_state();

    EXPECT_EQ(state.mode, FanState::Mode::Curve);
    EXPECT_NEAR(state.speed_pct, 50.2, 0.5);  // 128/255 * 100 ≈ 50.2
    EXPECT_EQ(state.rpm, 3200);
}

TEST_F(FanControllerTest, ReadRpm) {
    platform_.ec_registers[0x0476] = 0x0F;  // High byte
    platform_.ec_registers[0x0477] = 0xA0;  // Low byte → 0x0FA0 = 4000

    auto rpm = controller_->read_rpm();
    ASSERT_TRUE(rpm.has_value());
    EXPECT_EQ(rpm.value(), 4000);
}

// ===== TdpController Tests =====

class TdpControllerTest : public ::testing::Test {
protected:
    void SetUp() override {
        controller_ = std::make_unique<TdpController>(platform_);
    }

    MockPlatform platform_;
    std::unique_ptr<TdpController> controller_;
};

TEST_F(TdpControllerTest, ValidateTdpInRange) {
    EXPECT_TRUE(TdpController::validate_tdp(6));
    EXPECT_TRUE(TdpController::validate_tdp(45));
    EXPECT_TRUE(TdpController::validate_tdp(120));
}

TEST_F(TdpControllerTest, ValidateTdpOutOfRange) {
    EXPECT_FALSE(TdpController::validate_tdp(0));
    EXPECT_FALSE(TdpController::validate_tdp(5));
    EXPECT_FALSE(TdpController::validate_tdp(121));
    EXPECT_FALSE(TdpController::validate_tdp(200));
}

TEST_F(TdpControllerTest, ReadTdp) {
    // Set up SMU responses
    platform_.smu_responses[0x00] = 45;  // STAPM
    // All read messages use 0x00 placeholder, so last write wins
    // In real implementation, each would have unique message IDs

    auto state = controller_->read_tdp();
    ASSERT_TRUE(state.has_value());

    // Verify SMU was called 3 times (STAPM, Fast, Slow)
    EXPECT_EQ(platform_.smu_calls.size(), 3);

    // All values should be populated (from the same response in mock)
    EXPECT_TRUE(state->stapm_w.has_value());
    EXPECT_TRUE(state->fast_w.has_value());
    EXPECT_TRUE(state->slow_w.has_value());
}

TEST_F(TdpControllerTest, ReadTdpHandlesSmuFailure) {
    platform_.smu_should_fail = true;

    auto state = controller_->read_tdp();
    ASSERT_TRUE(state.has_value());  // Returns partial state

    // Fields should not be populated when SMU fails
    EXPECT_FALSE(state->stapm_w.has_value());
    EXPECT_FALSE(state->fast_w.has_value());
    EXPECT_FALSE(state->slow_w.has_value());
}

TEST_F(TdpControllerTest, WriteTdpSuccess) {
    auto result = controller_->write_tdp(45, 50, 45);
    ASSERT_TRUE(result.has_value());

    // Verify SMU was called 3 times (write STAPM, Fast, Slow)
    EXPECT_EQ(platform_.smu_calls.size(), 3);

    // Verify arguments
    EXPECT_EQ(platform_.smu_calls[0].arg, 45);  // STAPM
    EXPECT_EQ(platform_.smu_calls[1].arg, 50);  // Fast
    EXPECT_EQ(platform_.smu_calls[2].arg, 45);  // Slow

    // Verify last state was updated
    auto last = controller_->last_state();
    EXPECT_EQ(last.stapm_w.value(), 45);
    EXPECT_EQ(last.fast_w.value(), 50);
    EXPECT_EQ(last.slow_w.value(), 45);
}

TEST_F(TdpControllerTest, WriteTdpValidatesStapm) {
    auto result = controller_->write_tdp(5, 50, 45);  // STAPM out of range
    ASSERT_FALSE(result.has_value());
    EXPECT_EQ(result.error(), ErrorCode::TdpOutOfRange);

    // SMU should not be called
    EXPECT_EQ(platform_.smu_calls.size(), 0);
}

TEST_F(TdpControllerTest, WriteTdpValidatesFast) {
    auto result = controller_->write_tdp(45, 150, 45);  // Fast out of range
    ASSERT_FALSE(result.has_value());
    EXPECT_EQ(result.error(), ErrorCode::TdpOutOfRange);

    // SMU should not be called
    EXPECT_EQ(platform_.smu_calls.size(), 0);
}

TEST_F(TdpControllerTest, WriteTdpValidatesSlow) {
    auto result = controller_->write_tdp(45, 50, 0);  // Slow out of range
    ASSERT_FALSE(result.has_value());
    EXPECT_EQ(result.error(), ErrorCode::TdpOutOfRange);

    // SMU should not be called
    EXPECT_EQ(platform_.smu_calls.size(), 0);
}

TEST_F(TdpControllerTest, WriteTdpHandlesSmuFailure) {
    platform_.smu_should_fail = true;

    auto result = controller_->write_tdp(45, 50, 45);
    ASSERT_FALSE(result.has_value());
    EXPECT_EQ(result.error(), ErrorCode::HardwareBusy);
}

TEST_F(TdpControllerTest, LastStateInitiallyEmpty) {
    auto state = controller_->last_state();
    EXPECT_FALSE(state.stapm_w.has_value());
    EXPECT_FALSE(state.fast_w.has_value());
    EXPECT_FALSE(state.slow_w.has_value());
}

TEST_F(TdpControllerTest, LastStateUpdatedAfterWrite) {
    auto result = controller_->write_tdp(55, 65, 55);
    ASSERT_TRUE(result.has_value());

    auto state = controller_->last_state();
    EXPECT_EQ(state.stapm_w.value(), 55);
    EXPECT_EQ(state.fast_w.value(), 65);
    EXPECT_EQ(state.slow_w.value(), 55);
}

// ===== MetricsPoller Tests =====

class MetricsPollerTest : public ::testing::Test {
protected:
    void SetUp() override {
        fan_ctrl_ = std::make_unique<FanController>(platform_);
        tdp_ctrl_ = std::make_unique<TdpController>(platform_);
        poller_ = std::make_unique<MetricsPoller>(platform_, *fan_ctrl_, *tdp_ctrl_);
    }

    void TearDown() override {
        if (poller_ && poller_->is_running()) {
            poller_->stop();
        }
    }

    MockPlatform platform_;
    std::unique_ptr<FanController> fan_ctrl_;
    std::unique_ptr<TdpController> tdp_ctrl_;
    std::unique_ptr<MetricsPoller> poller_;
};

TEST_F(MetricsPollerTest, InitiallyNotRunning) {
    EXPECT_FALSE(poller_->is_running());
}

TEST_F(MetricsPollerTest, StartAndStop) {
    poller_->start();
    EXPECT_TRUE(poller_->is_running());

    poller_->stop();
    EXPECT_FALSE(poller_->is_running());
}

TEST_F(MetricsPollerTest, StartTwiceIsNoOp) {
    poller_->start();
    poller_->start();  // Should not crash or create duplicate threads
    EXPECT_TRUE(poller_->is_running());

    poller_->stop();
}

TEST_F(MetricsPollerTest, StopTwiceIsNoOp) {
    poller_->start();
    poller_->stop();
    poller_->stop();  // Should not crash
    EXPECT_FALSE(poller_->is_running());
}

TEST_F(MetricsPollerTest, GetMetricsReturnsInitialState) {
    Metrics metrics = poller_->get_metrics();

    // Initially, all metrics should be at defaults
    EXPECT_EQ(metrics.cpu.util_pct, 0.0);
    EXPECT_EQ(metrics.gpu.util_pct, 0.0);
    EXPECT_EQ(metrics.ram.used_gb, 0.0);
    EXPECT_EQ(metrics.fan.rpm, 0);
    EXPECT_EQ(metrics.power.mode, PowerState::Source::Unknown);
}

TEST_F(MetricsPollerTest, PollerUpdatesGpuMetrics) {
    // Set up mock GPU telemetry
    GpuTelemetry telemetry;
    telemetry.util_pct = 75.0;
    telemetry.clock_mhz = 1800;
    telemetry.temp_c = 70;
    telemetry.power_w = 50.0;
    telemetry.vram_used_mb = 4096;
    telemetry.vram_total_mb = 8192;
    platform_.gpu_telemetry = telemetry;

    poller_->start();

    // Wait for at least one poll cycle (2000ms) plus some margin
    std::this_thread::sleep_for(std::chrono::milliseconds(2500));

    Metrics metrics = poller_->get_metrics();

    // GPU metrics should be updated
    EXPECT_DOUBLE_EQ(metrics.gpu.util_pct, 75.0);
    EXPECT_EQ(metrics.gpu.clock_mhz, 1800);
    EXPECT_EQ(metrics.gpu.temp_c.value(), 70);
    EXPECT_DOUBLE_EQ(metrics.gpu.power_w.value(), 50.0);
    EXPECT_EQ(metrics.gpu.vram_used_mb.value(), 4096);
    EXPECT_EQ(metrics.gpu.vram_total_mb.value(), 8192);

    poller_->stop();
}

TEST_F(MetricsPollerTest, PollerHandlesGpuFailure) {
    platform_.gpu_should_fail = true;

    poller_->start();
    std::this_thread::sleep_for(std::chrono::milliseconds(2500));

    Metrics metrics = poller_->get_metrics();

    // GPU metrics should be at defaults (failure handled gracefully)
    EXPECT_EQ(metrics.gpu.util_pct, 0.0);
    EXPECT_FALSE(metrics.gpu.temp_c.has_value());

    poller_->stop();
}

TEST_F(MetricsPollerTest, PollerUpdatesFanMetrics) {
    // Set up EC registers for fan
    platform_.ec_registers[0x044A] = 0x01;  // Curve mode
    platform_.ec_registers[0x044B] = 128;   // ~50% duty
    platform_.ec_registers[0x0476] = 0x0C;  // RPM high
    platform_.ec_registers[0x0477] = 0x80;  // RPM low → 3200

    poller_->start();
    std::this_thread::sleep_for(std::chrono::milliseconds(2500));

    Metrics metrics = poller_->get_metrics();

    // Fan metrics should be updated
    EXPECT_EQ(metrics.fan.mode, FanState::Mode::Curve);
    EXPECT_NEAR(metrics.fan.speed_pct, 50.2, 0.5);
    EXPECT_EQ(metrics.fan.rpm, 3200);

    poller_->stop();
}

TEST_F(MetricsPollerTest, PollerUpdatesPowerMetrics) {
    // Set up EC registers for power state
    platform_.ec_registers[0x04FE] = 4;   // DC-In
    platform_.ec_registers[0x04A3] = 85;  // 85% charge limit

    poller_->start();
    std::this_thread::sleep_for(std::chrono::milliseconds(2500));

    Metrics metrics = poller_->get_metrics();

    // Power metrics should be updated
    EXPECT_EQ(metrics.power.mode, PowerState::Source::DcIn);
    EXPECT_EQ(metrics.power.label, "DC-In (dedicated charger)");
    EXPECT_EQ(metrics.power.charge_limit_pct.value(), 85);

    poller_->stop();
}

TEST_F(MetricsPollerTest, PollerUpdatesTimestamp) {
    poller_->start();
    std::this_thread::sleep_for(std::chrono::milliseconds(2500));

    Metrics metrics = poller_->get_metrics();

    // Timestamp should be set (non-zero)
    EXPECT_GT(metrics.ts, 0);

    poller_->stop();
}

TEST_F(MetricsPollerTest, DestructorStopsPoller) {
    poller_->start();
    EXPECT_TRUE(poller_->is_running());

    // Let the poller be destroyed
    poller_.reset();

    // If we get here without hanging, the destructor properly stopped the thread
    SUCCEED();
}

// ===== PowerController Tests =====

class PowerControllerTest : public ::testing::Test {
protected:
    void SetUp() override {
        controller_ = std::make_unique<PowerController>(platform_);
    }

    MockPlatform platform_;
    std::unique_ptr<PowerController> controller_;
};

TEST_F(PowerControllerTest, ValidateChargeLimitInRange) {
    EXPECT_TRUE(PowerController::validate_charge_limit(75));
    EXPECT_TRUE(PowerController::validate_charge_limit(85));
    EXPECT_TRUE(PowerController::validate_charge_limit(100));
}

TEST_F(PowerControllerTest, ValidateChargeLimitOutOfRange) {
    EXPECT_FALSE(PowerController::validate_charge_limit(0));
    EXPECT_FALSE(PowerController::validate_charge_limit(74));
    EXPECT_FALSE(PowerController::validate_charge_limit(101));
    EXPECT_FALSE(PowerController::validate_charge_limit(255));
}

TEST_F(PowerControllerTest, ReadPowerStateBattery) {
    platform_.ec_registers[0x04FE] = 1;  // Battery

    auto state = controller_->read_power_state();
    EXPECT_EQ(state, PowerState::Source::Battery);
}

TEST_F(PowerControllerTest, ReadPowerStateUsbCSlow) {
    platform_.ec_registers[0x04FE] = 8;  // USB-C slow

    auto state = controller_->read_power_state();
    EXPECT_EQ(state, PowerState::Source::UsbCSlow);
}

TEST_F(PowerControllerTest, ReadPowerStateUsbCFast) {
    platform_.ec_registers[0x04FE] = 2;  // USB-C fast

    auto state = controller_->read_power_state();
    EXPECT_EQ(state, PowerState::Source::UsbCFast);
}

TEST_F(PowerControllerTest, ReadPowerStateDcIn) {
    platform_.ec_registers[0x04FE] = 4;  // DC-In

    auto state = controller_->read_power_state();
    EXPECT_EQ(state, PowerState::Source::DcIn);
}

TEST_F(PowerControllerTest, ReadPowerStateUnknown) {
    platform_.ec_registers[0x04FE] = 99;  // Unknown value

    auto state = controller_->read_power_state();
    EXPECT_EQ(state, PowerState::Source::Unknown);
}

TEST_F(PowerControllerTest, CurrentStateInitiallyUnknown) {
    EXPECT_EQ(controller_->current_state(), PowerState::Source::Unknown);
}

TEST_F(PowerControllerTest, UpdatePowerStateNoChange) {
    platform_.ec_registers[0x04FE] = 4;  // DC-In

    // First update
    bool changed = controller_->update_power_state();
    EXPECT_TRUE(changed);
    EXPECT_EQ(controller_->current_state(), PowerState::Source::DcIn);

    // Second update with same value
    changed = controller_->update_power_state();
    EXPECT_FALSE(changed);
}

TEST_F(PowerControllerTest, UpdatePowerStateDetectsChange) {
    platform_.ec_registers[0x04FE] = 4;  // DC-In

    bool changed = controller_->update_power_state();
    EXPECT_TRUE(changed);
    EXPECT_EQ(controller_->current_state(), PowerState::Source::DcIn);

    // Change to battery
    platform_.ec_registers[0x04FE] = 1;
    changed = controller_->update_power_state();
    EXPECT_TRUE(changed);
    EXPECT_EQ(controller_->current_state(), PowerState::Source::Battery);
}

TEST_F(PowerControllerTest, StateChangeCallbackCalled) {
    bool callback_called = false;
    PowerState::Source new_state = PowerState::Source::Unknown;
    PowerState::Source old_state = PowerState::Source::Unknown;

    controller_->on_state_change([&](PowerState::Source ns, PowerState::Source os) {
        callback_called = true;
        new_state = ns;
        old_state = os;
    });

    platform_.ec_registers[0x04FE] = 4;  // DC-In
    controller_->update_power_state();

    EXPECT_TRUE(callback_called);
    EXPECT_EQ(new_state, PowerState::Source::DcIn);
    EXPECT_EQ(old_state, PowerState::Source::Unknown);
}

TEST_F(PowerControllerTest, StateChangeCallbackNotCalledOnNoChange) {
    platform_.ec_registers[0x04FE] = 4;  // DC-In
    controller_->update_power_state();

    bool callback_called = false;
    controller_->on_state_change([&](PowerState::Source, PowerState::Source) {
        callback_called = true;
    });

    // Update with same value
    controller_->update_power_state();

    EXPECT_FALSE(callback_called);
}

TEST_F(PowerControllerTest, ReadChargeLimit) {
    platform_.ec_registers[0x04A3] = 85;

    auto result = controller_->read_charge_limit();
    ASSERT_TRUE(result.has_value());
    EXPECT_EQ(result.value(), 85);

    // Last charge limit should be updated
    auto last = controller_->last_charge_limit();
    ASSERT_TRUE(last.has_value());
    EXPECT_EQ(last.value(), 85);
}

TEST_F(PowerControllerTest, WriteChargeLimitSuccess) {
    auto result = controller_->write_charge_limit(90);
    ASSERT_TRUE(result.has_value());

    // Last charge limit should be updated
    auto last = controller_->last_charge_limit();
    ASSERT_TRUE(last.has_value());
    EXPECT_EQ(last.value(), 90);
}

TEST_F(PowerControllerTest, WriteChargeLimitValidatesRange) {
    auto result1 = controller_->write_charge_limit(50);  // Too low
    ASSERT_FALSE(result1.has_value());
    EXPECT_EQ(result1.error(), ErrorCode::ChargeLimitInvalid);

    auto result2 = controller_->write_charge_limit(110);  // Too high
    ASSERT_FALSE(result2.has_value());
    EXPECT_EQ(result2.error(), ErrorCode::ChargeLimitInvalid);
}

TEST_F(PowerControllerTest, LastChargeLimitInitiallyEmpty) {
    auto last = controller_->last_charge_limit();
    EXPECT_FALSE(last.has_value());
}

// ===== AdaptiveController Tests =====

class AdaptiveControllerTest : public ::testing::Test {
protected:
    void SetUp() override {
        tdp_ctrl_ = std::make_unique<TdpController>(platform_);
        fan_ctrl_ = std::make_unique<FanController>(platform_);
        adaptive_ = std::make_unique<AdaptiveController>(*tdp_ctrl_, *fan_ctrl_);
    }

    void TearDown() override {
        if (adaptive_) {
            adaptive_->stop();
        }
    }

    MockPlatform platform_;
    std::unique_ptr<TdpController> tdp_ctrl_;
    std::unique_ptr<FanController> fan_ctrl_;
    std::unique_ptr<AdaptiveController> adaptive_;
};

TEST_F(AdaptiveControllerTest, InitiallyInactive) {
    EXPECT_FALSE(adaptive_->is_active());
}

TEST_F(AdaptiveControllerTest, ActivateAndDeactivate) {
    adaptive_->activate(TuningPreset::Default, 85, 55, 100);
    EXPECT_TRUE(adaptive_->is_active());

    adaptive_->deactivate();
    EXPECT_FALSE(adaptive_->is_active());
}

TEST_F(AdaptiveControllerTest, ConfigStored) {
    adaptive_->activate(TuningPreset::Performance, 80, 60, 90);

    auto config = adaptive_->config();
    EXPECT_TRUE(config.active);
    EXPECT_EQ(config.tuning, TuningPreset::Performance);
    EXPECT_EQ(config.target_temp_c, 80);
    EXPECT_EQ(config.tdp_max_w, 60);
    EXPECT_EQ(config.fan_max_pct, 90);
}

TEST_F(AdaptiveControllerTest, EffectiveTdpMaxUsesMinimum) {
    adaptive_->activate(TuningPreset::Default, 85, 55, 100);
    adaptive_->set_power_state_ceiling(45);

    EXPECT_EQ(adaptive_->effective_tdp_max(), 45);  // min(55, 45)
}

TEST_F(AdaptiveControllerTest, EffectiveTdpMaxWithHigherPowerState) {
    adaptive_->activate(TuningPreset::Default, 85, 55, 100);
    adaptive_->set_power_state_ceiling(65);

    EXPECT_EQ(adaptive_->effective_tdp_max(), 55);  // min(55, 65)
}

TEST_F(AdaptiveControllerTest, TickWhenInactiveDoesNothing) {
    // Not activated
    adaptive_->tick(70, 65);

    // No SMU calls should be made
    EXPECT_EQ(platform_.smu_calls.size(), 0);
}

TEST_F(AdaptiveControllerTest, TickAppliesPidControl) {
    adaptive_->activate(TuningPreset::Default, 85, 55, 100);

    // Temp above target → fan should increase
    adaptive_->tick(90, 85);

    // Fan should be set
    EXPECT_EQ(platform_.ec_registers.count(0x044A), 1);  // Fan mode register
    EXPECT_EQ(platform_.ec_registers.count(0x044B), 1);  // Fan duty register

    // TDP should be written
    EXPECT_GT(platform_.smu_calls.size(), 0);
}

TEST_F(AdaptiveControllerTest, CriticalTempTriggersSafetyOverride) {
    adaptive_->activate(TuningPreset::Default, 85, 55, 100);

    bool callback_called = false;
    std::string reason;
    adaptive_->on_adjust([&](int, int, int, int, const std::string& r) {
        callback_called = true;
        reason = r;
    });

    // Critical temperature (95°C)
    adaptive_->tick(95, 90);

    EXPECT_TRUE(callback_called);
    EXPECT_EQ(reason, "critical_temp");

    // TDP should be minimum
    auto last_tdp = adaptive_->last_tdp_w();
    EXPECT_EQ(last_tdp, 6);  // TDP_MIN

    // Fan should be 100%
    auto last_fan = adaptive_->last_fan_pct();
    EXPECT_EQ(last_fan, 100);
}

TEST_F(AdaptiveControllerTest, AsymmetricSmoothingFastRise) {
    adaptive_->activate(TuningPreset::Default, 85, 55, 100);

    // Start at 50°C
    adaptive_->tick(50, 50);
    int smoothed1 = adaptive_->last_smoothed_temp();

    // Jump to 80°C
    adaptive_->tick(80, 80);
    int smoothed2 = adaptive_->last_smoothed_temp();

    // Should rise quickly (alpha_rise = 0.5)
    // Expected: 0.5 * 80 + 0.5 * 50 = 65
    EXPECT_GT(smoothed2, smoothed1);
    EXPECT_NEAR(smoothed2, 65, 5);
}

TEST_F(AdaptiveControllerTest, AsymmetricSmoothingSlowFall) {
    adaptive_->activate(TuningPreset::Default, 85, 55, 100);

    // Start at 80°C
    adaptive_->tick(80, 80);
    int smoothed1 = adaptive_->last_smoothed_temp();

    // Drop to 50°C
    adaptive_->tick(50, 50);
    int smoothed2 = adaptive_->last_smoothed_temp();

    // Should fall slowly (alpha_fall = 0.05)
    // Expected: 0.05 * 50 + 0.95 * 80 = 78.5
    EXPECT_LT(smoothed2, smoothed1);
    EXPECT_NEAR(smoothed2, 78, 3);
}

TEST_F(AdaptiveControllerTest, CallbackCalledOnValueChange) {
    adaptive_->activate(TuningPreset::Default, 85, 55, 100);

    int call_count = 0;
    adaptive_->on_adjust([&](int, int, int, int, const std::string&) {
        call_count++;
    });

    // First tick
    adaptive_->tick(70, 70);
    int count1 = call_count;

    // Second tick with same temp (no change expected)
    adaptive_->tick(70, 70);
    int count2 = call_count;

    // Callback should be called at least once
    EXPECT_GT(count1, 0);
}

TEST_F(AdaptiveControllerTest, DestructorStopsThread) {
    adaptive_->activate(TuningPreset::Default, 85, 55, 100);
    adaptive_->start();
    EXPECT_TRUE(adaptive_->is_active());

    adaptive_.reset();

    // If we get here without hanging, destructor properly stopped the thread
    SUCCEED();
}

// ===== ButtonMonitor Tests =====

class ButtonMonitorTest : public ::testing::Test {
protected:
    void SetUp() override {
        monitor_ = std::make_unique<ButtonMonitor>(platform_);
    }

    void TearDown() override {
        if (monitor_) {
            monitor_->stop();
        }
    }

    MockPlatform platform_;
    std::unique_ptr<ButtonMonitor> monitor_;
};

TEST_F(ButtonMonitorTest, InitiallyNotVisible) {
    EXPECT_FALSE(monitor_->is_visible());
}

TEST_F(ButtonMonitorTest, FirstPollEstablishesBaseline) {
    platform_.ec_registers[0x0230] = 0x00;

    // First poll should not detect a press (baseline)
    bool pressed = monitor_->poll();
    EXPECT_FALSE(pressed);
    EXPECT_FALSE(monitor_->is_visible());
}

TEST_F(ButtonMonitorTest, SameValueNoPress) {
    platform_.ec_registers[0x0230] = 0x00;

    // First poll: baseline
    monitor_->poll();

    // Second poll: same value, no press
    bool pressed = monitor_->poll();
    EXPECT_FALSE(pressed);
    EXPECT_FALSE(monitor_->is_visible());
}

TEST_F(ButtonMonitorTest, ValueChangeDetectedAsPress) {
    platform_.ec_registers[0x0230] = 0x00;

    // First poll: baseline (0x00)
    monitor_->poll();

    // Change register value (simulate button press)
    platform_.ec_registers[0x0230] = 0x06;

    // Second poll: edge detected
    bool pressed = monitor_->poll();
    EXPECT_TRUE(pressed);
    EXPECT_TRUE(monitor_->is_visible());
}

TEST_F(ButtonMonitorTest, MultipleToggles) {
    platform_.ec_registers[0x0230] = 0x00;
    monitor_->poll();  // baseline

    // Press 1: 0x00 → 0x06
    platform_.ec_registers[0x0230] = 0x06;
    EXPECT_TRUE(monitor_->poll());
    EXPECT_TRUE(monitor_->is_visible());

    // Press 2: 0x06 → 0x00
    platform_.ec_registers[0x0230] = 0x00;
    EXPECT_TRUE(monitor_->poll());
    EXPECT_FALSE(monitor_->is_visible());

    // Press 3: 0x00 → 0x06
    platform_.ec_registers[0x0230] = 0x06;
    EXPECT_TRUE(monitor_->poll());
    EXPECT_TRUE(monitor_->is_visible());
}

TEST_F(ButtonMonitorTest, ToggleVisibilityManually) {
    EXPECT_FALSE(monitor_->is_visible());

    monitor_->toggle_visibility();
    EXPECT_TRUE(monitor_->is_visible());

    monitor_->toggle_visibility();
    EXPECT_FALSE(monitor_->is_visible());
}

TEST_F(ButtonMonitorTest, SetVisibleWithoutCallback) {
    bool callback_called = false;
    monitor_->on_visibility_change([&](bool) {
        callback_called = true;
    });

    // set_visible should NOT trigger callback
    monitor_->set_visible(true);
    EXPECT_TRUE(monitor_->is_visible());
    EXPECT_FALSE(callback_called);
}

TEST_F(ButtonMonitorTest, CallbackCalledOnPress) {
    platform_.ec_registers[0x0230] = 0x00;
    monitor_->poll();  // baseline

    bool callback_called = false;
    bool new_visible = false;
    monitor_->on_visibility_change([&](bool visible) {
        callback_called = true;
        new_visible = visible;
    });

    // Press button
    platform_.ec_registers[0x0230] = 0x06;
    monitor_->poll();

    EXPECT_TRUE(callback_called);
    EXPECT_TRUE(new_visible);
}

TEST_F(ButtonMonitorTest, CallbackCalledOnManualToggle) {
    bool callback_called = false;
    bool new_visible = false;
    monitor_->on_visibility_change([&](bool visible) {
        callback_called = true;
        new_visible = visible;
    });

    monitor_->toggle_visibility();

    EXPECT_TRUE(callback_called);
    EXPECT_TRUE(new_visible);
}

TEST_F(ButtonMonitorTest, InitAppFunEnWritesRegister) {
    monitor_->init_app_fun_en();

    // APP_FUN_EN register (0x0231) should be written with 0x01
    auto it = platform_.ec_registers.find(0x0231);
    ASSERT_NE(it, platform_.ec_registers.end());
    EXPECT_EQ(it->second, 0x01);
}

TEST_F(ButtonMonitorTest, StartAndStop) {
    monitor_->start();
    EXPECT_TRUE(monitor_->is_running());

    monitor_->stop();
    EXPECT_FALSE(monitor_->is_running());
}

TEST_F(ButtonMonitorTest, MonitorLoopDetectsPress) {
    platform_.ec_registers[0x0230] = 0x00;

    int press_count = 0;
    monitor_->on_visibility_change([&](bool) {
        press_count++;
    });

    monitor_->start();

    // Wait for baseline poll
    std::this_thread::sleep_for(std::chrono::milliseconds(150));

    // Simulate button press
    platform_.ec_registers[0x0230] = 0x06;
    std::this_thread::sleep_for(std::chrono::milliseconds(150));

    monitor_->stop();

    EXPECT_GE(press_count, 1);
}

TEST_F(ButtonMonitorTest, DestructorStopsThread) {
    monitor_->start();
    EXPECT_TRUE(monitor_->is_running());

    monitor_.reset();

    // If we get here without hanging, destructor properly stopped the thread
    SUCCEED();
}

TEST_F(ButtonMonitorTest, ECReadFailureNoPress) {
    // Don't set EC register -- MockPlatform returns 0 by default, but
    // let's test that poll() handles it gracefully
    platform_.ec_registers[0x0230] = 0x00;
    monitor_->poll();  // baseline

    // Even if EC read returns the same value (0), no press
    bool pressed = monitor_->poll();
    EXPECT_FALSE(pressed);
}

// ===== TransportService Tests =====

class TransportServiceTest : public ::testing::Test {
protected:
    void SetUp() override {
        // Create temp file paths
        auto temp_dir = fs::temp_directory_path() / "xmax_test";
        fs::create_directories(temp_dir);
        config_path_ = temp_dir / "config.json";
        profiles_path_ = temp_dir / "profiles.json";

        // Create controllers
        fan_ = std::make_unique<FanController>(platform_);
        tdp_ = std::make_unique<TdpController>(platform_);
        poller_ = std::make_unique<MetricsPoller>(platform_, *fan_, *tdp_);
        power_ = std::make_unique<PowerController>(platform_);
        adaptive_ = std::make_unique<AdaptiveController>(*tdp_, *fan_);
        button_ = std::make_unique<ButtonMonitor>(platform_);

        // Create transport service
        service_ = std::make_unique<TransportService>(
            platform_, *poller_, *fan_, *tdp_, *power_, *adaptive_, *button_,
            config_, profiles_, config_path_, profiles_path_
        );
    }

    void TearDown() override {
        service_.reset();
        adaptive_.reset();
        poller_.reset();
        fan_.reset();
        tdp_.reset();
        power_.reset();
        button_.reset();

        // Clean up temp files
        auto temp_dir = fs::temp_directory_path() / "xmax_test";
        fs::remove_all(temp_dir);
    }

    // Helper to create a command
    Command make_command(const std::string& method, const std::string& id = "req_001",
                        const std::string& payload = "{}") {
        Command cmd;
        cmd.method = method;
        cmd.id = id;
        cmd.payload = payload;
        return cmd;
    }

    MockPlatform platform_;
    Config config_;
    ProfileStorage profiles_;
    std::filesystem::path config_path_;
    std::filesystem::path profiles_path_;

    std::unique_ptr<FanController> fan_;
    std::unique_ptr<TdpController> tdp_;
    std::unique_ptr<MetricsPoller> poller_;
    std::unique_ptr<PowerController> power_;
    std::unique_ptr<AdaptiveController> adaptive_;
    std::unique_ptr<ButtonMonitor> button_;
    std::unique_ptr<TransportService> service_;
};

TEST_F(TransportServiceTest, PingReturnsEmptyData) {
    auto cmd = make_command("ping", "req_001");
    auto resp = service_->dispatch(cmd);

    EXPECT_TRUE(resp.ok);
    EXPECT_EQ(resp.id, "req_001");
    EXPECT_EQ(resp.data.value(), "{}");
}

TEST_F(TransportServiceTest, UnknownCommandReturnsError) {
    auto cmd = make_command("nonexistent_command");
    auto resp = service_->dispatch(cmd);

    EXPECT_FALSE(resp.ok);
    EXPECT_EQ(resp.error.value(), ErrorCode::UnknownCommand);
}

TEST_F(TransportServiceTest, GetMetricsReturnsData) {
    auto cmd = make_command("get_metrics");
    auto resp = service_->dispatch(cmd);

    EXPECT_TRUE(resp.ok);
    ASSERT_TRUE(resp.data.has_value());

    // Parse the data to verify it's valid JSON with expected fields
    auto data = nlohmann::json::parse(resp.data.value());
    EXPECT_TRUE(data.contains("cpu"));
    EXPECT_TRUE(data.contains("gpu"));
    EXPECT_TRUE(data.contains("ram"));
    EXPECT_TRUE(data.contains("fan"));
    EXPECT_TRUE(data.contains("power"));
    EXPECT_TRUE(data.contains("ts"));
}

TEST_F(TransportServiceTest, SubscribeAndUnsubscribeMetrics) {
    auto sub_cmd = make_command("subscribe_metrics", "req_001", R"({"interval_ms": 1000})");
    auto sub_resp = service_->dispatch(sub_cmd);

    EXPECT_TRUE(sub_resp.ok);

    auto unsub_cmd = make_command("unsubscribe_metrics", "req_002");
    auto unsub_resp = service_->dispatch(unsub_cmd);

    EXPECT_TRUE(unsub_resp.ok);
}

TEST_F(TransportServiceTest, GetFanReturnsState) {
    auto cmd = make_command("get_fan");
    auto resp = service_->dispatch(cmd);

    EXPECT_TRUE(resp.ok);
    ASSERT_TRUE(resp.data.has_value());

    auto data = nlohmann::json::parse(resp.data.value());
    EXPECT_TRUE(data.contains("mode"));
    EXPECT_TRUE(data.contains("speed_pct"));
    EXPECT_TRUE(data.contains("rpm"));
}

TEST_F(TransportServiceTest, SetFanPersistDisabled) {
    config_.persist = false;

    auto cmd = make_command("set_fan", "req_001", R"({"mode": "auto"})");
    auto resp = service_->dispatch(cmd);

    EXPECT_FALSE(resp.ok);
    EXPECT_EQ(resp.error.value(), ErrorCode::PersistDisabled);
}

TEST_F(TransportServiceTest, SetFanPersistEnabled) {
    config_.persist = true;
    config_.session_persist = true;

    auto cmd = make_command("set_fan", "req_001", R"({"mode": "auto"})");
    auto resp = service_->dispatch(cmd);

    EXPECT_TRUE(resp.ok);
    auto data = nlohmann::json::parse(resp.data.value());
    EXPECT_EQ(data["mode"], "auto");
}

TEST_F(TransportServiceTest, SetFanInvalidMode) {
    config_.persist = true;
    config_.session_persist = true;

    auto cmd = make_command("set_fan", "req_001", R"({"mode": "invalid"})");
    auto resp = service_->dispatch(cmd);

    EXPECT_FALSE(resp.ok);
    EXPECT_EQ(resp.error.value(), ErrorCode::FanSpeedInvalid);
}

TEST_F(TransportServiceTest, GetPowerMode) {
    platform_.ec_registers[0x04FE] = 4;  // DC-In
    power_->update_power_state();

    auto cmd = make_command("get_power_mode");
    auto resp = service_->dispatch(cmd);

    EXPECT_TRUE(resp.ok);
    auto data = nlohmann::json::parse(resp.data.value());
    EXPECT_EQ(data["mode"], "dc_in");
    EXPECT_EQ(data["label"], "DC-In (dedicated charger)");
}

TEST_F(TransportServiceTest, GetProfilesEmpty) {
    auto cmd = make_command("get_profiles");
    auto resp = service_->dispatch(cmd);

    EXPECT_TRUE(resp.ok);
    auto data = nlohmann::json::parse(resp.data.value());
    EXPECT_TRUE(data["profiles"].is_array());
    EXPECT_EQ(data["profiles"].size(), 0);
}

TEST_F(TransportServiceTest, SaveAndGetProfile) {
    // First save a fan curve (mandatory for fixed profiles)
    auto fc_cmd = make_command("save_fan_curve", "req_000",
        R"({"name": "Default", "points": [{"temp_c": 40, "speed_pct": 20}, {"temp_c": 80, "speed_pct": 80}]})");
    service_->dispatch(fc_cmd);

    auto save_cmd = make_command("save_profile", "req_001",
        R"({"name": "Gaming", "type": "fixed", "stapm": 45, "fast": 50, "slow": 45, "fan_curve": "default"})");
    auto save_resp = service_->dispatch(save_cmd);

    EXPECT_TRUE(save_resp.ok);
    auto save_data = nlohmann::json::parse(save_resp.data.value());
    EXPECT_EQ(save_data["id"], "gaming");

    // Now get profiles
    auto get_cmd = make_command("get_profiles");
    auto get_resp = service_->dispatch(get_cmd);

    EXPECT_TRUE(get_resp.ok);
    auto get_data = nlohmann::json::parse(get_resp.data.value());
    EXPECT_EQ(get_data["profiles"].size(), 1);
    EXPECT_EQ(get_data["profiles"][0]["name"], "Gaming");
}

TEST_F(TransportServiceTest, SetProfilePersistDisabled) {
    config_.persist = false;

    auto cmd = make_command("set_profile", "req_001", R"({"id": "gaming"})");
    auto resp = service_->dispatch(cmd);

    EXPECT_FALSE(resp.ok);
    EXPECT_EQ(resp.error.value(), ErrorCode::PersistDisabled);
}

TEST_F(TransportServiceTest, SetProfileNotFound) {
    config_.persist = true;
    config_.session_persist = true;

    auto cmd = make_command("set_profile", "req_001", R"({"id": "nonexistent"})");
    auto resp = service_->dispatch(cmd);

    EXPECT_FALSE(resp.ok);
    EXPECT_EQ(resp.error.value(), ErrorCode::ProfileNotFound);
}

TEST_F(TransportServiceTest, DeleteProfileWithPowerState) {
    // In the unified profile system, power_state is stored on the profile itself.
    // Deleting a profile with a power_state assigned should succeed (state becomes unassigned).
    Profile p;
    p.id = "gaming";
    p.name = "Gaming";
    p.type = ProfileType::Fixed;
    p.power_state = PowerState::Source::DcIn;
    p.stapm_w = 45;
    p.fast_w = 50;
    p.slow_w = 45;
    profiles_.profiles["gaming"] = p;

    auto cmd = make_command("delete_profile", "req_001", R"({"id": "gaming"})");
    auto resp = service_->dispatch(cmd);

    EXPECT_TRUE(resp.ok);
    EXPECT_EQ(profiles_.profiles.size(), 0);
}

TEST_F(TransportServiceTest, SaveAndGetFanCurve) {
    auto save_cmd = make_command("save_fan_curve", "req_001",
        R"({"name": "Quiet", "points": [{"temp_c": 40, "speed_pct": 15}, {"temp_c": 80, "speed_pct": 80}]})");
    auto save_resp = service_->dispatch(save_cmd);

    EXPECT_TRUE(save_resp.ok);
    auto save_data = nlohmann::json::parse(save_resp.data.value());
    EXPECT_EQ(save_data["id"], "quiet");

    // Now get fan curves
    auto get_cmd = make_command("get_fan_curves");
    auto get_resp = service_->dispatch(get_cmd);

    EXPECT_TRUE(get_resp.ok);
    auto get_data = nlohmann::json::parse(get_resp.data.value());
    EXPECT_EQ(get_data["fan_curves"].size(), 1);
    EXPECT_EQ(get_data["fan_curves"][0]["name"], "Quiet");
}

TEST_F(TransportServiceTest, SaveFanCurveInvalid) {
    // Too few points
    auto cmd = make_command("save_fan_curve", "req_001",
        R"({"name": "Bad", "points": [{"temp_c": 40, "speed_pct": 15}]})");
    auto resp = service_->dispatch(cmd);

    EXPECT_FALSE(resp.ok);
    EXPECT_EQ(resp.error.value(), ErrorCode::FanCurveInvalid);
}

TEST_F(TransportServiceTest, DeleteFanCurveInUse) {
    // Save a fan curve
    profiles_.fan_curves["aggressive"] = {"aggressive", "Aggressive",
        {{40, 30}, {80, 100}}};

    // Reference it from a profile
    Profile p;
    p.id = "gaming";
    p.name = "Gaming";
    p.type = ProfileType::Fixed;
    p.stapm_w = 45;
    p.fast_w = 50;
    p.slow_w = 45;
    p.fan_curve = "aggressive";
    profiles_.profiles["gaming"] = p;

    auto cmd = make_command("delete_fan_curve", "req_001", R"({"id": "aggressive"})");
    auto resp = service_->dispatch(cmd);

    EXPECT_FALSE(resp.ok);
    EXPECT_EQ(resp.error.value(), ErrorCode::FanCurveInUse);
}

TEST_F(TransportServiceTest, GetChargeLimit) {
    platform_.ec_registers[0x04A3] = 85;

    auto cmd = make_command("get_charge_limit");
    auto resp = service_->dispatch(cmd);

    EXPECT_TRUE(resp.ok);
    auto data = nlohmann::json::parse(resp.data.value());
    EXPECT_EQ(data["percent"], 85);
}

TEST_F(TransportServiceTest, SetChargeLimitPersistDisabled) {
    config_.persist = false;

    auto cmd = make_command("set_charge_limit", "req_001", R"({"percent": 85})");
    auto resp = service_->dispatch(cmd);

    EXPECT_FALSE(resp.ok);
    EXPECT_EQ(resp.error.value(), ErrorCode::PersistDisabled);
}

TEST_F(TransportServiceTest, SetChargeLimitInvalidRange) {
    config_.persist = true;
    config_.session_persist = true;

    auto cmd = make_command("set_charge_limit", "req_001", R"({"percent": 50})");
    auto resp = service_->dispatch(cmd);

    EXPECT_FALSE(resp.ok);
    EXPECT_EQ(resp.error.value(), ErrorCode::ChargeLimitInvalid);
}

TEST_F(TransportServiceTest, GetConfig) {
    config_.language = "en";
    config_.theme = "dark";
    config_.persist = true;

    auto cmd = make_command("get_config");
    auto resp = service_->dispatch(cmd);

    EXPECT_TRUE(resp.ok);
    auto data = nlohmann::json::parse(resp.data.value());
    EXPECT_EQ(data["language"], "en");
    EXPECT_EQ(data["theme"], "dark");
    EXPECT_EQ(data["persist"], true);
}

TEST_F(TransportServiceTest, SetConfigPartialUpdate) {
    config_.language = "en";
    config_.theme = "system";
    config_.persist = false;

    // Only update theme
    auto cmd = make_command("set_config", "req_001", R"({"theme": "dark"})");
    auto resp = service_->dispatch(cmd);

    EXPECT_TRUE(resp.ok);

    // Verify only theme changed
    EXPECT_EQ(config_.language, "en");      // Unchanged
    EXPECT_EQ(config_.theme, "dark");        // Updated
    EXPECT_EQ(config_.persist, false);       // Unchanged
}

TEST_F(TransportServiceTest, RequestIdCorrelation) {
    auto cmd = make_command("ping", "unique_req_42");
    auto resp = service_->dispatch(cmd);

    EXPECT_EQ(resp.id, "unique_req_42");
}

TEST_F(TransportServiceTest, GetButton) {
    platform_.ec_registers[0x0230] = 0x06;

    auto cmd = make_command("get_button");
    auto resp = service_->dispatch(cmd);

    EXPECT_TRUE(resp.ok);
    auto data = nlohmann::json::parse(resp.data.value());
    EXPECT_EQ(data["presses"], 0x06);
}

// ===== ProcessManager Tests =====

class ProcessManagerTest : public ::testing::Test {
protected:
    void SetUp() override {
        manager_ = std::make_unique<ProcessManager>(platform_);
    }

    void TearDown() override {
        if (manager_) {
            manager_->stop_monitor();
        }
    }

    MockPlatform platform_;
    std::unique_ptr<ProcessManager> manager_;
};

TEST_F(ProcessManagerTest, InitiallyNotRunning) {
    EXPECT_FALSE(manager_->is_running());
    EXPECT_FALSE(manager_->is_monitoring());
}

TEST_F(ProcessManagerTest, SpawnStoresChildInfo) {
    auto result = manager_->spawn("C:/xmax/xmax.exe");
    ASSERT_TRUE(result.has_value());

    EXPECT_TRUE(manager_->is_running());
    EXPECT_EQ(platform_.spawn_count, 1);
    EXPECT_EQ(platform_.last_spawn_path, "C:/xmax/xmax.exe");

    auto child = manager_->child();
    EXPECT_GT(child.pid, 0u);
    EXPECT_NE(child.process_handle, nullptr);
}

TEST_F(ProcessManagerTest, SpawnFailureReturnsError) {
    platform_.spawn_should_fail = true;

    auto result = manager_->spawn("C:/xmax/xmax.exe");
    ASSERT_FALSE(result.has_value());
    EXPECT_EQ(result.error(), ErrorCode::HardwareBusy);
    EXPECT_FALSE(manager_->is_running());
}

TEST_F(ProcessManagerTest, ShowWindowCallsPlatform) {
    manager_->spawn("C:/xmax/xmax.exe");

    auto result = manager_->show_window(true);
    ASSERT_TRUE(result.has_value());

    ASSERT_EQ(platform_.show_window_calls.size(), 1u);
    EXPECT_TRUE(platform_.show_window_calls[0].visible);

    result = manager_->show_window(false);
    ASSERT_TRUE(result.has_value());
    ASSERT_EQ(platform_.show_window_calls.size(), 2u);
    EXPECT_FALSE(platform_.show_window_calls[1].visible);
}

TEST_F(ProcessManagerTest, ShowWindowNoChildCallsPlatformFallback) {
    // No spawn -- show_window still calls platform (FindWindow fallback by title)
    auto result = manager_->show_window(true);
    ASSERT_TRUE(result.has_value());
    EXPECT_EQ(platform_.show_window_calls.size(), 1u);
}

TEST_F(ProcessManagerTest, StartAndStopMonitor) {
    manager_->spawn("C:/xmax/xmax.exe");

    // Make wait block briefly (default mock behavior)
    platform_.wait_returns_immediately = false;

    manager_->start_monitor();
    EXPECT_TRUE(manager_->is_monitoring());

    manager_->stop_monitor();
    EXPECT_FALSE(manager_->is_monitoring());
}

TEST_F(ProcessManagerTest, MonitorDetectsCrash) {
    manager_->spawn("C:/xmax/xmax.exe");

    // Make wait return immediately with exit code 1 (crash)
    platform_.wait_returns_immediately = true;
    platform_.wait_exit_code = 1;

    // Don't actually respawn (make spawn fail on second call)
    bool crash_called = false;
    int crash_exit_code = 0;
    manager_->on_crash([&](int code) {
        crash_called = true;
        crash_exit_code = code;
    });

    manager_->start_monitor();

    // Wait for the crash to be detected
    std::this_thread::sleep_for(std::chrono::milliseconds(200));

    manager_->stop_monitor();

    EXPECT_TRUE(crash_called);
    EXPECT_EQ(crash_exit_code, 1);
}

TEST_F(ProcessManagerTest, RespawnAfterCrash) {
    manager_->spawn("C:/xmax/xmax.exe");
    EXPECT_EQ(platform_.spawn_count, 1);

    // Make wait return immediately (crash)
    platform_.wait_returns_immediately = true;
    platform_.wait_exit_code = 1;

    manager_->on_crash([](int) {});  // No-op callback

    manager_->start_monitor();

    // Wait for crash detection + 1s respawn delay + second spawn
    std::this_thread::sleep_for(std::chrono::milliseconds(1500));

    manager_->stop_monitor();

    // Should have spawned at least twice (initial + respawn)
    EXPECT_GE(platform_.spawn_count, 2);
}

TEST_F(ProcessManagerTest, TerminateCallsPlatform) {
    manager_->spawn("C:/xmax/xmax.exe");
    EXPECT_TRUE(manager_->is_running());

    manager_->terminate();
    EXPECT_FALSE(manager_->is_running());
    EXPECT_EQ(platform_.terminate_count, 1);
}

TEST_F(ProcessManagerTest, TerminateWithoutSpawnIsNoOp) {
    manager_->terminate();
    EXPECT_EQ(platform_.terminate_count, 0);
}

TEST_F(ProcessManagerTest, StopMonitorTerminatesChild) {
    manager_->spawn("C:/xmax/xmax.exe");

    platform_.wait_returns_immediately = false;
    manager_->start_monitor();
    EXPECT_TRUE(manager_->is_monitoring());

    manager_->stop_monitor();
    EXPECT_FALSE(manager_->is_monitoring());
    EXPECT_FALSE(manager_->is_running());
    EXPECT_GE(platform_.terminate_count, 1);
}

TEST_F(ProcessManagerTest, DestructorStopsMonitor) {
    manager_->spawn("C:/xmax/xmax.exe");
    platform_.wait_returns_immediately = false;
    manager_->start_monitor();
    EXPECT_TRUE(manager_->is_monitoring());

    manager_.reset();

    // If we get here without hanging, destructor properly stopped the monitor
    SUCCEED();
}

TEST_F(ProcessManagerTest, WaitFailureStopsMonitor) {
    manager_->spawn("C:/xmax/xmax.exe");

    // Make wait fail (simulates invalid handle)
    platform_.wait_returns_immediately = true;
    platform_.wait_should_fail = true;

    manager_->start_monitor();

    // Wait should fail immediately, monitor exits
    std::this_thread::sleep_for(std::chrono::milliseconds(200));

    // Monitor thread should have exited due to wait failure
    // (it's no longer monitoring but the flag may still be true until joined)
    manager_->stop_monitor();
    EXPECT_FALSE(manager_->is_running());
}

// ===== TrayManager Tests =====

class TrayManagerTest : public ::testing::Test {
protected:
    void SetUp() override {
        tray_ = std::make_unique<TrayManager>(platform_);
    }

    void TearDown() override {
        tray_.reset();
    }

    MockPlatform platform_;
    std::unique_ptr<TrayManager> tray_;
};

TEST_F(TrayManagerTest, InitiallyInactive) {
    EXPECT_FALSE(tray_->is_active());
}

TEST_F(TrayManagerTest, StartCreatesTrayIcon) {
    auto result = tray_->start();
    ASSERT_TRUE(result.has_value());
    EXPECT_TRUE(tray_->is_active());
    EXPECT_EQ(platform_.tray_create_count, 1);
}

TEST_F(TrayManagerTest, StartFailureReturnsError) {
    platform_.tray_should_fail = true;

    auto result = tray_->start();
    ASSERT_FALSE(result.has_value());
    EXPECT_FALSE(tray_->is_active());
}

TEST_F(TrayManagerTest, StopRemovesTrayIcon) {
    tray_->start();
    EXPECT_TRUE(tray_->is_active());

    tray_->stop();
    EXPECT_FALSE(tray_->is_active());
    EXPECT_EQ(platform_.tray_remove_count, 1);
}

TEST_F(TrayManagerTest, DoubleStartIsNoOp) {
    tray_->start();
    tray_->start();

    EXPECT_EQ(platform_.tray_create_count, 1);
}

TEST_F(TrayManagerTest, DoubleStopIsNoOp) {
    tray_->start();
    tray_->stop();
    tray_->stop();

    EXPECT_EQ(platform_.tray_remove_count, 1);
}

TEST_F(TrayManagerTest, UpdateTooltipFormatsMetrics) {
    tray_->start();

    Metrics metrics;
    metrics.cpu.package_watts = 45.2;
    metrics.cpu.temp_c = 79;

    tray_->update_tooltip(metrics, "Gaming");

    EXPECT_EQ(platform_.last_tray_tooltip, "45W | 79°C | Gaming");
}

TEST_F(TrayManagerTest, UpdateTooltipWithoutProfile) {
    tray_->start();

    Metrics metrics;
    metrics.cpu.package_watts = 30.0;
    metrics.cpu.temp_c = 65;

    tray_->update_tooltip(metrics);

    EXPECT_EQ(platform_.last_tray_tooltip, "30W | 65°C");
}

TEST_F(TrayManagerTest, UpdateTooltipWithMissingValues) {
    tray_->start();

    Metrics metrics;
    // No package_watts or temp_c set

    tray_->update_tooltip(metrics);

    EXPECT_EQ(platform_.last_tray_tooltip, "?W | ?°C");
}

TEST_F(TrayManagerTest, UpdateTooltipWhenInactiveIsNoOp) {
    // Don't start tray
    Metrics metrics;
    metrics.cpu.package_watts = 45.0;
    metrics.cpu.temp_c = 79;

    tray_->update_tooltip(metrics);

    // No tooltip update should happen
    EXPECT_TRUE(platform_.last_tray_tooltip.empty());
}

TEST_F(TrayManagerTest, CallbacksCanBeSet) {
    bool toggle_called = false;
    bool show_called = false;
    bool restart_called = false;
    bool quit_called = false;

    tray_->on_toggle([&]() { toggle_called = true; });
    tray_->on_show([&]() { show_called = true; });
    tray_->on_restart([&]() { restart_called = true; });
    tray_->on_quit([&]() { quit_called = true; });

    // Callbacks are stored but not invoked by TrayManager itself
    // (they're invoked by Platform's tray window procedure)
    EXPECT_FALSE(toggle_called);
    EXPECT_FALSE(show_called);
    EXPECT_FALSE(restart_called);
    EXPECT_FALSE(quit_called);
}

TEST_F(TrayManagerTest, DestructorStopsTray) {
    tray_->start();
    EXPECT_TRUE(tray_->is_active());

    tray_.reset();

    // If we get here without hanging, destructor properly stopped the tray
    EXPECT_EQ(platform_.tray_remove_count, 1);
}
