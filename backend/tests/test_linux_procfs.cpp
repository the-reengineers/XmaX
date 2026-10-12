// Fixture-driven tests for the Linux sysfs + /proc parsers. Hermetic: they
// read committed files under tests/fixtures/ (path injected by CMake), so no
// real hardware or /proc access is needed.

#include <gtest/gtest.h>

#include <string>

#include "../src/platform/linux/procfs.h"

// Absolute path to the fixture tree, injected as a compile definition by
// CMake. The fallback only keeps a plain gtest invocation working when run
// from the backend/build-linux directory.
#ifndef XMAX_FIXTURES_DIR
#define XMAX_FIXTURES_DIR "../tests/fixtures"
#endif

namespace {

std::string fx(const std::string& rel) {
    return std::string(XMAX_FIXTURES_DIR) + "/" + rel;
}

// ---- /proc/stat ----

TEST(ProcfsCpuTimes, ParsesIdleAndTotal) {
    // fixture: cpu  user nice system idle iowait irq softirq steal guest guest_nice
    // idle = idle(600) + iowait(100) = 700; total = sum = 1000.
    auto t = procfs::read_cpu_times(fx("proc_stat.txt"));
    ASSERT_TRUE(t.has_value());
    EXPECT_EQ(t->idle, 700ull);
    EXPECT_EQ(t->total, 1000ull);
}

TEST(ProcfsCpuTimes, MissingFileReturnsNullopt) {
    EXPECT_FALSE(procfs::read_cpu_times(fx("does_not_exist.txt")).has_value());
}

TEST(ProcfsCpuTimes, MalformedLineReturnsNullopt) {
    EXPECT_FALSE(procfs::read_cpu_times(fx("proc_stat_malformed.txt")).has_value());
}

TEST(ProcfsCpuUtil, FirstSampleIsZero) {
    procfs::CpuUtil util;
    EXPECT_DOUBLE_EQ(util.sample(fx("proc_stat.txt")), 0.0);
}

TEST(ProcfsCpuUtil, DeltaComputesUtilization) {
    // B vs A: +50 idle, +50 user => total +100, idle +50 => busy +50 => 50%.
    procfs::CpuUtil util;
    util.sample(fx("proc_stat.txt"));
    EXPECT_NEAR(util.sample(fx("proc_stat_b.txt")), 50.0, 0.001);
}

// ---- /proc/meminfo ----

TEST(ProcfsMeminfo, ParsesValues) {
    // total=1000kB, avail=700kB, free=400kB => used=300kB, load=30%.
    auto m = procfs::read_meminfo(fx("proc_meminfo.txt"));
    ASSERT_TRUE(m.has_value());
    EXPECT_EQ(m->total_bytes, 1000ull * 1024ull);
    EXPECT_EQ(m->avail_bytes, 700ull * 1024ull);
    EXPECT_EQ(m->free_bytes, 400ull * 1024ull);
    EXPECT_EQ(m->used_bytes, 300ull * 1024ull);
    EXPECT_DOUBLE_EQ(m->load_pct, 30.0);
}

TEST(ProcfsMeminfo, MissingFileReturnsNullopt) {
    EXPECT_FALSE(procfs::read_meminfo(fx("does_not_exist.txt")).has_value());
}

// ---- cpufreq ----

TEST(ProcfsCpuClock, ConvertsKhzToMhz) {
    auto mhz = procfs::cpu_clock_mhz(fx("scaling_cur_freq.txt"));
    ASSERT_TRUE(mhz.has_value());
    EXPECT_EQ(*mhz, 2900u);
}

TEST(ProcfsCpuClock, MissingFileReturnsNullopt) {
    EXPECT_FALSE(procfs::cpu_clock_mhz(fx("does_not_exist.txt")).has_value());
}

// ---- hwmon: read_long / read_temp_c ----

TEST(ProcfsReadLong, ParsesInteger) {
    auto v = procfs::read_long(fx("scaling_cur_freq.txt"));
    ASSERT_TRUE(v.has_value());
    EXPECT_EQ(*v, 2900453ll);
}

TEST(ProcfsReadLong, MissingFileReturnsNullopt) {
    EXPECT_FALSE(procfs::read_long(fx("does_not_exist.txt")).has_value());
}

TEST(ProcfsTemp, K10tempScansHwmon) {
    // hwmon1 is "k10temp" with temp1_input = 47750 millidegrees = 47 C.
    auto c = procfs::cpu_temp_c(fx("hwmon"));
    ASSERT_TRUE(c.has_value());
    EXPECT_EQ(*c, 47);
}

TEST(ProcfsTemp, MissingChipReturnsNullopt) {
    EXPECT_FALSE(procfs::cpu_temp_c(fx("does_not_exist")).has_value());
}

// ---- power_supply ----

TEST(ProcfsBattery, ReadsCapacity) {
    auto p = procfs::battery_pct(fx("power_supply"));
    ASSERT_TRUE(p.has_value());
    EXPECT_EQ(*p, 91);
}

TEST(ProcfsAc, ReadsOnline) {
    auto on = procfs::ac_online(fx("power_supply"));
    ASSERT_TRUE(on.has_value());
    EXPECT_TRUE(*on);
}

// ---- amdgpu ----

TEST(ProcfsGpu, ReadsAmdgpuTelemetry) {
    // hwmon0 = amdgpu; drm/card1/device/gpu_busy_percent = 0.
    auto g = procfs::gpu_info(fx("hwmon"), fx("drm"));
    ASSERT_TRUE(g.found);

    ASSERT_TRUE(g.temp_c.has_value());
    EXPECT_EQ(*g.temp_c, 45);
    ASSERT_TRUE(g.power_w.has_value());
    EXPECT_NEAR(*g.power_w, 13.085, 0.001);
    ASSERT_TRUE(g.clock_mhz.has_value());
    EXPECT_EQ(*g.clock_mhz, 609u);
    ASSERT_TRUE(g.util_pct.has_value());
    EXPECT_DOUBLE_EQ(*g.util_pct, 0.0);

    ASSERT_TRUE(g.vram_used_bytes.has_value());
    EXPECT_EQ(*g.vram_used_bytes, 5368709120ull);
    ASSERT_TRUE(g.vram_total_bytes.has_value());
    EXPECT_EQ(*g.vram_total_bytes, 8589934592ull);
}

TEST(ProcfsGpu, MissingAmdgpuNotFound) {
    auto g = procfs::gpu_info(fx("does_not_exist"), fx("does_not_exist"));
    EXPECT_FALSE(g.found);
}

}  // namespace