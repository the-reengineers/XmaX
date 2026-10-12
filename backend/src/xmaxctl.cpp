// xmaxctl — command-line client for the XmaX backend daemon (xmaxd).
//
// Talks to org.xmax.Service over the D-Bus system bus, falling back to the
// AF_UNIX JSON-line socket (Phase 1) when the service is not on the bus (e.g.
// running xmaxd manually during development).
//
// Usage:
//   xmaxctl version
//   xmaxctl metrics
//   xmaxctl tdp <stapm_w> <fast_w> <slow_w>
//   xmaxctl fan auto | curve [curve_id]
//   xmaxctl charge-limit [percent]
//   xmaxctl auto-start [on|off]
//   xmaxctl profiles list [--json]
//   xmaxctl profiles set <id>
//   xmaxctl profiles save --name <name> [--id <slug>] [--type fixed|adaptive]
//     [--stapm W --fast W --slow W] [--fan-curve <id>]
//     [--tuning silent|default|performance] [--target-temp C]
//     [--tdp-max W] [--fan-max PCT] [--power-state <state>]
//   xmaxctl profiles delete <id>
//   xmaxctl fan-curves list [--json]
//   xmaxctl fan-curves save --id <slug> --name <name> --points <temp:speed,...>
//   xmaxctl fan-curves delete <id>

#include <systemd/sd-bus.h>

#include <nlohmann/json.hpp>

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <optional>
#include <string>
#include <vector>

#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

using json = nlohmann::json;

namespace {

constexpr const char* kServiceName = "org.xmax.Service";
constexpr const char* kServicePath = "/org/xmax/Service";

void usage(std::ostream& out) {
    out << "usage: xmaxctl version|metrics|tdp|fan|charge-limit|auto-start|profiles|fan-curves [...]\n"
        << "\n"
        << "  profiles list [--json]              list all profiles\n"
        << "  profiles set <id>                   apply a profile to hardware\n"
        << "  profiles save --name <name> [...]   create or replace a profile\n"
        << "  profiles delete <id>                delete a profile\n"
        << "  fan-curves list [--json]            list all fan curves\n"
        << "  fan-curves save --id <s> --name <s> --points <t:s,...>\n"
        << "  fan-curves delete <id>              delete a fan curve\n";
}

// ---- D-Bus helpers ---------------------------------------------------------

sd_bus* open_system_bus() {
    sd_bus* bus = nullptr;
    if (sd_bus_open_system(&bus) < 0) {
        return nullptr;
    }
    return bus;
}

void print_dbus_error(const char* what, const sd_bus_error* err) {
    std::cerr << what << ": "
              << (err && err->message ? err->message : "D-Bus error") << "\n";
}

// Append a flat a{sv} of string->uint32 onto an outgoing method-call message.
int append_asv_uint(sd_bus_message* m, const std::vector<std::pair<const char*, uint32_t>>& kvs) {
    int r = sd_bus_message_open_container(m, 'a', "{sv}");
    for (const auto& [k, v] : kvs) {
        r = r ?: sd_bus_message_open_container(m, 'e', "sv");
        r = r ?: sd_bus_message_append(m, "s", k);
        r = r ?: sd_bus_message_open_container(m, 'v', "u");
        r = r ?: sd_bus_message_append(m, "u", v);
        r = r ?: sd_bus_message_close_container(m);
        r = r ?: sd_bus_message_close_container(m);
    }
    return r ?: sd_bus_message_close_container(m);
}

// Append a flat a{sv} of string->string.
int append_asv_string(sd_bus_message* m, const std::vector<std::pair<const char*, const char*>>& kvs) {
    int r = sd_bus_message_open_container(m, 'a', "{sv}");
    for (const auto& [k, v] : kvs) {
        r = r ?: sd_bus_message_open_container(m, 'e', "sv");
        r = r ?: sd_bus_message_append(m, "s", k);
        r = r ?: sd_bus_message_open_container(m, 'v', "s");
        r = r ?: sd_bus_message_append(m, "s", v);
        r = r ?: sd_bus_message_close_container(m);
        r = r ?: sd_bus_message_close_container(m);
    }
    return r ?: sd_bus_message_close_container(m);
}

// ---- Socket fallback (AF_UNIX JSON-line protocol) --------------------------

std::string socket_path() {
    if (const char* rt = std::getenv("XDG_RUNTIME_DIR"); rt && *rt) {
        return std::string(rt) + "/xmax.sock";
    }
    return "/tmp/xmax-" + std::to_string(getuid()) + ".sock";
}

// Send one command and return the parsed response JSON object, or nullopt.
std::optional<json> socket_request(const std::string& method, const json& params) {
    int fd = ::socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0) {
        return std::nullopt;
    }

