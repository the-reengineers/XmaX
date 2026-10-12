#!/usr/bin/env bash
# build-ryzenadj.sh — compile libryzenadj.so from the vendored RyzenAdj source
# (backend/lib/ryzenadj/) and install it where xmaxd's dlopen binding can load it.
#
# ryzenadj is a *runtime* prerequisite of xmaxd, not a build one: xmaxd links
# nothing from it and instead dlopen's libryzenadj.so at startup (see
# backend/src/platform/linux/ryzenadj.cpp). This script is therefore a separate,
# opt-in step — run it once to install the library.
#
# Build deps: cmake, ninja, gcc, pkg-config, and libpci (Fedora/RHEL:
# pciutils-devel; Debian/Ubuntu: libpci-dev). Reading/adjusting TDP additionally
# needs root + the appropriate SMU access at runtime, not here.
#
# Usage:
#   sudo ./build-ryzenadj.sh                  # install to /usr/local/{lib,bin}
#   PREFIX="$HOME/.local" ./build-ryzenadj.sh # install to a user prefix
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

SRC="${REPO_ROOT}/backend/lib/ryzenadj"
BUILD_DIR="${REPO_ROOT}/backend/build-linux/ryzenadj"   # already gitignored
PREFIX="${PREFIX:-/usr/local}"
LIBDIR="${PREFIX}/lib"

need() { command -v "$1" >/dev/null 2>&1 || { echo "error: $1 not found in PATH" >&2; exit 1; }; }

need cmake
need gcc
pkg-config --exists libpci || {
    echo "error: libpci not found via pkg-config. Install it first:" >&2
    echo "  Fedora/RHEL:   sudo dnf install pciutils-devel" >&2
    echo "  Debian/Ubuntu: sudo apt install libpci-dev" >&2
    exit 1
}

echo "==> Configuring ryzenadj (vendored v0.19.0) ..."
cmake -S "$SRC" -B "$BUILD_DIR" -G Ninja \
    -DBUILD_SHARED_LIBS=ON -DCMAKE_BUILD_TYPE=Release

echo "==> Building libryzenadj ..."
cmake --build "$BUILD_DIR" --target libryzenadj

# Upstream only installs the CLI (ryzenadj), not the library — install the lib
# ourselves, plus the CLI for hands-on testing (check-ryzenadj.sh detects both).
echo "==> Installing libryzenadj.so + ryzenadj -> ${PREFIX} ..."
install -d "$LIBDIR" "$PREFIX/bin"
install -m 0644 "$BUILD_DIR/libryzenadj.so" "$LIBDIR/libryzenadj.so"
cmake --build "$BUILD_DIR" --target ryzenadj
install -m 0755 "$BUILD_DIR/ryzenadj" "$PREFIX/bin/ryzenadj"

if [ "$(id -u)" -eq 0 ]; then
    ldconfig
    echo "installed: $LIBDIR/libryzenadj.so (dlopen will find it via ldconfig)"
else
    echo "installed: $LIBDIR/libryzenadj.so (user prefix; no ldconfig)"
    echo "hint: export XMAX_RYZENADJ_LIB=$LIBDIR/libryzenadj.so"
fi