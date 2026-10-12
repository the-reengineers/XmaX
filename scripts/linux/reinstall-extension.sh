#!/usr/bin/env bash
# reinstall-extension.sh — symlink the extension source into the GNOME Shell
# extensions directory so changes are picked up immediately (no re-pack needed).
# Also compiles GSettings schemas. Run from inside a GNOME session.
set -euo pipefail

SRC_DIR="$(cd "$(dirname "$0")/../../frontend/gnome/xmax@local" && pwd)"
INSTALL_DIR="$HOME/.local/share/gnome-shell/extensions/xmax@local"

if ! command -v gnome-extensions >/dev/null 2>&1; then
    echo "gnome-extensions not found — is gnome-shell installed?" >&2
    exit 1
fi

# Compile schemas first (required for GSettings to work).
echo "compiling schemas …"
glib-compile-schemas "$SRC_DIR/schemas/"

# Remove any existing install (real directory or stale symlink).
if [ -e "$INSTALL_DIR" ] || [ -L "$INSTALL_DIR" ]; then
    echo "removing existing install at $INSTALL_DIR …"
    rm -rf "$INSTALL_DIR"
fi

# Create a symlink to the source tree.
echo "linking $INSTALL_DIR → $SRC_DIR …"
ln -s "$SRC_DIR" "$INSTALL_DIR"

# Ensure xmax@local is in the enabled-extensions dconf list so a fresh shell
# auto-enables it.
python3 - <<'PY'
import re, subprocess
out = subprocess.check_output(
    ["gsettings", "get", "org.gnome.shell", "enabled-extensions"], text=True)
vals = [m for m in re.findall(r"'([^']*)'", out) if m]
if "xmax@local" in vals:
    print("xmax@local already in enabled-extensions")
else:
    vals.append("xmax@local")
    value = "[" + ", ".join("'%s'" % v for v in vals) + "]"
    subprocess.check_call(
        ["gsettings", "set", "org.gnome.shell", "enabled-extensions", value])
    print("enabled xmax@local: " + value)
PY

echo "reinstalled xmax@local via symlink (changes are immediate)."