    sockaddr_un addr{};
    addr.sun_family = AF_UNIX;
    std::strncpy(addr.sun_path, socket_path().c_str(), sizeof(addr.sun_path) - 1);
    if (::connect(fd, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) != 0) {
        ::close(fd);
        return std::nullopt;
    }

    json cmd = {
        {"type", "command"},
        {"method", method},
        {"id", "1"},
        {"params", params},
    };
    std::string line = cmd.dump() + "\n";
    if (::send(fd, line.data(), line.size(), MSG_NOSIGNAL) < 0) {
        ::close(fd);
        return std::nullopt;
    }

    // Read until we have a complete newline-delimited response line. The daemon
    // keeps the connection open for further commands, so reading until EOF would
    // block forever — stop as soon as a full line has arrived.
    std::string buf;
    char tmp[4096];
    ssize_t n;
    while (buf.find('\n') == std::string::npos
           && (n = ::read(fd, tmp, sizeof(tmp))) > 0) {
        buf.append(tmp, static_cast<size_t>(n));
    }
    ::close(fd);

    const auto pos = buf.find('\n');
    if (pos == std::string::npos) {
        return std::nullopt;
    }
    try {
        return json::parse(buf.substr(0, pos));
    } catch (const json::exception&) {
        return std::nullopt;
    }
}

// ---- Commands --------------------------------------------------------------

int cmd_version() {
    sd_bus* bus = open_system_bus();
    if (bus) {
        sd_bus_error err = SD_BUS_ERROR_NULL;
        sd_bus_message* reply = nullptr;
        int r = sd_bus_get_property(bus, kServiceName, kServicePath, kServiceName,
                                    "Version", &err, &reply, "s");
        if (r >= 0) {
            const char* v = nullptr;
            sd_bus_message_read(reply, "s", &v);
            std::cout << (v ? v : XMAX_VERSION) << "\n";
            sd_bus_message_unref(reply);
            sd_bus_unref(bus);
            return 0;
        }
        sd_bus_error_free(&err);
        sd_bus_unref(bus);
    }
    // Fall back to the compile-time constant.
    std::cout << XMAX_VERSION << "\n";
    return 0;
}

int cmd_metrics() {
    sd_bus* bus = open_system_bus();
    if (bus) {
        sd_bus_error err = SD_BUS_ERROR_NULL;
        sd_bus_message* reply = nullptr;
        int r = sd_bus_call_method(bus, kServiceName, kServicePath, kServiceName,
                                   "GetMetrics", &err, &reply, "");
        if (r >= 0) {
            const char* s = nullptr;
            sd_bus_message_read(reply, "s", &s);
            std::cout << (s ? s : "{}") << "\n";
            sd_bus_message_unref(reply);
            sd_bus_unref(bus);
            return 0;
        }
        print_dbus_error("metrics", &err);
        sd_bus_error_free(&err);
        sd_bus_unref(bus);
        // Fall through to the socket.
    }

    auto resp = socket_request("get_metrics", json::object());
    if (!resp) {
        std::cerr << "metrics: service not reachable (D-Bus or socket)\n";
        return 1;
    }
    if (!resp->value("ok", false)) {
        std::cerr << "metrics: " << resp->value("error", "error") << "\n";
        return 1;
    }
    std::cout << resp->value("data", json("{}")) << "\n";
    return 0;
}

