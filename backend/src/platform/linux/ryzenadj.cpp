#include "ryzenadj.h"

#include <dlfcn.h>

#include <cstdlib>

namespace ryzenadj {
namespace {

// libryzenadj.so's C ABI (https://github.com/FlyGoat/ryzenadj).
using set_fn = int (*)(uint32_t);  // 0 = success, non-zero = failure
using get_fn = int (*)();          // current limit in mW, negative = failure
using init_fn = int (*)();         // 0 = success

struct Library {
    void* handle = nullptr;
    set_fn set_stapm = nullptr;
    set_fn set_fast = nullptr;
    set_fn set_slow = nullptr;
    get_fn get_stapm = nullptr;
    get_fn get_fast = nullptr;
    get_fn get_slow = nullptr;
};

// Load and resolve libryzenadj once. Candidates cover the unversioned
// development symlink plus common distro SONAMEs; XMAX_RYZENADJ_LIB forces an
// exact path for non-standard installs.
Library& library() {
    static Library lib = [] {
        Library out;

        const char* override_path = std::getenv("XMAX_RYZENADJ_LIB");
        static constexpr const char* kCandidates[] = {
            "libryzenadj.so",
            "libryzenadj.so.6",
            "libryzenadj.so.5",
            "libryzenadj.so.4",
            "libryzenadj.so.1",
        };

        if (override_path && *override_path) {
            out.handle = dlopen(override_path, RTLD_LAZY | RTLD_LOCAL);
        } else {
            for (const char* name : kCandidates) {
                out.handle = dlopen(name, RTLD_LAZY | RTLD_LOCAL);
                if (out.handle) {
                    break;
                }
            }
        }
        if (!out.handle) {
            return out;
        }

        auto load = [&](const char* sym) -> void* { return dlsym(out.handle, sym); };
        auto* init = reinterpret_cast<init_fn>(load("init_ryzenadj"));
        out.set_stapm = reinterpret_cast<set_fn>(load("set_stapm_limit"));
        out.set_fast = reinterpret_cast<set_fn>(load("set_fast_limit"));
        out.set_slow = reinterpret_cast<set_fn>(load("set_slow_limit"));
        out.get_stapm = reinterpret_cast<get_fn>(load("get_stapm_limit"));
        out.get_fast = reinterpret_cast<get_fn>(load("get_fast_limit"));
        out.get_slow = reinterpret_cast<get_fn>(load("get_slow_limit"));

        bool usable = init && out.set_stapm && out.set_fast && out.set_slow &&
                      out.get_stapm && out.get_fast && out.get_slow;
        if (!usable || init() != 0) {
            dlclose(out.handle);
            out.handle = nullptr;
        }
        return out;
    }();
    return lib;
}

}  // namespace

bool init() {
    return library().handle != nullptr;
}

bool set_stapm_limit(uint32_t mw) {
    auto& lib = library();
    return lib.set_stapm && lib.set_stapm(mw) == 0;
}

bool set_fast_limit(uint32_t mw) {
    auto& lib = library();
    return lib.set_fast && lib.set_fast(mw) == 0;
}

bool set_slow_limit(uint32_t mw) {
    auto& lib = library();
    return lib.set_slow && lib.set_slow(mw) == 0;
}

std::optional<uint32_t> get_stapm_limit() {
    auto& lib = library();
    if (!lib.get_stapm) {
        return std::nullopt;
    }
    int v = lib.get_stapm();
    return v < 0 ? std::nullopt : std::optional<uint32_t>(static_cast<uint32_t>(v));
}

std::optional<uint32_t> get_fast_limit() {
    auto& lib = library();
    if (!lib.get_fast) {
        return std::nullopt;
    }
    int v = lib.get_fast();
    return v < 0 ? std::nullopt : std::optional<uint32_t>(static_cast<uint32_t>(v));
}

std::optional<uint32_t> get_slow_limit() {
    auto& lib = library();
    if (!lib.get_slow) {
        return std::nullopt;
    }
    int v = lib.get_slow();
    return v < 0 ? std::nullopt : std::optional<uint32_t>(static_cast<uint32_t>(v));
}

}  // namespace ryzenadj