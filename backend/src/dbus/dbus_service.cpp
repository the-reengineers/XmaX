#include "dbus_service.h"

#include "../logger.h"
#include "../protocol.h"
#include "../transport.h"
#include "../platform/platform.h"

#include <nlohmann/json.hpp>

#include <chrono>
#include <cstdlib>
#include <cstring>
#include <string>

using json = nlohmann::json;

namespace {

constexpr const char* kServiceName = "org.xmax.Service";
constexpr const char* kServicePath = "/org/xmax/Service";

// polkit authority on the system bus.
constexpr const char* kPolkitName = "org.freedesktop.PolicyKit1";
constexpr const char* kPolkitPath = "/org/freedesktop/PolicyKit1/Authority";
constexpr const char* kPolkitInterface = "org.freedesktop.PolicyKit1.Authority";

// polkit CheckAuthorization flags: ALLOW_USER_INTERACTION.
constexpr uint32_t kPolkitAllowUserInteraction = 0x1u;

// How often to emit the MetricsChanged signal.
constexpr std::chrono::seconds kMetricsInterval{2};

// Map a dispatch Response back to a D-Bus error reply.
int reply_transport_error(sd_bus_message* m, const Response& resp) {
    std::string code = resp.error ? error_code_to_string(*resp.error) : "unknown_error";
    if (resp.error_message.has_value()) {
        return sd_bus_reply_method_errorf(m, "org.xmax.Service.Failed",
                                          "%s", resp.error_message->c_str());
    }
    return sd_bus_reply_method_errorf(m, "org.xmax.Service.Failed",
                                      "Backend rejected the command (%s)", code.c_str());
}

int reply_not_authorized(sd_bus_message* m) {
    return sd_bus_reply_method_errorf(m, "org.freedesktop.PolicyKit1.Error.NotAuthorized",
                                      "Not authorized");
}

// Read a flat a{sv} (dictionary of string -> variant of a basic type) into a
// json object. Returns false on malformed input.
bool read_asv(sd_bus_message* m, json& out) {
    int r = sd_bus_message_enter_container(m, 'a', "{sv}");
    if (r < 0) {
        return false;
    }

    out = json::object();

    while ((r = sd_bus_message_enter_container(m, 'e', "sv")) > 0) {
        const char* key = nullptr;
        if (sd_bus_message_read(m, "s", &key) < 0) {
            return false;
        }
        std::string k = key ? key : "";

        if (sd_bus_message_enter_container(m, 'v', nullptr) < 0) {
            return false;
        }

        // peek_type() reports the basic type in its first (type) out-param;
        // the "contents" out-param is only populated for container types.
        char t = '\0';
        if (sd_bus_message_peek_type(m, &t, nullptr) < 0) {
            t = '\0';
        }

        // Use a lambda-free switch to keep the container strictly nested.
        switch (t) {
            case 'u': { uint32_t v = 0; sd_bus_message_read(m, "u", &v); out[k] = v; break; }
            case 'q': { uint16_t v = 0; sd_bus_message_read(m, "q", &v); out[k] = v; break; }
            case 'i': { int32_t v = 0;  sd_bus_message_read(m, "i", &v); out[k] = v; break; }
            case 'n': { int16_t v = 0;  sd_bus_message_read(m, "n", &v); out[k] = v; break; }
            case 'x': { int64_t v = 0;  sd_bus_message_read(m, "x", &v); out[k] = v; break; }
            case 't': { uint64_t v = 0; sd_bus_message_read(m, "t", &v); out[k] = v; break; }
            case 'd': { double v = 0.0; sd_bus_message_read(m, "d", &v); out[k] = v; break; }
            case 'b': { int v = 0;      sd_bus_message_read(m, "b", &v); out[k] = (v != 0); break; }
            case 's':
            case 'o': { const char* v = nullptr; sd_bus_message_read(m, "s", &v); out[k] = v ? v : ""; break; }
            default:
                // Unsupported variant type — bail.
                sd_bus_message_exit_container(m);
                sd_bus_message_exit_container(m);
                sd_bus_message_exit_container(m);
                return false;
        }

        sd_bus_message_exit_container(m);  // variant
        sd_bus_message_exit_container(m);  // entry
    }

    sd_bus_message_exit_container(m);  // array
    return true;
}

}  // namespace

