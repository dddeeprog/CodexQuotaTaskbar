# Zotero Mica Glass Theme — Design

## Goal

Create a Windows 11-only, bootstrapped Zotero plugin for Zotero 9.0.6 (64-bit) that applies a restrained Mica-inspired glass treatment across Zotero-owned interface windows: the library, reader, notes, preferences, menus, and compatible dialogs. The theme must preserve usability and be fully removable without restarting Zotero.

## Scope and boundaries

The plugin will theme content rendered inside Zotero windows. It will not attempt to modify the native Windows title bar, system-owned dialogs, or use an external native executable. If a window cannot safely accept the stylesheet, the plugin will leave it unchanged and Zotero will continue normally.

Windows 11 is the only supported and tested target. The initial manifest will be capped to the tested Zotero 9.0.6 build (`strict_min_version` and `strict_max_version` both 9.0.6); widening that range requires repeating the clean-profile verification below. This is a support policy rather than a Windows-major-version runtime gate: Windows 10 and 11 commonly expose the same `10.0` version family, so the plugin does not make an unreliable distinction. Startup checks only `Zotero.isWin`; on non-Windows it registers nothing. The bootstrap design follows Zotero's current bootstrapped-plugin model and isolates version-sensitive code in one module.

## Architecture

The add-on is a bootstrapped XPI with these responsibilities:

1. `manifest.json` declares the extension identity, version, and Zotero 9 compatibility.
2. `bootstrap.js` owns startup, shutdown, main-window hooks, and a lightweight window observer for reader, preferences, and compatible internal dialogs.
3. A window-style manager injects exactly one owned stylesheet link into each eligible document. The single bundle uses a `data-mica-glass-window` value (`main`, `reader`, `preferences`, or `dialog`) to select scoped rules; it removes the link during unload or shutdown.
4. The bundle supplies shared custom properties for the Mica palette, borders, selected states, and text contrast. Embedded reader and note-editor documents are handled as separate documents, never assumed to inherit an outer-window stylesheet.
5. Preferences store whether the theme is enabled and whether the high-readability variant is active. Changes are applied to all open eligible windows without a restart.
6. A Tools-menu submenu exposes Enable/Disable, Standard Mica, and Enhanced Readability.

Window injection is idempotent: the manager first checks the unique stylesheet element ID. A `generation` token and `active` flag guard every delayed document-ready and stylesheet-load callback. Each lifecycle hook catches errors per window and logs a diagnostic, never throwing into Zotero's startup path. Shutdown first sets `active` false and advances the generation, then unregisters observers, preference listeners, and menu registrations; it removes owned links and unload listeners from the tracked documents before clearing references. A delayed callback whose generation no longer matches must do nothing.

## Window eligibility matrix

Only documents positively classified by the following matrix receive a stylesheet. The URI predicates and window types below were verified against the installed Zotero 9.0.6 application package. Unknown URI/type combinations are excluded.

| Target | Discovery | Ready point | Bundle value | Notes |
| --- | --- | --- | --- | --- |
| Library/main window | `onMainWindowLoad` | `DOMContentLoaded` or already-complete document | `main` | URI exactly `chrome://zotero/content/zoteroPane.xhtml`; `windowtype="navigator:browser"`. Its own popup menus are styled by this bundle. |
| Standalone reader shell | `Services.wm` `onOpenWindow` | top-level `load`, then `#reader` browser `load` | `reader` | URI exactly `chrome://zotero/content/reader.xhtml`; `windowtype="zotero:reader"`. |
| Reader content | `#tabs-deck` mutation observer in a main window or `#reader` in standalone shell | matching browser `load` | `reader` | Browser source exactly `resource://zotero/reader/reader.html`; inject into its `contentDocument`, not its host tab/shell alone. |
| Standalone note window | `Services.wm` `onOpenWindow` | top-level `load` | `dialog` | URI exactly `chrome://zotero/content/note.xhtml`; `windowtype="zotero:note"`. |
| Note editor content | Mutation observer under a styled Zotero chrome document | matching iframe `load` | `reader` | `note-editor` descendant `iframe#editor-view[src="resource://zotero/note-editor/editor.html"]`; inject into `contentDocument`. |
| Preferences window | `Services.wm` `onOpenWindow` | top-level `load` | `preferences` | URI exactly `chrome://zotero/content/preferences/preferences.xhtml`; `windowtype="zotero:pref"`. |
| Generic Zotero dialog | `Services.wm` `onOpenWindow` | top-level `load` | `dialog` | URI matching `chrome://zotero/content/**/*.xhtml` and an actual top-level Zotero window; excludes the specific shell URLs above, all non-`chrome://zotero/` URLs, native/system dialogs, and content-browser documents. |

