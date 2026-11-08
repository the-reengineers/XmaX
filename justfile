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
# One build dir shared across OSes silently breaks when a Windows CMakeCache
# (which bakes in absolute paths like d:/…) is re-read on Linux. Split per OS;
# this matches scripts/linux/verify-build.sh, which builds into backend/build-linux.
_be_build := if _os == "windows" { "backend/build" } else { "backend/build-linux" }
# Multi-config generators (Visual Studio) nest binaries under <config>/;
# single-config (Make/Ninja) drop them at the build-dir root.
_be_out := if _os == "windows" { _be_build + "/" + config } else { _be_build }
_fe_src := "frontend/windows"
_fe_build := _fe_src + "/bin/" + config + "/net10.0-windows10.0.26100.0/" + _fe_rid + "/publish"

# Unified output directory (copy target for testing/running)
_out := "out/" + config

# Default recipe
default:
    @just --list

# Generate assets (icons, etc.) from source files
generate-assets:
    python scripts/generate_icon.py shared/assets/logo.png backend/logo.ico
    python scripts/generate_icon.py shared/assets/logo.png frontend/windows/Assets/logo.ico
    python scripts/generate_locales.py
    {{ if _os == "windows" { "Copy-Item -Path 'frontend/assets/tabler-icons-300.ttf' -Destination 'frontend/windows/Assets/' -Force" } else { "cp frontend/assets/tabler-icons-300.ttf frontend/windows/Assets/" } }}

# Build backend
build-be: generate-assets
    cmake -B "{{_be_build}}" "{{_be_src}}"
    cmake --build "{{_be_build}}" --config {{config}}

# Build frontend (Windows-only; the Linux UI is the GNOME extension)
build-fe: generate-assets
    {{ if _os == "windows" { "dotnet publish " + _fe_src + "/xmax.csproj -c " + config + " -r " + _fe_rid } else { "echo '  (no Linux frontend build: the GNOME extension is prebuilt JS)'" } }}

# Build both
build: build-be build-fe

# Copy both builds into out/<config>/ for unified testing
copy: build
    {{ if _os == "windows" { "New-Item -ItemType Directory -Path '" + _out + "' -Force | Out-Null; Copy-Item -Path '" + _be_out + "\\*' -Destination '" + _out + "' -Recurse -Force; Copy-Item -Path '" + _fe_build + "\\*' -Destination '" + _out + "' -Recurse -Force" } else { "mkdir -p " + _out + " && cp -v " + _be_out + "/xmaxd " + _out + "/ && cp -v " + _be_out + "/xmaxctl " + _out + "/" } }}

# Run backend unit tests
test-be: build-be
    ctest --test-dir "{{_be_build}}" --build-config {{config}} --output-on-failure

# Run frontend unit tests (Windows-only)
test-fe:
    {{ if _os == "windows" { "dotnet test " + _fe_src + "/xmax.Tests/xmax.Tests.csproj -c " + config } else { "echo '  (no Windows frontend tests on Linux; skipping)'" } }}

# Run all tests
test: test-be test-fe

# Start backend only
run-be: copy
    {{ if _os == "windows" { "& " } else { "" } }}"{{_out}}/{{_be_name}}"

# Start frontend only (Windows-only: the Linux UI is the GNOME extension)
run-fe: copy
    {{ if _os == "windows" { "& " + _out + "/" + _fe_name } else { "echo '  (no Linux frontend binary: the GNOME Shell extension provides the UI)'" } }}

# Reinstall the GNOME extension from source (FE-only, no daemon rebuild)
reinstall-fe:
    scripts/linux/reinstall-extension.sh

# Install the vendored ryzen_smu kernel module via DKMS (root, one-time; see
# LINUX.md "Runtime prerequisites"). Builds + signs locally; on Secure Boot
# machines it imports the MOK key and asks for a one-time enrollment password
# (reboot required once — never again after that).
install-smu:
    sudo scripts/linux/install-smu-driver.sh

# Install the vendored xmax_sio_ec kernel module via DKMS (root, one-time).
# Provides the IT87 Super I/O EC RAM bypass for charge-limit writes on the
# ONEXPLAYER SUPER X. Same Secure Boot / MOK enrollment flow as install-smu.
install-sio:
    sudo scripts/linux/install-sio-driver.sh

# Dev mode: build both, copy to out/, start backend (which spawns the frontend). Ctrl+C stops both.
dev: copy
    @echo "Starting {{_be_name}} ({{config}}) — frontend spawned automatically"
    @echo "Press Ctrl+C to stop both"
    {{ if _os == "windows" { "& " } else { "" } }}"{{_out}}/{{_be_name}}" --debug

# Rebuild daemon, reinstall the GNOME extension, and open a nested GNOME Shell for live FE testing
dev-gui: build-be
    scripts/linux/reinstall-extension.sh
    scripts/linux/run-extension-nested.sh