int cmd_tdp(uint32_t stapm, uint32_t fast_w, uint32_t slow_w) {
    sd_bus* bus = open_system_bus();
    if (bus) {
        sd_bus_message* msg = nullptr;
        sd_bus_message_new_method_call(bus, &msg, kServiceName, kServicePath, kServiceName, "SetTdp");
        append_asv_uint(msg, {{"stapm", stapm}, {"fast", fast_w}, {"slow", slow_w}});
        sd_bus_error err = SD_BUS_ERROR_NULL;
        sd_bus_message* reply = nullptr;
        int r = sd_bus_call(bus, msg, 0, &err, &reply);
        if (r >= 0) {
            std::cout << "tdp: " << stapm << "/" << fast_w << "/" << slow_w << " W\n";
            sd_bus_message_unref(reply);
            sd_bus_message_unref(msg);
            sd_bus_unref(bus);
            return 0;
        }
        print_dbus_error("tdp", &err);
        sd_bus_error_free(&err);
        sd_bus_message_unref(msg);
        sd_bus_unref(bus);
    }

    auto resp = socket_request("set_tdp", json{{"stapm", stapm}, {"fast", fast_w}, {"slow", slow_w}});
    if (!resp) {
        std::cerr << "tdp: service not reachable (D-Bus or socket)\n";
        return 1;
    }
    if (!resp->value("ok", false)) {
        std::cerr << "tdp: " << resp->value("error", "error") << "\n";
        return 1;
    }
    std::cout << "tdp: " << stapm << "/" << fast_w << "/" << slow_w << " W\n";
    return 0;
}

int cmd_fan(const std::string& mode, const std::optional<std::string>& curve_id) {
    std::vector<std::pair<const char*, const char*>> args{{"mode", mode.c_str()}};
    if (curve_id) {
        args.emplace_back("curve_id", curve_id->c_str());
    }
    json params{{"mode", mode}};
    if (curve_id) {
        params["curve_id"] = *curve_id;
    }

    sd_bus* bus = open_system_bus();
    if (bus) {
        sd_bus_message* msg = nullptr;
        sd_bus_message_new_method_call(bus, &msg, kServiceName, kServicePath, kServiceName, "SetFan");
        append_asv_string(msg, args);
        sd_bus_error err = SD_BUS_ERROR_NULL;
        sd_bus_message* reply = nullptr;
        int r = sd_bus_call(bus, msg, 0, &err, &reply);
        if (r >= 0) {
            std::cout << "fan: " << mode << "\n";
            sd_bus_message_unref(reply);
            sd_bus_message_unref(msg);
            sd_bus_unref(bus);
            return 0;
        }
        print_dbus_error("fan", &err);
        sd_bus_error_free(&err);
        sd_bus_message_unref(msg);
        sd_bus_unref(bus);
    }

    auto resp = socket_request("set_fan", params);
    if (!resp) {
        std::cerr << "fan: service not reachable (D-Bus or socket)\n";
        return 1;
    }
    if (!resp->value("ok", false)) {
        std::cerr << "fan: " << resp->value("error", "error") << "\n";
        return 1;
    }
    std::cout << "fan: " << mode << "\n";
    return 0;
}

int cmd_charge_limit(const std::optional<uint32_t>& percent) {
    if (percent) {
        sd_bus* bus = open_system_bus();
        if (bus) {
            sd_bus_error err = SD_BUS_ERROR_NULL;
            sd_bus_message* reply = nullptr;
            int r = sd_bus_call_method(bus, kServiceName, kServicePath, kServiceName,
                                       "SetChargeLimit", &err, &reply, "u", *percent);
            if (r >= 0) {
                std::cout << "charge-limit: " << *percent << "%\n";
                sd_bus_message_unref(reply);
                sd_bus_unref(bus);
                return 0;
            }
            print_dbus_error("charge-limit", &err);
            sd_bus_error_free(&err);
            sd_bus_unref(bus);
        }

        auto resp = socket_request("set_charge_limit", json{{"percent", *percent}});
        if (!resp) {
            std::cerr << "charge-limit: service not reachable (D-Bus or socket)\n";
            return 1;
        }
        if (!resp->value("ok", false)) {
            std::cerr << "charge-limit: " << resp->value("error", "error") << "\n";
            return 1;
        }
        std::cout << "charge-limit: " << *percent << "%\n";
        return 0;
    }

    // Read current limit.
    sd_bus* bus = open_system_bus();
    if (bus) {
        sd_bus_error err = SD_BUS_ERROR_NULL;
        sd_bus_message* reply = nullptr;
        int r = sd_bus_call_method(bus, kServiceName, kServicePath, kServiceName,
                                   "GetChargeLimit", &err, &reply, "");
        if (r >= 0) {
            uint32_t pct = 0;
            sd_bus_message_read(reply, "u", &pct);
            std::cout << pct << "%\n";
            sd_bus_message_unref(reply);
            sd_bus_unref(bus);
            return 0;
        }
        print_dbus_error("charge-limit", &err);
        sd_bus_error_free(&err);
        sd_bus_unref(bus);
    }

    auto resp = socket_request("get_charge_limit", json::object());
    if (!resp) {
        std::cerr << "charge-limit: service not reachable (D-Bus or socket)\n";
        return 1;
    }
    if (!resp->value("ok", false)) {
        std::cerr << "charge-limit: " << resp->value("error", "error") << "\n";
        return 1;
    }
    std::cout << resp->value("data", json{{"percent", -1}})["percent"] << "%\n";
    return 0;
}

