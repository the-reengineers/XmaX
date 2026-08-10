# XmaX build/test/run tasks
# Install just: https://github.com/casey/just#installation
set unstable
set lists
set shell := ["bash", "-cu"]
set windows-shell := ["powershell.exe", "-NoProfile", "-Command"]

# Build config (override with: just build config=Release)
config := "Debug"

# Platform
_os := os()

# Binary names per platform
_be_name := if _os == "windows" { "xmaxsvc.exe" } else { "xmaxd" }
_fe_name := if _os == "windows" { "XmaX.exe" } else { "xmax" }
_fe_rid := if _os == "windows" { "win-x64" } else { "linux-x64" }

# Directories
_be_src := "backend"
_be_build := "backend/build"
_be_out := _be_build + "/" + config
_fe_src := "frontend/windows"
_fe_publish := _fe_src + "/bin/" + config + "/net10.0-windows10.0.19041.0/" + _fe_rid + "/publish"

# Unified output directory (copy target for testing/running)
_out := "out/" + config

# Default recipe
default:
    @just --list

# Generate assets (icons, etc.) from source files
generate-assets:
    python scripts/generate_icon.py shared/assets/logo.png backend/logo.ico
    python scripts/generate_icon.py shared/assets/logo.png frontend/windows/Assets/logo.ico

# Build backend
build-be: generate-assets
    cmake -B "{{_be_build}}" "{{_be_src}}"
    cmake --build "{{_be_build}}" --config {{config}}

# Build frontend
build-fe: generate-assets
    dotnet publish "{{_fe_src}}/xmax.csproj" -c {{config}} -r {{_fe_rid}}

# Build both
build: build-be build-fe

# Copy both builds into out/<config>/ for unified testing
copy: build
    {{ if _os == "windows" { "New-Item -ItemType Directory -Path '" + _out + "' -Force | Out-Null; Copy-Item -Path '" + _be_out + "/*' -Destination '" + _out + "/' -Recurse -Force; Copy-Item -Path '" + _fe_publish + "/*' -Destination '" + _out + "/' -Recurse -Force" } else { "mkdir -p " + _out + " && cp -rv " + _be_out + "/* " + _out + "/ && cp -rv " + _fe_publish + "/* " + _out + "/" } }}

# Run backend unit tests
test-be: build-be
    ctest --test-dir "{{_be_build}}" --build-config {{config}} --output-on-failure

# Run frontend unit tests
test-fe:
    dotnet test "{{_fe_src}}/xmax.Tests/xmax.Tests.csproj" -c {{config}}

# Run all tests
test: test-be test-fe

# Start backend only
run-be: copy
    {{ if _os == "windows" { "& " } else { "" } }}"{{_out}}/{{_be_name}}"

# Start frontend only (requires backend already running)
run-fe: copy
    {{ if _os == "windows" { "& " } else { "" } }}"{{_out}}/{{_fe_name}}"

# Dev mode: build both, copy to out/, start backend (which spawns the frontend). Ctrl+C stops both.
dev: copy
    @echo "Starting {{_be_name}} ({{config}}) — frontend spawned automatically"
    @echo "Press Ctrl+C to stop both"
    {{ if _os == "windows" { "& " } else { "" } }}"{{_out}}/{{_be_name}}"

# Clean build artifacts
clean:
    -cmake --build "{{_be_build}}" --target clean
    -dotnet clean "{{_fe_src}}/xmax.csproj"
    {{ if _os == "windows" { "Remove-Item -Path 'out' -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item -Path 'backend/logo.ico' -Force -ErrorAction SilentlyContinue; Remove-Item -Path 'frontend/windows/Assets/logo.ico' -Force -ErrorAction SilentlyContinue" } else { "rm -rf out backend/logo.ico frontend/windows/Assets/logo.ico" } }}

# Rebuild from scratch
rebuild: clean build

# Release build + test
release:
    just test config=Release
