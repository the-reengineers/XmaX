#include "ec.h"

#include <chrono>
#include <thread>

#include <fcntl.h>
#include <sys/types.h>
#include <unistd.h>

// Raw port I/O (iopl/inb/outb) is x86-only; on other architectures the
// Super-I/O fallback is compiled out and only the ec_sys path remains.
#if defined(__x86_64__) || defined(__i386__)
#include <sys/io.h>
#define XMAX_HAVE_PORT_IO 1
#else
#define XMAX_HAVE_PORT_IO 0
#endif

namespace xmax_ec {
namespace {

enum class Backend { None, EcSys, SuperIo };

struct State {
    Backend backend = Backend::None;
    bool probed = false;
    int fd = -1;  // ec_sys debugfs char node
};

State g_state;

constexpr const char* kEcSysIoPath = "/sys/kernel/debug/ec/ec0/io";

// Standard ACPI embedded-controller command/data ports.
constexpr unsigned short kDataPort = 0x62;
constexpr unsigned short kCmdPort = 0x66;

constexpr uint8_t kEcReadCmd = 0x80;   // read one byte from register
constexpr uint8_t kEcWriteCmd = 0x81;  // write one byte to register

constexpr uint8_t kStatusObf = 0x01;  // output buffer full: data ready at 0x62
constexpr uint8_t kStatusIbf = 0x02;  // input buffer full: 0x62/0x66 busy

#if XMAX_HAVE_PORT_IO
// Poll the EC status register for a condition, with a bounded timeout. Returns
// false if the EC never signals readiness (absent/unresponsive controller).
bool wait_status(uint8_t mask, bool want_set) {
    constexpr int kTries = 1000;
    for (int i = 0; i < kTries; ++i) {
        uint8_t status = inb(kCmdPort);
        if (((status & mask) != 0) == want_set) {
            return true;
        }
        std::this_thread::sleep_for(std::chrono::microseconds(50));
    }
    return false;
}

std::optional<uint8_t> superio_read(uint8_t reg_idx) {
    if (!wait_status(kStatusIbf, false)) return std::nullopt;
    outb(kEcReadCmd, kCmdPort);
    if (!wait_status(kStatusIbf, false)) return std::nullopt;
    outb(reg_idx, kDataPort);
    if (!wait_status(kStatusObf, true)) return std::nullopt;
    return inb(kDataPort);
}

bool superio_write(uint8_t reg_idx, uint8_t value) {
    if (!wait_status(kStatusIbf, false)) return false;
    outb(kEcWriteCmd, kCmdPort);
    if (!wait_status(kStatusIbf, false)) return false;
    outb(reg_idx, kDataPort);
    if (!wait_status(kStatusIbf, false)) return false;
    outb(value, kDataPort);
    return true;
}
#endif  // XMAX_HAVE_PORT_IO

}  // namespace

void init() {
    if (g_state.probed) {
        return;
    }
    g_state.probed = true;

    // Primary: ec_sys debugfs char node. The register index is the byte offset;
    // pread/pwrite do seek+read atomically so a single fd suffices.
    int fd = ::open(kEcSysIoPath, O_RDWR);
    if (fd >= 0) {
        g_state.backend = Backend::EcSys;
        g_state.fd = fd;
        return;
    }

#if XMAX_HAVE_PORT_IO
    // Fallback: raw EC command/data port I/O (requires CAP_SYS_RAWIO).
    if (iopl(3) == 0) {
        g_state.backend = Backend::SuperIo;
    }
#endif
}

bool available() {
    if (!g_state.probed) {
        init();
    }
    return g_state.backend != Backend::None;
}

std::optional<uint8_t> read(uint16_t reg) {
    if (!g_state.probed) {
        init();
    }
    switch (g_state.backend) {
        case Backend::EcSys: {
            uint8_t byte = 0;
            ssize_t n = ::pread(g_state.fd, &byte, 1, static_cast<off_t>(reg));
            return n == 1 ? std::optional<uint8_t>(byte) : std::nullopt;
        }
#if XMAX_HAVE_PORT_IO
        case Backend::SuperIo:
            // The ACPI EC exposes a 256-byte space; pass the low index byte.
            // 16-bit vendor registers (0x0230-0x04FE) require the ec_sys path
            // or a vendor bank-select not implemented here -- see check-ec.sh.
            return superio_read(static_cast<uint8_t>(reg & 0xFF));
#endif
        default:
            return std::nullopt;
    }
}

bool write(uint16_t reg, uint8_t value) {
    if (!g_state.probed) {
        init();
    }
    switch (g_state.backend) {
        case Backend::EcSys: {
            ssize_t n = ::pwrite(g_state.fd, &value, 1, static_cast<off_t>(reg));
            return n == 1;
        }
#if XMAX_HAVE_PORT_IO
        case Backend::SuperIo:
            return superio_write(static_cast<uint8_t>(reg & 0xFF), value);
#endif
        default:
            return false;
    }
}

}  // namespace xmax_ec