int cmd_auto_start(const std::optional<bool>& enabled) {
    sd_bus* bus = open_system_bus();
    if (!bus) {
        std::cerr << "auto-start: D-Bus system bus unavailable\n";
        return 1;
    }

    if (enabled) {
        sd_bus_error err = SD_BUS_ERROR_NULL;
        sd_bus_message* reply = nullptr;
        int r = sd_bus_call_method(bus, kServiceName, kServicePath, kServiceName,
                                   "SetAutoStart", &err, &reply, "b", *enabled ? 1 : 0);
        if (r >= 0) {
            std::cout << (*enabled ? "on" : "off") << "\n";
            sd_bus_message_unref(reply);
            sd_bus_unref(bus);
            return 0;
        }
        print_dbus_error("auto-start", &err);
        sd_bus_error_free(&err);
        sd_bus_unref(bus);
        return 1;
    }

    sd_bus_error err = SD_BUS_ERROR_NULL;
    sd_bus_message* reply = nullptr;
    int r = sd_bus_get_property(bus, kServiceName, kServicePath, kServiceName,
                                "AutoStartEnabled", &err, &reply, "b");
    if (r < 0) {
        print_dbus_error("auto-start", &err);
        sd_bus_error_free(&err);
        sd_bus_unref(bus);
        return 1;
    }
    int v = 0;
    sd_bus_message_read(reply, "b", &v);
    std::cout << (v ? "on" : "off") << "\n";
    sd_bus_message_unref(reply);
    sd_bus_unref(bus);
    return 0;
}

// ---- Flag-parsing helpers for profiles/fan-curves --------------------------

// Find --flag in args starting at `start`. Returns the value (next arg) or nullopt.
std::optional<std::string> find_flag(const std::vector<std::string>& args,
                                     size_t start, const std::string& flag) {
    for (size_t i = start; i + 1 < args.size(); ++i) {
        if (args[i] == flag) {
            return args[i + 1];
        }
    }
    return std::nullopt;
}

bool has_flag(const std::vector<std::string>& args, size_t start, const std::string& flag) {
    for (size_t i = start; i < args.size(); ++i) {
        if (args[i] == flag) return true;
    }
    return false;
}

// ---- Profile commands ------------------------------------------------------

// D-Bus call: GetProfiles() -> JSON string. Falls back to socket.
std::optional<json> dbus_or_socket_get_profiles() {
    sd_bus* bus = open_system_bus();
    if (bus) {
        sd_bus_error err = SD_BUS_ERROR_NULL;
        sd_bus_message* reply = nullptr;
        int r = sd_bus_call_method(bus, kServiceName, kServicePath, kServiceName,
                                   "GetProfiles", &err, &reply, "");
        if (r >= 0) {
            const char* s = nullptr;
            sd_bus_message_read(reply, "s", &s);
            json result;
            try { result = json::parse(s ? s : "{}"); } catch (...) { result = json::object(); }
            sd_bus_message_unref(reply);
            sd_bus_unref(bus);
            return result;
        }
        sd_bus_error_free(&err);
        sd_bus_unref(bus);
    }
    auto resp = socket_request("get_profiles", json::object());
    if (!resp || !resp->value("ok", false)) return std::nullopt;
    // data may be a JSON object or a JSON-encoded string
    auto data = resp->value("data", json::object());
    if (data.is_string()) {
        try { return json::parse(data.get<std::string>()); } catch (...) { return std::nullopt; }
    }
    return data;
}

