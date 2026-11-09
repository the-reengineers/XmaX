// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
// logo.js — resolves the XmaX app logo as a GIcon. The logo is bundled with
// the extension at icons/logo.svg (kept in sync with shared/assets/logo.svg by
// `just generate-assets`) and loaded as a file GIcon, so no icon-theme
// installation is needed. If the file is missing (stale checkout, incomplete
// bundle) callers get a system symbolic instead.

import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

const LOGO_PATH = 'icons/logo.svg';
const FALLBACK_ICON = 'utilities-system-monitor-symbolic';

/**
 * @param {string} extensionDir this extension's install directory
 * (Extension#path), used to locate the bundled logo
 * @returns {Gio.Icon} the bundled XmaX logo, or a system symbolic fallback
 */
export function logoGicon(extensionDir) {
    if (extensionDir) {
        const path = GLib.build_filenamev([extensionDir, LOGO_PATH]);
        const file = Gio.File.new_for_path(path);
        try {
            if (file.query_exists(null))
                return new Gio.FileIcon({file});
        } catch (e) {
            logError(e, `XmaX logo lookup failed for ${path}`);
        }
    }
    return Gio.ThemedIcon.new_with_default_fallbacks(FALLBACK_ICON);
}
