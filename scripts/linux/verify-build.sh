#!/usr/bin/env bash
# Verify Step 0: the Linux backend (xmaxd) and unit tests build and pass.
# Uses a dedicated build dir (backend/build-linux) so the existing Windows
# Visual Studio build dir (backend/build) is left untouched.

set -euo pipefail

# Resolve repo root as the parent of the scripts/ dir.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
BACKEND_SRC="${REPO_ROOT}/backend"
BUILD_DIR="${BACKEND_SRC}/build-linux"

echo "==> Repo root: ${REPO_ROOT}"
echo "==> Build dir: ${BUILD_DIR}"

# Reuse FetchContent sources already downloaded by a prior build (avoids
# re-hitting the network).
FETCH_OVERRIDE=()
if [ -d "${BACKEND_SRC}/build/_deps/json-src" ]; then
    FETCH_OVERRIDE+=("-DFETCHCONTENT_SOURCE_DIR_JSON=${BACKEND_SRC}/build/_deps/json-src")
fi
if [ -d "${BACKEND_SRC}/build/_deps/googletest-src" ]; then
    FETCH_OVERRIDE+=("-DFETCHCONTENT_SOURCE_DIR_GOOGLETEST=${BACKEND_SRC}/build/_deps/googletest-src")
fi

echo "==> Configuring (ninja)..."
cmake -S "${BACKEND_SRC}" -B "${BUILD_DIR}" -G Ninja "${FETCH_OVERRIDE[@]}"

echo "==> Building..."
cmake --build "${BUILD_DIR}"

echo "==> Running tests..."
ctest --test-dir "${BUILD_DIR}" --output-on-failure

echo "==> OK: xmaxd built and all tests passed."