// D-Bus call: simple string-in, void-out method. Returns true on success.
bool dbus_profile_void_method(const char* method, const std::string& arg) {
    sd_bus* bus = open_system_bus();
    if (bus) {
        sd_bus_error err = SD_BUS_ERROR_NULL;
        sd_bus_message* reply = nullptr;
        int r = sd_bus_call_method(bus, kServiceName, kServicePath, kServiceName,
                                   method, &err, &reply, "s", arg.c_str());
        if (r >= 0) {
            sd_bus_message_unref(reply);
            sd_bus_unref(bus);
            return true;
        }
        print_dbus_error(method, &err);
        sd_bus_error_free(&err);
        sd_bus_unref(bus);
    }
    // Socket fallback uses the same method name.
    std::string key = (std::strcmp(method, "SetProfile") == 0 || std::strcmp(method, "DeleteProfile") == 0)
                          ? "id" : "";
    json params;
    if (key[0]) params[key] = arg;
    else params = json::object();
    // Map method name -> socket command name.
    std::string socket_method;
    if (std::strcmp(method, "SetProfile") == 0) socket_method = "set_profile";
    else if (std::strcmp(method, "DeleteProfile") == 0) socket_method = "delete_profile";
    else return false;

    auto resp = socket_request(socket_method, params);
    return resp && resp->value("ok", false);
}

int cmd_profiles_list(bool as_json) {
    auto data = dbus_or_socket_get_profiles();
    if (!data) {
        std::cerr << "profiles: service not reachable (D-Bus or socket)\n";
        return 1;
    }
    const auto& profiles = data->value("profiles", json::array());
    if (as_json) {
        std::cout << profiles.dump(2) << "\n";
        return 0;
    }
    if (profiles.empty()) {
        std::cout << "(no profiles)\n";
        return 0;
    }
    // Table header
    std::cout << "ID                TYPE       DEFAULT  POWER_STATE   NAME\n";
    for (const auto& p : profiles) {
        std::string id = p.value("id", "");
        std::string type = p.value("type", "fixed");
        bool is_default = p.value("is_default", false);
        std::string ps;
        if (p.contains("power_state") && !p["power_state"].is_null()) {
            ps = p["power_state"].get<std::string>();
        }
        std::string name = p.value("name", "");
        // Pad columns
        char row[256];
        std::snprintf(row, sizeof(row), "%-17s %-10s %-8s %-13s %s",
                      id.c_str(), type.c_str(),
                      is_default ? "yes" : "no",
                      ps.empty() ? "-" : ps.c_str(),
                      name.c_str());
        std::cout << row << "\n";
        // Show TDP summary for fixed profiles
        if (type == "fixed" && p.contains("tdp")) {
            const auto& t = p["tdp"];
            std::cout << "                  stapm=" << t.value("stapm", 0)
                      << "W  fast=" << t.value("fast", 0)
                      << "W  slow=" << t.value("slow", 0) << "W";
            if (p.contains("fan_curve") && !p["fan_curve"].is_null()) {
                std::string fc = p["fan_curve"].get<std::string>();
                if (!fc.empty()) std::cout << "  fan_curve=" << fc;
            }
            std::cout << "\n";
        } else if (type == "adaptive") {
            std::cout << "                  tuning=" << p.value("tuning", "default")
                      << "  target_temp=" << p.value("target_temp_c", 85) << "C"
                      << "  tdp_max=" << p.value("tdp_max_w", 55) << "W"
                      << "  fan_max=" << p.value("fan_max_pct", 100) << "%\n";
        }
    }
    return 0;
}

int cmd_profiles_set(const std::string& id) {
    if (!dbus_profile_void_method("SetProfile", id)) {
        std::cerr << "profiles set: failed (service unreachable or persist disabled)\n";
        return 1;
    }
    std::cout << "profile applied: " << id << "\n";
    return 0;
}

