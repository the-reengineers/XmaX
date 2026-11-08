// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
#include "ryzenadj.h"

#include <dlfcn.h>

#include <cstdlib>
#include <optional>

namespace ryzenadj {
namespace {

// libryzenadj's C ABI (backend/lib/ryzenadj/lib/ryzenadj.h, v0.19):
//   ryzen_access init_ryzenadj();            -- NULL on failure
//   void  cleanup_ryzenadj(ryzen_access);
//   int   set_stapm_limit(ryzen_access, uint32_t value);   -- 0 = success
//   float get_stapm_limit(ryzen_access ry);  -- current limit in mW, <0 = failure
// Every function takes the opaque ryzen_access handle as its first argument;
// init_ryzenadj returns that handle by value (it is a pointer, NOT an int
// status). Calling the set/get functions without the handle argument
// dereferences whatever lands in its register slot and segfaults.
using init_fn = void* (*)();
using cleanup_fn = void (*)(void*);
using set_fn = int (*)(void*, uint32_t);
using get_fn = float (*)(void*);

struct Library {
    void* handle = nullptr;
    void* access = nullptr;   // ryzen_access from init_ryzenadj()
    cleanup_fn cleanup = nullptr;
    set_fn set_stapm = nullptr;
    set_fn set_fast = nullptr;
    set_fn set_slow = nullptr;
    get_fn get_stapm = nullptr;
    get_fn get_fast = nullptr;
    get_fn get_slow = nullptr;
};

// Load and resolve libryzenadj once. Candidates cover the unversioned
// development symlink plus common distro SONAMEs, and absolute paths for
// prefixes the dynamic loader does not search by default — notably
// /usr/local/lib, which Fedora's ldconfig ignores (it only indexes the
// directories in /etc/ld.so.conf, which do not include /usr/local/lib, unlike
// Debian/Ubuntu). XMAX_RYZENADJ_LIB forces an exact path for non-standard
// installs.
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
            "/usr/local/lib/libryzenadj.so",
            "/usr/local/lib64/libryzenadj.so",
            "/usr/lib/libryzenadj.so",
            "/usr/lib64/libryzenadj.so",
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
        out.cleanup = reinterpret_cast<cleanup_fn>(load("cleanup_ryzenadj"));
        out.set_stapm = reinterpret_cast<set_fn>(load("set_stapm_limit"));
        out.set_fast = reinterpret_cast<set_fn>(load("set_fast_limit"));
        out.set_slow = reinterpret_cast<set_fn>(load("set_slow_limit"));
        out.get_stapm = reinterpret_cast<get_fn>(load("get_stapm_limit"));
        out.get_fast = reinterpret_cast<get_fn>(load("get_fast_limit"));
        out.get_slow = reinterpret_cast<get_fn>(load("get_slow_limit"));

        bool symbols_ok = init && out.set_stapm && out.set_fast && out.set_slow &&
                          out.get_stapm && out.get_fast && out.get_slow;

        // init_ryzenadj returns the opaque access handle (NULL on failure) —
        // keep it, it is the first argument of every other call.
        out.access = symbols_ok ? init() : nullptr;
        if (!out.access) {
            dlclose(out.handle);
            out.handle = nullptr;
            out.cleanup = nullptr;
            out.set_stapm = out.set_fast = out.set_slow = nullptr;
            out.get_stapm = out.get_fast = out.get_slow = nullptr;
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
    return lib.set_stapm && lib.set_stapm(lib.access, mw) == 0;
}

bool set_fast_limit(uint32_t mw) {
    auto& lib = library();
    return lib.set_fast && lib.set_fast(lib.access, mw) == 0;
}

bool set_slow_limit(uint32_t mw) {
    auto& lib = library();
    return lib.set_slow && lib.set_slow(lib.access, mw) == 0;
}

std::optional<uint32_t> get_stapm_limit() {
    auto& lib = library();
    if (!lib.get_stapm) {
        return std::nullopt;
    }
    float v = lib.get_stapm(lib.access);
    return v < 0 ? std::nullopt : std::optional<uint32_t>(static_cast<uint32_t>(v));
}

std::optional<uint32_t> get_fast_limit() {
    auto& lib = library();
    if (!lib.get_fast) {
        return std::nullopt;
    }
    float v = lib.get_fast(lib.access);
    return v < 0 ? std::nullopt : std::optional<uint32_t>(static_cast<uint32_t>(v));
}

std::optional<uint32_t> get_slow_limit() {
    auto& lib = library();
    if (!lib.get_slow) {
        return std::nullopt;
    }
    float v = lib.get_slow(lib.access);
    return v < 0 ? std::nullopt : std::optional<uint32_t>(static_cast<uint32_t>(v));
}

}  // namespace ryzenadj