# Verify the extension's D-Bus client (the real dbus.js GNOME Shell imports)
# against the live daemon: ping, metrics, profiles. No shell reload needed —
# run after `just dev-sys` to confirm the FE wiring before/after re-login.
verify-fe:
    gjs -m scripts/linux/extension-client-check.mjs frontend/gnome/xmax@local

# System-bus dev install (root): build daemon, stop the user-session unit, install
# D-Bus policy + polkit + the system unit, reinstall the extension. The daemon runs
# as root on the *system* bus — the production wiring pointed at the dev binary — so
# the extension connects with no XMAX_DBUS_BUS env var (its default). nopolkit=true
# (default) skips polkit auth on SetTdp/SetFan/SetChargeLimit for dev parity with
# the session-bus path; pass nopolkit=false to keep real auth_admin prompts.
# Reversed by `just uninstall`. Summary: root system-bus daemon on the dev binary.
dev-sys nopolkit="true": build-be
    @echo "==> stopping any user-session xmaxd (avoids two daemons owning the name)"
    -systemctl --user stop xmaxd.service 2>/dev/null
    @echo "==> installing system-bus daemon + D-Bus policy + polkit + unit (root)"
    sudo scripts/linux/verify-dbus.sh
    @echo "==> reinstalling GNOME extension"
    scripts/linux/reinstall-extension.sh
    @echo "==> polkit (nopolkit={{nopolkit}})"
    if [ "{{nopolkit}}" = "true" ]; then \
        sudo mkdir -p /etc/systemd/system/xmaxd.service.d; \
        printf '[Service]\nEnvironment=XMAX_NO_POLKIT=1\n' | sudo tee /etc/systemd/system/xmaxd.service.d/nopolkit.conf >/dev/null; \
        echo "  XMAX_NO_POLKIT=1 drop-in installed — privileged methods skip auth (dev parity)"; \
    else \
        sudo rm -f /etc/systemd/system/xmaxd.service.d/nopolkit.conf; \
        echo "  polkit active — SetTdp/SetFan/SetChargeLimit will prompt for admin (auth_admin)"; \
    fi
    sudo systemctl daemon-reload && sudo systemctl restart xmaxd
    @echo
    @echo "==> extension client check (same dbus.js the shell will import)"
    gjs -m scripts/linux/extension-client-check.mjs frontend/gnome/xmax@local
    @echo
    @echo "system-bus xmaxd running — check:  systemctl status xmaxd  ;  journalctl -u xmaxd -f"
    @echo "log out/in (Wayland) to load the extension into the shell, then:"
    @echo "  just verify-fe      # re-assert the client path any time"

# Run without rebuilding (copies existing build outputs to out/). Errors if outputs are missing.
run:
    {{ if _os == "windows" { "if (-not (Test-Path '" + _be_out + "')) { Write-Host 'Error: Backend build not found. Run `just build` first.' -ForegroundColor Red; exit 1 }; if (-not (Test-Path '" + _fe_build + "')) { Write-Host 'Error: Frontend build not found. Run `just build` first.' -ForegroundColor Red; exit 1 }; New-Item -ItemType Directory -Path '" + _out + "' -Force | Out-Null; Copy-Item -Path '" + _be_out + "\\*' -Destination '" + _out + "' -Recurse -Force; Copy-Item -Path '" + _fe_build + "\\*' -Destination '" + _out + "' -Recurse -Force" } else { "if [ ! -d '" + _be_out + "' ]; then echo 'Error: Backend build not found. Run `just build` first.' >&2; exit 1; fi; mkdir -p " + _out + " && cp -v " + _be_out + "/xmaxd " + _out + "/ && cp -v " + _be_out + "/xmaxctl " + _out + "/" } }}
    @echo "Starting {{_be_name}} ({{config}}) — frontend spawned automatically"
    @echo "Press Ctrl+C to stop both"
    {{ if _os == "windows" { "& " } else { "" } }}"{{_out}}/{{_be_name}}" --debug

# Clean build artifacts
clean:
    -cmake --build "{{_be_build}}" --target clean
    -dotnet clean "{{_fe_src}}/xmax.csproj"
    {{ if _os == "windows" { "Remove-Item -Path 'out' -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item -Path 'backend/logo.ico' -Force -ErrorAction SilentlyContinue; Remove-Item -Path 'frontend/windows/Assets/logo.ico' -Force -ErrorAction SilentlyContinue" } else { "rm -rf out backend/logo.ico frontend/windows/Assets/logo.ico" } }}

# Reverse the Linux dev (source) install: system files (root) + GNOME extension
# (per-user). Local build artifacts are reverted separately by `just clean`.
uninstall:
    sudo scripts/linux/uninstall.sh
    gnome-extensions uninstall xmax@local || true

# Rebuild from scratch
rebuild: clean build

# Release build + test
release:
    just test config=Release