DbusService::DbusService(TransportService& transport, Platform& platform)
    : transport_(transport)
    , platform_(platform)
{
}

DbusService::~DbusService() {
    stop();
}

bool DbusService::start() {
    if (running_.load()) {
        return true;
    }

    no_polkit_ = std::getenv("XMAX_NO_POLKIT") != nullptr;

    const char* bus_env = std::getenv("XMAX_DBUS_BUS");
    const bool use_user_bus = bus_env && std::string(bus_env) == "user";

    int r = use_user_bus ? sd_bus_open_user(&bus_) : sd_bus_open_system(&bus_);
    if (r < 0) {
        log_warn("[dbus] Failed to open bus (r=" + std::to_string(r) + ") — D-Bus disabled");
        bus_ = nullptr;
        return false;
    }

    r = sd_bus_request_name(bus_, kServiceName, 0);
    if (r < 0) {
        log_warn("[dbus] Failed to request name " + std::string(kServiceName)
                 + " (r=" + std::to_string(r) + ") — D-Bus disabled");
        sd_bus_unref(bus_);
        bus_ = nullptr;
        return false;
    }

    int ignored = use_user_bus ? sd_bus_open_user(&auth_bus_) : sd_bus_open_system(&auth_bus_);
    if (ignored < 0) {
        // Non-fatal: authorize() will simply deny privileged methods.
        auth_bus_ = nullptr;
    }

    static const sd_bus_vtable vtable[] = {
        SD_BUS_VTABLE_START(0),
        SD_BUS_METHOD("Ping", "", "", on_ping, 0),
        SD_BUS_METHOD("GetMetrics", "", "s", on_get_metrics, 0),
        SD_BUS_METHOD("SetTdp", "a{sv}", "", on_set_tdp, 0),
        SD_BUS_METHOD("SetFan", "a{sv}", "", on_set_fan, 0),
        SD_BUS_METHOD("SetChargeLimit", "u", "", on_set_charge_limit, 0),
        SD_BUS_METHOD("GetChargeLimit", "", "u", on_get_charge_limit, 0),
        SD_BUS_METHOD("SetAutoStart", "b", "", on_set_auto_start, 0),
        SD_BUS_METHOD("GetProfiles", "", "s", on_get_profiles, 0),
        SD_BUS_METHOD("SetProfile", "s", "", on_set_profile, 0),
        SD_BUS_METHOD("SaveProfile", "s", "", on_save_profile, 0),
        SD_BUS_METHOD("DeleteProfile", "s", "", on_delete_profile, 0),
        SD_BUS_METHOD("GetFanCurves", "", "s", on_get_fan_curves, 0),
        SD_BUS_METHOD("SaveFanCurve", "s", "", on_save_fan_curve, 0),
        SD_BUS_METHOD("DeleteFanCurve", "s", "", on_delete_fan_curve, 0),
        SD_BUS_PROPERTY("Version", "s", on_get_version, 0, SD_BUS_VTABLE_PROPERTY_CONST),
        SD_BUS_PROPERTY("AutoStartEnabled", "b", on_get_auto_start, 0, SD_BUS_VTABLE_PROPERTY_EMITS_CHANGE),
        SD_BUS_SIGNAL("MetricsChanged", "a{sv}", 0),
        SD_BUS_VTABLE_END
    };

    r = sd_bus_add_object_vtable(bus_, nullptr, kServicePath, kServiceName, vtable, this);
    if (r < 0) {
        log_warn("[dbus] Failed to register object vtable (r=" + std::to_string(r) + ") — D-Bus disabled");
        sd_bus_unref(auth_bus_);
        auth_bus_ = nullptr;
        sd_bus_unref(bus_);
        bus_ = nullptr;
        return false;
    }

    running_.store(true);
    thread_ = std::thread(&DbusService::run, this);
    return true;
}

void DbusService::stop() {
    if (!running_.exchange(false)) {
        return;
    }

    // Wake the loop thread if it is blocked in sd_bus_wait().
    if (bus_) {
        sd_bus_close(bus_);
    }

    if (thread_.joinable()) {
        thread_.join();
    }

    if (bus_) {
        sd_bus_unref(bus_);
        bus_ = nullptr;
    }
    if (auth_bus_) {
        sd_bus_unref(auth_bus_);
        auth_bus_ = nullptr;
    }
}

