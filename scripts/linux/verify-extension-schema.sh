#!/usr/bin/env bash
# verify-extension-schema.sh — validate the org.xmax.shell GSettings schema and
# confirm the extension's settings keys round-trip. Runs unprivileged.
set -euo pipefail

EXT_DIR="$(cd "$(dirname "$0")/../../frontend/gnome/xmax@local" && pwd)"
SCHEMAS_SRC="$EXT_DIR/schemas"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/glib-2.0/schemas"

echo "== 1. glib-compile-schemas (strict) =="
glib-compile-schemas --strict --targetdir="$WORK/glib-2.0/schemas" "$SCHEMAS_SRC"
echo "   schema compiles"

export GSETTINGS_SCHEMA_DIR="$WORK/glib-2.0/schemas"

echo "== 2. schema registered =="
gsettings list-schemas | grep -qx org.xmax.shell && echo "   org.xmax.shell present" \
    || { echo "   org.xmax.shell MISSING" >&2; exit 1; }

echo "== 3. list-keys =="
gsettings list-keys org.xmax.shell

echo "== 4. default show-top-bar-icon =="
gsettings get org.xmax.shell show-top-bar-icon

echo "== 5. charge-limit-values default =="
gsettings get org.xmax.shell charge-limit-values
echo "   default: ['85', '100']"

echo "== 6. charge-limit-values round-trip =="
gsettings set org.xmax.shell charge-limit-values "['80','90','100']"
test "$(gsettings get org.xmax.shell charge-limit-values)" = "['80', '90', '100']"
gsettings reset org.xmax.shell charge-limit-values
echo "   round-trip OK"

echo
echo "schema verification OK"