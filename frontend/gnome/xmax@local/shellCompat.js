// shellCompat.js — thin adapter over GNOME Shell's internal imports. Every
// shell UI module used by this extension is re-exported here so future GNOME
// version / DE differences are localized to one file instead of being strewn
// across indicator.js, quickSettings.js and extension.js.

import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';
import * as QuickSettings from 'resource:///org/gnome/shell/ui/quickSettings.js';
import * as Config from 'resource:///org/gnome/shell/misc/config.js';

export {Main, PanelMenu, PopupMenu, QuickSettings, Config};

/**
 * @param {number} major
 * @param {number} [minor]
 * @returns {boolean} true when the running shell is at least the given version.
 */
export function shellAtLeast(major, minor = 0) {
    const [maj, min] = Config.PACKAGE_VERSION.split('.').map(v => Number(v));
    return maj > major || (maj === major && min >= minor);
}