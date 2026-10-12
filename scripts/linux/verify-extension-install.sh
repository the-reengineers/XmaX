#!/usr/bin/env bash
# verify-extension-install.sh — install and enable the extension, and validate
# metadata.json. The install/enable steps talk to org.gnome.Shell.Extensions on
# the session bus, so run this from inside the GNOME session, not over SSH / a
# tty.
#
# Two GNOME-specific caveats handled here:
#   1. `gnome-extensions install` wants an extension *bundle* (a
#      .shell-extension.zip), not a source directory, so we `pack` first. The
#      pack tool on this host does not auto-follow the ESM import graph, so the
#      imported modules are listed explicitly via --extra-source — a complete
#      bundle is required or the enabled extension fails to load.
#   2. A freshly-installed local extension is only adopted by the running
#      gnome-shell after a shell restart. enable/info are attempted, but if the
#      shell reports "does not exist", we warn (not fail): the install itself
#      succeeded, and only the enable needs a log out/in (or Alt+F2 -> r).
set -euo pipefail

EXT_DIR="$(cd "$(dirname "$0")/../../frontend/gnome/xmax@local" && pwd)"

echo "== 1. metadata.json parses as JSON =="
python3 - "$EXT_DIR/metadata.json" <<'PY'
import json, sys
meta = json.load(open(sys.argv[1]))
assert meta["uuid"] == "xmax@local", meta["uuid"]
assert "50" in meta["shell-version"], meta["shell-version"]
assert meta["settings-schema"] == "org.xmax.shell"
print(f"   uuid={meta['uuid']} shell-version={meta['shell-version']} schema={meta['settings-schema']}")
PY

echo "== 2. metadata.json has no CR/LF corruption (od sanity) =="
if od -c "$EXT_DIR/metadata.json" | grep -q '\\r'; then
    echo "   ERROR: CR characters present in metadata.json" >&2; exit 1
fi
echo "   clean"

echo "== 3. gnome-extensions pack (source dir -> bundle) =="
BUNDLE_DIR="$(mktemp -d)"
trap 'rm -rf "$BUNDLE_DIR"' EXIT
gnome-extensions pack --force --out-dir="$BUNDLE_DIR" \
    --extra-source=shellCompat.js \
    --extra-source=dbus.js \
    --extra-source=indicator.js \
    --extra-source=quickSettings.js \
    "$EXT_DIR"
BUNDLE="$BUNDLE_DIR/xmax@local.shell-extension.zip"
echo "   bundle: $BUNDLE"

echo "== 4. gnome-extensions install =="
gnome-extensions install --force "$BUNDLE"

echo "== 5. gnome-extensions enable =="
if gnome-extensions enable xmax@local 2>enable.err; then
    echo "   enabled"
else
    if grep -qiE "does not exist|doesn't exist|not installed" enable.err; then
        echo "   [warn] shell has not adopted xmax@local yet (extension was just installed)."
        echo "          Log out/in (or Alt+F2 -> r) and re-run this script; the install itself succeeded."
    else
        cat enable.err >&2
        exit 1
    fi
fi

echo "== 6. gnome-extensions info =="
gnome-extensions info xmax@local || true
if gnome-extensions info xmax@local 2>/dev/null | grep -qiE 'State: ENABLED'; then
    echo "   ENABLED"
else
    echo "   [warn] not reported ENABLED (shell may need a restart to adopt the new extension)."
fi

echo
echo "install verification OK"