auto DbusService::is_running() const -> bool {
    return running_.load();
}

void DbusService::run() {
    auto next_emit = std::chrono::steady_clock::now() + kMetricsInterval;

    while (running_.load()) {
        int r = sd_bus_process(bus_, nullptr);
        if (r < 0) {
            log_error("[dbus] sd_bus_process failed (r=" + std::to_string(r) + ")");
            break;
        }
        if (r > 0) {
            continue;  // More messages queued — drain before blocking.
        }

        auto now = std::chrono::steady_clock::now();
        if (now >= next_emit) {
            emit_metrics_changed();
            next_emit = now + kMetricsInterval;
            continue;
        }

        auto remaining_us = std::chrono::duration_cast<std::chrono::microseconds>(next_emit - now).count();
        r = sd_bus_wait(bus_, static_cast<uint64_t>(remaining_us));
        if (r < 0) {
            log_error("[dbus] sd_bus_wait failed (r=" + std::to_string(r) + ")");
            break;
        }
    }
}

bool DbusService::authorize(sd_bus_message* m, const char* action_id) {
    if (no_polkit_) {
        return true;
    }
    if (!auth_bus_) {
        return false;
    }

    const char* sender = sd_bus_message_get_sender(m);
    if (!sender) {
        return false;
    }

    sd_bus_message* call = nullptr;
    int r = sd_bus_message_new_method_call(auth_bus_, &call, kPolkitName, kPolkitPath,
                                           kPolkitInterface, "CheckAuthorization");
    if (r < 0) {
        return false;
    }

    // subject: (sa{sv}) = ("system-bus-name", {"name": <sender>}).
    r = sd_bus_message_open_container(call, 'r', "sa{sv}");
    r = r ?: sd_bus_message_append(call, "s", "system-bus-name");
    r = r ?: sd_bus_message_open_container(call, 'a', "{sv}");
    r = r ?: sd_bus_message_open_container(call, 'e', "sv");
    r = r ?: sd_bus_message_append(call, "s", "name");
    r = r ?: sd_bus_message_open_container(call, 'v', "s");
    r = r ?: sd_bus_message_append(call, "s", sender);
    r = r ?: sd_bus_message_close_container(call);  // variant
    r = r ?: sd_bus_message_close_container(call);  // entry
    r = r ?: sd_bus_message_close_container(call);  // a{sv}
    r = r ?: sd_bus_message_close_container(call);  // (sa{sv})

    r = r ?: sd_bus_message_append(call, "s", action_id);

    // details: empty a{ss}.
    r = r ?: sd_bus_message_open_container(call, 'a', "{ss}");
    r = r ?: sd_bus_message_close_container(call);

    r = r ?: sd_bus_message_append(call, "u", kPolkitAllowUserInteraction);
    r = r ?: sd_bus_message_append(call, "s", "");
    if (r < 0) {
        sd_bus_message_unref(call);
        return false;
    }

    sd_bus_error error = SD_BUS_ERROR_NULL;
    sd_bus_message* reply = nullptr;
    r = sd_bus_call(auth_bus_, call, 0, &error, &reply);
    sd_bus_message_unref(call);
    if (r < 0) {
        log_warn("[dbus] polkit CheckAuthorization failed: "
                 + std::string(error.message ? error.message : "(unknown)"));
        sd_bus_error_free(&error);
        return false;
    }

    int is_authorized = 0, is_challenge = 0;
    sd_bus_message_read(reply, "bb", &is_authorized, &is_challenge);
    sd_bus_message_unref(reply);

    if (!is_authorized) {
        log_info("[dbus] polkit denied action " + std::string(action_id) + " for " + std::string(sender));
    }
    return is_authorized != 0;
}