int cmd_profiles_save(const std::vector<std::string>& args, size_t start) {
    auto name = find_flag(args, start, "--name");
    if (!name) {
        std::cerr << "profiles save: --name is required\n";
        return 1;
    }

    json profile;
    profile["name"] = *name;

    if (auto id = find_flag(args, start, "--id")) profile["id"] = *id;

    std::string type = find_flag(args, start, "--type").value_or("fixed");
    profile["type"] = type;

    if (type == "fixed") {
        auto stapm = find_flag(args, start, "--stapm");
        auto fast = find_flag(args, start, "--fast");
        auto slow = find_flag(args, start, "--slow");
        if (stapm) profile["stapm"] = std::stoi(*stapm);
        if (fast) profile["fast"] = std::stoi(*fast);
        if (slow) profile["slow"] = std::stoi(*slow);
        // Default to "default" fan curve if not specified
        auto fc = find_flag(args, start, "--fan-curve");
        profile["fan_curve"] = fc.value_or("default");
    } else if (type == "adaptive") {
        if (auto t = find_flag(args, start, "--tuning")) profile["tuning"] = *t;
        if (auto tt = find_flag(args, start, "--target-temp")) profile["target_temp_c"] = std::stoi(*tt);
        if (auto tm = find_flag(args, start, "--tdp-max")) profile["tdp_max_w"] = std::stoi(*tm);
        if (auto fm = find_flag(args, start, "--fan-max")) profile["fan_max_pct"] = std::stoi(*fm);
    }

    if (auto ps = find_flag(args, start, "--power-state")) profile["power_state"] = *ps;

    // D-Bus call: SaveProfile(json_string)
    sd_bus* bus = open_system_bus();
    if (bus) {
        sd_bus_error err = SD_BUS_ERROR_NULL;
        sd_bus_message* reply = nullptr;
        std::string payload = profile.dump();
        int r = sd_bus_call_method(bus, kServiceName, kServicePath, kServiceName,
                                   "SaveProfile", &err, &reply, "s", payload.c_str());
        if (r >= 0) {
            const char* resp_s = nullptr;
            sd_bus_message_read(reply, "s", &resp_s);
            try {
                auto resp = json::parse(resp_s ? resp_s : "{}");
                std::cout << "profile saved: " << resp.value("id", profile.value("id", "")) << "\n";
            } catch (...) {
                std::cout << "profile saved\n";
            }
            sd_bus_message_unref(reply);
            sd_bus_unref(bus);
            return 0;
        }
        print_dbus_error("profiles save", &err);
        sd_bus_error_free(&err);
        sd_bus_unref(bus);
    }

    // Socket fallback
    auto resp = socket_request("save_profile", profile);
    if (!resp || !resp->value("ok", false)) {
        std::cerr << "profiles save: service unreachable or rejected\n";
        return 1;
    }
    std::cout << "profile saved: " << resp->value("data", json::object()).value("id", "") << "\n";
    return 0;
}

int cmd_profiles_delete(const std::string& id) {
    if (!dbus_profile_void_method("DeleteProfile", id)) {
        std::cerr << "profiles delete: failed (service unreachable)\n";
        return 1;
    }
    std::cout << "profile deleted: " << id << "\n";
    return 0;
}

int cmd_profiles(const std::vector<std::string>& args) {
    if (args.size() < 2) { usage(std::cerr); return 1; }
    const std::string& sub = args[1];
    if (sub == "list") {
        return cmd_profiles_list(has_flag(args, 2, "--json"));
    }
    if (sub == "set") {
        if (args.size() != 3) { usage(std::cerr); return 1; }
        return cmd_profiles_set(args[2]);
    }
    if (sub == "save") {
        return cmd_profiles_save(args, 2);
    }
    if (sub == "delete") {
        if (args.size() != 3) { usage(std::cerr); return 1; }
        return cmd_profiles_delete(args[2]);
    }
    usage(std::cerr);
    return 1;
}

// ---- Fan curve commands ----------------------------------------------------

