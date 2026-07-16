# Zotero Mica Glass Theme — Design

## Goal

Create a Windows 11-only, bootstrapped Zotero plugin for Zotero 9.0.6 (64-bit) that applies a restrained Mica-inspired glass treatment across Zotero-owned interface windows: the library, reader, notes, preferences, menus, and compatible dialogs. The theme must preserve usability and be fully removable without restarting Zotero.

## Scope and boundaries

The plugin will theme content rendered inside Zotero windows. It will not attempt to modify the native Windows title bar, system-owned dialogs, or use an external native executable. If a window cannot safely accept the stylesheet, the plugin will leave it unchanged and Zotero will continue normally.

Windows 11 is the supported target. The manifest will target Zotero 9; the bootstrap design follows Zotero's current bootstrapped-plugin model and is isolated so compatibility changes can be made in one place.

## Architecture

The add-on is a bootstrapped XPI with these responsibilities:

1. `manifest.json` declares the extension identity, version, and Zotero 9 compatibility.
2. `bootstrap.js` owns startup, shutdown, main-window hooks, and a lightweight window observer for reader, preferences, and compatible internal dialogs.
3. A window-style manager injects exactly one identified stylesheet link into each eligible document and removes it during unload or shutdown.
4. Separate CSS entry points cover the library/main UI, the reader, and generic compatible dialogs. Shared custom properties supply the Mica palette, borders, selected states, and text contrast.
5. Preferences store whether the theme is enabled and whether the high-readability variant is active. Changes are applied to all open eligible windows without a restart.
6. A Tools-menu submenu exposes Enable/Disable, Standard Mica, and Enhanced Readability.

Window injection is idempotent: the manager first checks the unique stylesheet element ID. Each lifecycle hook catches errors per window and logs a diagnostic, never throwing into Zotero's startup path. Shutdown iterates all tracked/open windows, removes injected nodes, unregisters observers and menus, and releases references.

## Visual system

The default Standard Mica mode uses a calm blue-gray surface with moderate opacity, a one-pixel low-contrast border, restrained elevation, and clear active/selected states. Panels are visually layered through translucent surfaces rather than heavy shadows. Standard text stays near opaque; muted text retains sufficient contrast.

Enhanced Readability uses more opaque panel backgrounds, stronger separators, and higher text contrast. It changes variables only, so it is low-risk and applies consistently to all theme stylesheets. The theme responds to Zotero's light/dark mode using media and application-state selectors where available.

## Interaction flow

On startup or enable, the plugin registers its menu and injects the selected style variant into open windows. Newly opened main, reader, preference, or compatible dialog windows are styled after their documents are ready. Menu actions persist their preference and refresh injected styles. Disabling from the menu removes styles immediately; re-enabling restores them. Disabling or uninstalling the add-on removes all styles and observers.

## Error handling

- An unsupported or unknown window is skipped.
- A stylesheet-load failure is isolated to that window and reported in Zotero's debug log.
- A missing selector simply retains Zotero's original visual styling.
- No data, library content, reader annotations, or Zotero preferences outside the plugin namespace are modified.

## Verification

1. Package validation confirms the XPI manifest and bootstrap entry points.
2. Manual smoke tests cover the main library, PDF reader, notes, preferences, menus, and a representative dialog.
3. Test Standard Mica and Enhanced Readability in both Zotero light and dark modes.
4. Repeatedly open/close a reader and preferences window to verify no duplicate stylesheet links and no errors.
5. Disable and uninstall the plugin, confirming all injected elements are removed and Zotero remains usable.

## Acceptance criteria

- Zotero-owned UI content is consistently Mica-styled across the stated in-scope windows.
- Readability remains intact in light and dark modes.
- The user can switch off the effect or use Enhanced Readability without restarting.
- An unsupported window never prevents Zotero from starting or working.
- Disable/uninstall leaves no visible style residue.