void DbusService::emit_metrics_changed() {
    Command cmd;
    cmd.method = "get_metrics";
    cmd.id = "dbus";
    cmd.payload = "{}";

    Response resp = transport_.dispatch(cmd);
    std::string metrics_json = (resp.ok && resp.data) ? *resp.data : "{}";

    sd_bus_message* sig = nullptr;
    if (sd_bus_message_new_signal(bus_, &sig, kServicePath, kServiceName, "MetricsChanged") < 0) {
        return;
    }

    int r = sd_bus_message_append(sig, "a{sv}", 1, "metrics", "s", metrics_json.c_str());
    if (r >= 0) {
        r = sd_bus_send(bus_, sig, nullptr);
        if (r >= 0) {
            r = sd_bus_flush(bus_);
        }
    }
    if (r < 0) {
        log_warn("[dbus] MetricsChanged send failed (r=" + std::to_string(r) + ")");
    }
    sd_bus_message_unref(sig);
}

// ===== vtable callbacks =====

int DbusService::on_ping(sd_bus_message* m, void*, sd_bus_error*) {
    return sd_bus_reply_method_return(m, "");
}

int DbusService::on_get_metrics(sd_bus_message* m, void* userdata, sd_bus_error*) {
    auto* self = static_cast<DbusService*>(userdata);

    Command cmd;
    cmd.method = "get_metrics";
    cmd.id = "dbus";
    cmd.payload = "{}";

    Response resp = self->transport_.dispatch(cmd);
    if (!resp.ok) {
        return reply_transport_error(m, resp);
    }
    std::string data = resp.data.value_or("{}");
    return sd_bus_reply_method_return(m, "s", data.c_str());
}

int DbusService::on_set_tdp(sd_bus_message* m, void* userdata, sd_bus_error*) {
    auto* self = static_cast<DbusService*>(userdata);

    if (!self->authorize(m, "org.xmax.set-tdp")) {
        return reply_not_authorized(m);
    }

    json args;
    if (!read_asv(m, args)) {
        return sd_bus_reply_method_errorf(m, "org.freedesktop.DBus.Error.InvalidArgs",
                                          "Expected a{sv} with keys stapm/fast/slow");
    }
    if (!args.contains("stapm") || !args.contains("fast") || !args.contains("slow")) {
        return sd_bus_reply_method_errorf(m, "org.freedesktop.DBus.Error.InvalidArgs",
                                          "Missing stapm/fast/slow");
    }

    json payload;
    payload["stapm"] = args["stapm"];
    payload["fast"] = args["fast"];
    payload["slow"] = args["slow"];

    Command cmd;
    cmd.method = "set_tdp";
    cmd.id = "dbus";
    cmd.payload = payload.dump();

    Response resp = self->transport_.dispatch(cmd);
    if (!resp.ok) {
        return reply_transport_error(m, resp);
    }
    return sd_bus_reply_method_return(m, "");
}

int DbusService::on_set_fan(sd_bus_message* m, void* userdata, sd_bus_error*) {
    auto* self = static_cast<DbusService*>(userdata);

    if (!self->authorize(m, "org.xmax.set-fan")) {
        return reply_not_authorized(m);
    }

    json args;
    if (!read_asv(m, args)) {
        return sd_bus_reply_method_errorf(m, "org.freedesktop.DBus.Error.InvalidArgs",
                                          "Expected a{sv} with key mode");
    }
    if (!args.contains("mode")) {
        return sd_bus_reply_method_errorf(m, "org.freedesktop.DBus.Error.InvalidArgs",
                                          "Missing mode");
    }

    json payload;
    payload["mode"] = args["mode"];
    if (args.contains("curve_id")) {
        payload["curve_id"] = args["curve_id"];
    }

    Command cmd;
    cmd.method = "set_fan";
    cmd.id = "dbus";
    cmd.payload = payload.dump();

    Response resp = self->transport_.dispatch(cmd);
    if (!resp.ok) {
        return reply_transport_error(m, resp);
    }
    return sd_bus_reply_method_return(m, "");
}

int DbusService::on_set_charge_limit(sd_bus_message* m, void* userdata, sd_bus_error*) {
    auto* self = static_cast<DbusService*>(userdata);

    if (!self->authorize(m, "org.xmax.set-charge-limit")) {
        return reply_not_authorized(m);
    }

    uint32_t percent = 0;
    if (sd_bus_message_read(m, "u", &percent) < 0) {
        return sd_bus_reply_method_errorf(m, "org.freedesktop.DBus.Error.InvalidArgs",
                                          "Expected u percent");
    }

    json payload;
    payload["percent"] = percent;

    Command cmd;
    cmd.method = "set_charge_limit";
    cmd.id = "dbus";
    cmd.payload = payload.dump();

    Response resp = self->transport_.dispatch(cmd);
    if (!resp.ok) {
        return reply_transport_error(m, resp);
    }
    return sd_bus_reply_method_return(m, "");
}