std::optional<json> dbus_or_socket_get_fan_curves() {
    sd_bus* bus = open_system_bus();
    if (bus) {
        sd_bus_error err = SD_BUS_ERROR_NULL;
        sd_bus_message* reply = nullptr;
        int r = sd_bus_call_method(bus, kServiceName, kServicePath, kServiceName,
                                   "GetFanCurves", &err, &reply, "");
        if (r >= 0) {
            const char* s = nullptr;
            sd_bus_message_read(reply, "s", &s);
            json result;
            try { result = json::parse(s ? s : "{}"); } catch (...) { result = json::object(); }
            sd_bus_message_unref(reply);
            sd_bus_unref(bus);
            return result;
        }
        sd_bus_error_free(&err);
        sd_bus_unref(bus);
    }
    auto resp = socket_request("get_fan_curves", json::object());
    if (!resp || !resp->value("ok", false)) return std::nullopt;
    // data may be a JSON object or a JSON-encoded string
    auto data = resp->value("data", json::object());
    if (data.is_string()) {
        try { return json::parse(data.get<std::string>()); } catch (...) { return std::nullopt; }
    }
    return data;
}

int cmd_fan_curves_list(bool as_json) {
    auto data = dbus_or_socket_get_fan_curves();
    if (!data) {
        std::cerr << "fan-curves: service not reachable (D-Bus or socket)\n";
        return 1;
    }
    const auto& curves = data->value("fan_curves", json::array());
    if (as_json) {
        std::cout << curves.dump(2) << "\n";
        return 0;
    }
    if (curves.empty()) {
        std::cout << "(no fan curves)\n";
        return 0;
    }
    std::cout << "ID                BUILTIN  NAME\n";
    for (const auto& c : curves) {
        std::string id = c.value("id", "");
        bool builtin = c.value("builtin", false);
        std::string name = c.value("name", "");
        char row[256];
        std::snprintf(row, sizeof(row), "%-17s %-8s %s",
                      id.c_str(), builtin ? "yes" : "no", name.c_str());
        std::cout << row << "\n";
        // Show points on next line
        if (c.contains("points")) {
            std::cout << "                  points: ";
            bool first = true;
            for (const auto& pt : c["points"]) {
                if (!first) std::cout << ", ";
                std::cout << pt.value("temp_c", 0) << "C:" << pt.value("speed_pct", 0) << "%";
                first = false;
            }
            std::cout << "\n";
        }
    }
    return 0;
}

int cmd_fan_curves_save(const std::vector<std::string>& args, size_t start) {
    auto id = find_flag(args, start, "--id");
    auto name = find_flag(args, start, "--name");
    auto points_str = find_flag(args, start, "--points");
    if (!id || !name || !points_str) {
        std::cerr << "fan-curves save: --id, --name, and --points are required\n";
        return 1;
    }

    // Parse points: "temp:speed,temp:speed,..."
    json points = json::array();
    std::string pts = *points_str;
    size_t pos = 0;
    while (pos < pts.size()) {
        size_t comma = pts.find(',', pos);
        std::string pair = pts.substr(pos, comma - pos);
        size_t colon = pair.find(':');
        if (colon == std::string::npos) {
            std::cerr << "fan-curves save: invalid point format (use temp:speed)\n";
            return 1;
        }
        try {
            int temp = std::stoi(pair.substr(0, colon));
            int speed = std::stoi(pair.substr(colon + 1));
            points.push_back({{"temp_c", temp}, {"speed_pct", speed}});
        } catch (...) {
            std::cerr << "fan-curves save: invalid point values\n";
            return 1;
        }
        if (comma == std::string::npos) break;
        pos = comma + 1;
    }

    json curve = {{"id", *id}, {"name", *name}, {"points", points}};

    sd_bus* bus = open_system_bus();
    if (bus) {
        sd_bus_error err = SD_BUS_ERROR_NULL;
        sd_bus_message* reply = nullptr;
        std::string payload = curve.dump();
        int r = sd_bus_call_method(bus, kServiceName, kServicePath, kServiceName,
                                   "SaveFanCurve", &err, &reply, "s", payload.c_str());
        if (r >= 0) {
            sd_bus_message_unref(reply);
            sd_bus_unref(bus);
            std::cout << "fan curve saved: " << *id << "\n";
            return 0;
        }
        print_dbus_error("fan-curves save", &err);
        sd_bus_error_free(&err);
        sd_bus_unref(bus);
    }

    auto resp = socket_request("save_fan_curve", curve);
    if (!resp || !resp->value("ok", false)) {
        std::cerr << "fan-curves save: service unreachable or rejected\n";
        return 1;
    }
    std::cout << "fan curve saved: " << *id << "\n";
    return 0;
}