The observer registers a `Services.wm` listener during startup and unregisters the identical listener during shutdown. Its `onOpenWindow` receives the opened XUL window, obtains `docShell.domWindow`, and attaches a one-shot top-level `load` listener before classification. Main-window and reader-shell observers use a `MutationObserver` for browser/iframe nodes added after the host document loads, then attach a one-shot `load` listener and inject only if the exact `src` in the matrix matches. The manager attaches one unload listener per tracked document. The implementation will not use a catch-all selector or style content from arbitrary web origins.

In-document XUL/HTML popup menus are not separate windows and are not observed through `Services.wm`: they are styled by the same bundle injected into their owning main, reader-shell, note, preferences, or dialog document. The window mediator is reserved for top-level Zotero windows and dialogs.

## Visual system

The default Standard Mica mode uses a calm blue-gray surface with moderate opacity, a one-pixel low-contrast border, restrained elevation, and clear active/selected states. Panels are visually layered through translucent surfaces rather than heavy shadows. Standard text stays near opaque; muted text retains sufficient contrast.

Enhanced Readability uses more opaque panel backgrounds, stronger separators, and higher text contrast. It changes variables only, so it is low-risk and applies consistently to the single theme bundle. The theme responds to Zotero's light/dark mode using media and application-state selectors where available. It is a CSS approximation of Windows Mica: if transparency or backdrop filtering is unavailable, the fallback is an opaque layered blue-gray surface, not a broken or invisible panel.

Normal text must meet WCAG AA's 4.5:1 contrast target against its panel background in both modes; muted text must meet 3:1, and selected states must preserve a non-colour cue such as border or weight.

## Interaction flow

On startup or enable, the plugin registers its menu and injects the selected style variant into open eligible windows. Newly opened main, reader, note-editor, preference, or compatible dialog documents are styled after their documents are ready. Menu actions persist their preference and refresh injected styles. Disabling from the menu removes styles immediately; re-enabling restores them. Disabling or uninstalling the add-on removes all styles and observers.

Root `prefs.js` defines `extensions.mica-glass.enabled` (default `true`) and `extensions.mica-glass.variant` (default `standard`; allowed `standard` or `readability`). The variant persists while disabled. A namespaced preference observer updates all tracked documents and is removed during shutdown. The menu uses Zotero's official `main/menubar/tools` target and namespaced Fluent IDs; its items display checked state for enabled and the current variant, while variant choices are disabled when the theme is off.

## Error handling

- An unsupported, unknown, or non-Windows 11 window is skipped.
- A stylesheet-load failure is isolated to that window and reported in Zotero's debug log.
- A missing selector simply retains Zotero's original visual styling.
- No data, library content, reader annotations, or Zotero preferences outside the plugin namespace are modified.

## Verification

1. Package validation confirms the XPI manifest, bootstrap entry points, and the exact 9.0.6 compatibility cap.
2. A clean Zotero 9.0.6 profile validates the URI/type whitelist from the eligibility matrix before a release package is produced.
3. Manual smoke tests cover every eligible matrix row: main library, PDF reader, notes, preferences, Zotero-rendered menus, and a representative whitelisted dialog.
4. Test Standard Mica and Enhanced Readability in both Zotero light and dark modes, checking the stated contrast targets.
5. Repeatedly open/close reader and preferences documents and assert one owned stylesheet link per eligible document, no duplicate injection, and no error output.
6. Run enable/disable/uninstall/re-enable cycles and assert zero owned links, observers, preference listeners, and menu registrations after each cleanup.

## Acceptance criteria

- Zotero-owned UI content is consistently Mica-styled across the stated in-scope windows.
- Readability remains intact in light and dark modes.
- The user can switch off the effect or use Enhanced Readability without restarting.
- An unsupported window never prevents Zotero from starting or working.
- Disable/uninstall leaves no visible style residue.