int DbusService::on_get_charge_limit(sd_bus_message* m, void* userdata, sd_bus_error*) {
    auto* self = static_cast<DbusService*>(userdata);

    Command cmd;
    cmd.method = "get_charge_limit";
    cmd.id = "dbus";
    cmd.payload = "{}";

    Response resp = self->transport_.dispatch(cmd);
    if (!resp.ok || !resp.data) {
        return reply_transport_error(m, resp);
    }

    uint32_t percent = 0;
    try {
        percent = json::parse(*resp.data).value("percent", 0u);
    } catch (const json::exception&) {
        return sd_bus_reply_method_errorf(m, "org.xmax.Service.Failed", "Malformed response");
    }
    return sd_bus_reply_method_return(m, "u", percent);
}

int DbusService::on_set_auto_start(sd_bus_message* m, void* userdata, sd_bus_error*) {
    auto* self = static_cast<DbusService*>(userdata);

    if (!self->authorize(m, "org.xmax.set-auto-start")) {
        return reply_not_authorized(m);
    }

    int enabled = 0;
    if (sd_bus_message_read(m, "b", &enabled) < 0) {
        return sd_bus_reply_method_errorf(m, "org.freedesktop.DBus.Error.InvalidArgs",
                                          "Expected b enabled");
    }

    auto result = self->platform_.set_auto_start(enabled != 0, self->platform_.self_exe_path());
    if (!result) {
        std::string code = error_code_to_string(result.error());
        return sd_bus_reply_method_errorf(m, "org.xmax.Service.Failed",
                                          "Failed to set auto-start (%s)", code.c_str());
    }
    return sd_bus_reply_method_return(m, "");
}

int DbusService::on_get_version(sd_bus* /*bus*/, const char* /*path*/, const char* /*interface*/,
                                const char* /*property*/, sd_bus_message* reply,
                                void* /*userdata*/, sd_bus_error* /*ret_error*/) {
    int r = sd_bus_message_append(reply, "s", XMAX_VERSION);
    if (r < 0) {
        return r;
    }
    return 1;
}

int DbusService::on_get_auto_start(sd_bus* /*bus*/, const char* /*path*/, const char* /*interface*/,
                                   const char* /*property*/, sd_bus_message* reply,
                                   void* userdata, sd_bus_error* /*ret_error*/) {
    auto* self = static_cast<DbusService*>(userdata);
    auto result = self->platform_.is_auto_start_enabled();
    int enabled = result ? static_cast<int>(result.value()) : 0;

    int r = sd_bus_message_append(reply, "b", enabled);
    if (r < 0) {
        return r;
    }
    return 1;
}

// ===== Profile + fan-curve vtable callbacks =====

int DbusService::on_get_profiles(sd_bus_message* m, void* userdata, sd_bus_error*) {
    auto* self = static_cast<DbusService*>(userdata);

    Command cmd;
    cmd.method = "get_profiles";
    cmd.id = "dbus";
    cmd.payload = "{}";

    Response resp = self->transport_.dispatch(cmd);
    if (!resp.ok || !resp.data) {
        return reply_transport_error(m, resp);
    }
    return sd_bus_reply_method_return(m, "s", resp.data->c_str());
}

int DbusService::on_set_profile(sd_bus_message* m, void* userdata, sd_bus_error*) {
    auto* self = static_cast<DbusService*>(userdata);

    if (!self->authorize(m, "org.xmax.set-profile")) {
        return reply_not_authorized(m);
    }

    const char* id = nullptr;
    if (sd_bus_message_read(m, "s", &id) < 0) {
        return sd_bus_reply_method_errorf(m, "org.freedesktop.DBus.Error.InvalidArgs",
                                          "Expected s id");
    }

    json payload;
    payload["id"] = id ? id : "";

    Command cmd;
    cmd.method = "set_profile";
    cmd.id = "dbus";
    cmd.payload = payload.dump();

    Response resp = self->transport_.dispatch(cmd);
    if (!resp.ok) {
        return reply_transport_error(m, resp);
    }
    return sd_bus_reply_method_return(m, "");
}