int cmd_fan_curves_delete(const std::string& id) {
    sd_bus* bus = open_system_bus();
    if (bus) {
        sd_bus_error err = SD_BUS_ERROR_NULL;
        sd_bus_message* reply = nullptr;
        int r = sd_bus_call_method(bus, kServiceName, kServicePath, kServiceName,
                                   "DeleteFanCurve", &err, &reply, "s", id.c_str());
        if (r >= 0) {
            sd_bus_message_unref(reply);
            sd_bus_unref(bus);
            std::cout << "fan curve deleted: " << id << "\n";
            return 0;
        }
        print_dbus_error("fan-curves delete", &err);
        sd_bus_error_free(&err);
        sd_bus_unref(bus);
    }

    auto resp = socket_request("delete_fan_curve", json{{"id", id}});
    if (!resp || !resp->value("ok", false)) {
        std::cerr << "fan-curves delete: service unreachable or rejected\n";
        return 1;
    }
    std::cout << "fan curve deleted: " << id << "\n";
    return 0;
}

int cmd_fan_curves(const std::vector<std::string>& args) {
    if (args.size() < 2) { usage(std::cerr); return 1; }
    const std::string& sub = args[1];
    if (sub == "list") {
        return cmd_fan_curves_list(has_flag(args, 2, "--json"));
    }
    if (sub == "save") {
        return cmd_fan_curves_save(args, 2);
    }
    if (sub == "delete") {
        if (args.size() != 3) { usage(std::cerr); return 1; }
        return cmd_fan_curves_delete(args[2]);
    }
    usage(std::cerr);
    return 1;
}

}  // namespace

int main(int argc, char* argv[]) {
    std::vector<std::string> args(argv + 1, argv + argc);
    if (args.empty()) {
        usage(std::cerr);
        return 1;
    }

    const std::string& cmd = args[0];

    if (cmd == "version") {
        return cmd_version();
    }
    if (cmd == "metrics") {
        return cmd_metrics();
    }

    if (cmd == "tdp") {
        if (args.size() != 4) {
            usage(std::cerr);
            return 1;
        }
        try {
            return cmd_tdp(std::stoul(args[1]), std::stoul(args[2]), std::stoul(args[3]));
        } catch (...) {
            std::cerr << "tdp: invalid wattage\n";
            return 1;
        }
    }

    if (cmd == "fan") {
        if (args.size() < 2) {
            usage(std::cerr);
            return 1;
        }
        std::string mode = args[1];
        std::optional<std::string> curve_id;
        if (args.size() >= 3) {
            curve_id = args[2];
        }
        if (mode != "auto" && mode != "curve") {
            std::cerr << "fan: mode must be auto or curve\n";
            return 1;
        }
        return cmd_fan(mode, curve_id);
    }

    if (cmd == "charge-limit") {
        if (args.size() > 2) {
            usage(std::cerr);
            return 1;
        }
        std::optional<uint32_t> percent;
        if (args.size() == 2) {
            try {
                percent = std::stoul(args[1]);
            } catch (...) {
                std::cerr << "charge-limit: invalid percent\n";
                return 1;
            }
        }
        return cmd_charge_limit(percent);
    }

    if (cmd == "auto-start") {
        if (args.size() > 2) {
            usage(std::cerr);
            return 1;
        }
        std::optional<bool> enabled;
        if (args.size() == 2) {
            if (args[1] == "on") {
                enabled = true;
            } else if (args[1] == "off") {
                enabled = false;
            } else {
                std::cerr << "auto-start: expected on or off\n";
                return 1;
            }
        }
        return cmd_auto_start(enabled);
    }

    if (cmd == "profiles") {
        return cmd_profiles(args);
    }

    if (cmd == "fan-curves") {
        return cmd_fan_curves(args);
    }

    usage(std::cerr);
    return 1;
}