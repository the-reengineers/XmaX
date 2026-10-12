#pragma once

#include <cstdint>
#include <optional>

// Thin dlopen() binding to libryzenadj.so (no link-time dependency). The
// backend needs only the STAPM/Fast/Slow TDP limit operations; all values are
// milliwatts, matching the SMU PM-table units that TdpController already
// converts to/from watts. When the library is absent (or a symbol is missing),
// every call degrades to false/nullopt so the Platform layer reports
// SensorUnavailable instead of crashing.
namespace ryzenadj {

// Probe and initialize libryzenadj (idempotent). Returns false if the library
// is absent, a required symbol is missing, or init_ryzenadj() failed.
bool init();

// Setters return false on failure; getters return nullopt on failure/unavailable.
// All values are milliwatts.
bool set_stapm_limit(uint32_t mw);
bool set_fast_limit(uint32_t mw);
bool set_slow_limit(uint32_t mw);

std::optional<uint32_t> get_stapm_limit();
std::optional<uint32_t> get_fast_limit();
std::optional<uint32_t> get_slow_limit();

}  // namespace ryzenadj