int DbusService::on_save_profile(sd_bus_message* m, void* userdata, sd_bus_error*) {
    auto* self = static_cast<DbusService*>(userdata);

    if (!self->authorize(m, "org.xmax.set-profile")) {
        return reply_not_authorized(m);
    }

    const char* profile_json = nullptr;
    if (sd_bus_message_read(m, "s", &profile_json) < 0) {
        return sd_bus_reply_method_errorf(m, "org.freedesktop.DBus.Error.InvalidArgs",
                                          "Expected s profile_json");
    }

    Command cmd;
    cmd.method = "save_profile";
    cmd.id = "dbus";
    cmd.payload = profile_json ? profile_json : "{}";

    Response resp = self->transport_.dispatch(cmd);
    if (!resp.ok) {
        return reply_transport_error(m, resp);
    }
    return sd_bus_reply_method_return(m, "");
}

int DbusService::on_delete_profile(sd_bus_message* m, void* userdata, sd_bus_error*) {
    auto* self = static_cast<DbusService*>(userdata);

    if (!self->authorize(m, "org.xmax.set-profile")) {
        return reply_not_authorized(m);
    }

    const char* id = nullptr;
    if (sd_bus_message_read(m, "s", &id) < 0) {
        return sd_bus_reply_method_errorf(m, "org.freedesktop.DBus.Error.InvalidArgs",
                                          "Expected s id");
    }

    json payload;
    payload["id"] = id ? id : "";

    Command cmd;
    cmd.method = "delete_profile";
    cmd.id = "dbus";
    cmd.payload = payload.dump();

    Response resp = self->transport_.dispatch(cmd);
    if (!resp.ok) {
        return reply_transport_error(m, resp);
    }
    return sd_bus_reply_method_return(m, "");
}

int DbusService::on_get_fan_curves(sd_bus_message* m, void* userdata, sd_bus_error*) {
    auto* self = static_cast<DbusService*>(userdata);

    Command cmd;
    cmd.method = "get_fan_curves";
    cmd.id = "dbus";
    cmd.payload = "{}";

    Response resp = self->transport_.dispatch(cmd);
    if (!resp.ok || !resp.data) {
        return reply_transport_error(m, resp);
    }
    return sd_bus_reply_method_return(m, "s", resp.data->c_str());
}

int DbusService::on_save_fan_curve(sd_bus_message* m, void* userdata, sd_bus_error*) {
    auto* self = static_cast<DbusService*>(userdata);

    if (!self->authorize(m, "org.xmax.set-fan-curve")) {
        return reply_not_authorized(m);
    }

    const char* curve_json = nullptr;
    if (sd_bus_message_read(m, "s", &curve_json) < 0) {
        return sd_bus_reply_method_errorf(m, "org.freedesktop.DBus.Error.InvalidArgs",
                                          "Expected s curve_json");
    }

    Command cmd;
    cmd.method = "save_fan_curve";
    cmd.id = "dbus";
    cmd.payload = curve_json ? curve_json : "{}";

    Response resp = self->transport_.dispatch(cmd);
    if (!resp.ok) {
        return reply_transport_error(m, resp);
    }
    return sd_bus_reply_method_return(m, "");
}

int DbusService::on_delete_fan_curve(sd_bus_message* m, void* userdata, sd_bus_error*) {
    auto* self = static_cast<DbusService*>(userdata);

    if (!self->authorize(m, "org.xmax.set-fan-curve")) {
        return reply_not_authorized(m);
    }

    const char* id = nullptr;
    if (sd_bus_message_read(m, "s", &id) < 0) {
        return sd_bus_reply_method_errorf(m, "org.freedesktop.DBus.Error.InvalidArgs",
                                          "Expected s id");
    }

    json payload;
    payload["id"] = id ? id : "";

    Command cmd;
    cmd.method = "delete_fan_curve";
    cmd.id = "dbus";
    cmd.payload = payload.dump();

    Response resp = self->transport_.dispatch(cmd);
    if (!resp.ok) {
        return reply_transport_error(m, resp);
    }
    return sd_bus_reply_method_return(m, "");
}