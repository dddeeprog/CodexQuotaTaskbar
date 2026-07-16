# Zotero Mica Glass Theme — Design

## Goal

Create a Windows 11-only, bootstrapped Zotero plugin for Zotero 9.0.6 (64-bit) that applies genuine DWM Mica to Zotero top-level windows and a matching restrained glass treatment across Zotero-owned interface documents: the library, reader, notes, preferences, menus, and supported dialogs. The theme must preserve usability and be fully removable without restarting Zotero.

## Scope and boundaries

The plugin follows the architecture of Avi7ii/Zotero-glass but replaces its macOS AppKit bridge with a Windows DWM bridge. It uses no helper process, LaunchAgent, DLL, registry write, or external executable. The bridge styles the native title bar/background of eligible Zotero top-level windows; scoped CSS lets Zotero's otherwise opaque content surfaces reveal that material. System-owned dialogs and browser content from non-Zotero origins are excluded. If either the bridge or CSS cannot safely apply to a window, that window keeps Zotero's original appearance and Zotero continues normally.

Windows 11 is the only supported and tested target. The initial manifest will be capped to the tested Zotero 9.0.6 build (`strict_min_version` and `strict_max_version` both 9.0.6); widening that range requires repeating the clean-profile verification below. This is a support policy rather than a Windows-major-version runtime gate: Windows 10 and 11 commonly expose the same `10.0` version family, so the plugin does not make an unreliable distinction. Startup checks only `Zotero.isWin`; on non-Windows it registers nothing. The bootstrap design follows Zotero's current bootstrapped-plugin model and isolates version-sensitive code in one module.

## Architecture

The add-on is a bootstrapped XPI with these responsibilities:

1. `manifest.json` declares the extension identity, version, and Zotero 9 compatibility.
2. `bootstrap.js` owns startup, shutdown, main-window hooks, existing-window enumeration, and a top-level-window observer for reader, preferences, notes, and supported dialogs.
3. `native/dwmBridge.js` dynamically imports `ctypes`, opens the system `dwmapi.dll`, resolves `DwmGetWindowAttribute` and `DwmSetWindowAttribute`, and never loads a bundled or third-party DLL.
4. A native-window manager obtains each eligible Gecko top-level window's `nsIBaseWindow.nativeHandle`, normalizes it to an `HWND`, snapshots the DWM attributes it will change, applies Mica, and restores the snapshot during cleanup.
5. A stylesheet manager injects exactly one owned bundle into each eligible Zotero document. The bundle uses a `data-mica-glass-window` value (`main`, `reader`, `preferences`, or `dialog`) to select scoped rules. Embedded reader and note-editor documents are handled as separate documents, never assumed to inherit an outer-window stylesheet.
6. The CSS bundle supplies transparency only where the Mica backdrop can show through, plus Mica palette variables, borders, selected states, and text contrast. It remains a safe opaque layered fallback when native Mica is unavailable.
7. Preferences store whether the theme is enabled and whether the high-readability variant is active. Changes are applied to all open eligible windows without a restart.
8. A Tools-menu submenu exposes Enable/Disable, Standard Mica, and Enhanced Readability.

Window injection and native application are idempotent: the managers first check their unique stylesheet ID and per-`HWND` bridge record. A `generation` token and `active` flag guard every delayed document-ready and stylesheet-load callback. Each lifecycle hook catches errors per window and logs a diagnostic, never throwing into Zotero's startup path. Shutdown first sets `active` false and advances the generation, then unregisters observers, preference listeners, and menu registrations; it restores native DWM attributes, removes owned links and unload listeners from tracked documents, closes `dwmapi.dll`, and clears references. A delayed callback whose generation no longer matches must do nothing.

## Native DWM bridge contract

For each eligible top-level Zotero window, the bridge reads and stores the pre-plugin values before writing them. It calls `DwmSetWindowAttribute(HWND, DWMWA_SYSTEMBACKDROP_TYPE, ...)` with `DWMSBT_MAINWINDOW` (value `2`) for the library and standalone reader, and `DWMSBT_TRANSIENTWINDOW` (value `3`) for supported utility windows. It calls `DWMWA_USE_IMMERSIVE_DARK_MODE` (value `20`) only when Zotero is in dark mode; on a light Zotero theme it restores the saved frame-mode value rather than forcing dark chrome.

The bridge treats every failing HRESULT, missing `ctypes` capability, missing `nativeHandle`, or unsupported DWM attribute as a per-window native failure. It records the reason, leaves the window usable, and applies the opaque CSS fallback. It must never use undocumented `SetWindowCompositionAttribute`, patch Zotero binaries, or start another process. On disable/uninstall it restores the stored backdrop and dark-frame attributes for each tracked `HWND`; if a prior value could not be read, it sets the backdrop to `DWMSBT_AUTO` and removes only plugin-owned CSS.

## Window eligibility matrix

Only documents positively classified by the following matrix receive a stylesheet. The URI predicates and window types below were verified against the installed Zotero 9.0.6 application package. Unknown URI/type combinations are excluded. Startup/enable first enumerates existing top-level windows through `Services.wm.getEnumerator(null)` and applies this same matrix, then registers `onOpenWindow` for future windows.

| Target | Discovery | Ready point | Bundle value | Notes |
| --- | --- | --- | --- | --- |
| Library/main window | `onMainWindowLoad` | `DOMContentLoaded` or already-complete document | `main` | URI exactly `chrome://zotero/content/zoteroPane.xhtml`; `windowtype="navigator:browser"`. Its own popup menus are styled by this bundle. |
| Standalone reader shell | `Services.wm` `onOpenWindow` | top-level `load`, then `#reader` browser `load` | `reader` | URI exactly `chrome://zotero/content/reader.xhtml`; `windowtype="zotero:reader"`. |
| Reader content | `#tabs-deck` mutation observer in a main window or `#reader` in standalone shell | matching browser `load` | `reader` | Browser source exactly `resource://zotero/reader/reader.html`; inject into its `contentDocument`, not its host tab/shell alone. |
| Standalone note window | `Services.wm` `onOpenWindow` | top-level `load` | `dialog` | URI exactly `chrome://zotero/content/note.xhtml`; `windowtype="zotero:note"`. |
| Note editor content | Mutation observer under a styled Zotero chrome document | matching iframe `load` | `reader` | `note-editor` descendant `iframe#editor-view[src="resource://zotero/note-editor/editor.html"]`; inject into `contentDocument`. |
| Preferences window | `Services.wm` `onOpenWindow` | top-level `load` | `preferences` | URI exactly `chrome://zotero/content/preferences/preferences.xhtml`; `windowtype="zotero:pref"`. |
| Verified generic dialogs | `Services.wm` enumeration/`onOpenWindow` | top-level `load` | `dialog` | Finite allowlist: `chrome://zotero/content/rtfScan.xhtml` (`rtfScan`), `chrome://zotero/content/merge.xhtml` (`zotero:merge`), `chrome://zotero/content/selectItemsDialog.xhtml` (`zotero:selectItems`), and `chrome://zotero/content/preferences/quickCopySiteEditor.xhtml` (`zotero:quickCopySiteEditor`). Other dialogs remain unstyled until added with a test. |

The observer registers a `Services.wm` listener during startup and unregisters the identical listener during shutdown. Its `onOpenWindow` receives the opened XUL window, obtains `docShell.domWindow`, and attaches a one-shot top-level `load` listener before classification. Main-window and reader-shell observers use a `MutationObserver` for browser/iframe nodes added after the host document loads, then attach a one-shot `load` listener and inject only if the exact `src` in the matrix matches. The manager attaches one unload listener per tracked document. The implementation will not use a catch-all selector or style content from arbitrary web origins.

In-document XUL/HTML popup menus are not separate windows and are not observed through `Services.wm`: they are styled by the same bundle injected into their owning main, reader-shell, note, preferences, or dialog document. The window mediator is reserved for top-level Zotero windows and dialogs.

## Visual system

The default Standard Mica mode uses DWM's system-drawn Mica behind eligible native window areas, with a calm blue-gray CSS tint where Zotero content surfaces need a stabilizing layer. It uses a one-pixel low-contrast border, restrained elevation, and clear active/selected states. Panels are visually layered through translucent surfaces rather than heavy shadows. Standard text stays near opaque; muted text retains sufficient contrast.

Enhanced Readability uses more opaque panel backgrounds, stronger separators, and higher text contrast. It changes CSS variables only, so it is low-risk and applies consistently to the single theme bundle. The theme responds to Zotero's light/dark mode using media and application-state selectors where available. If native Mica, transparency, or backdrop filtering is unavailable, the fallback is an opaque layered blue-gray surface, not a broken or invisible panel.

Normal text must meet WCAG AA's 4.5:1 contrast target against its panel background in both modes; muted text must meet 3:1, and selected states must preserve a non-colour cue such as border or weight.

## Interaction flow

On startup or enable, the plugin enumerates existing eligible windows, applies native DWM Mica to their top-level handles, and injects the selected style variant into their eligible documents before registering observers for newly opened windows. Newly opened main, reader, note-editor, preference, or supported dialog documents are styled after their documents are ready. Menu actions persist their preference and refresh injected styles. Disabling from the menu restores each tracked DWM window and removes styles immediately; re-enabling reapplies both. Disabling or uninstalling the add-on removes all styles and observers and restores the saved native attributes.

Root `prefs.js` defines `extensions.mica-glass.enabled` (default `true`) and `extensions.mica-glass.variant` (default `standard`; allowed `standard` or `readability`). The variant persists while disabled. A namespaced preference observer updates all tracked documents and is removed during shutdown. The menu uses Zotero's official `main/menubar/tools` target and namespaced Fluent IDs; its items display checked state for enabled and the current variant, while variant choices are disabled when the theme is off.

## Error handling

- An unsupported, unknown, non-Windows, or native-bridge-ineligible window is skipped or receives CSS fallback only.
- A DWM call failure is recorded per window and never prevents the CSS layer, Zotero startup, or normal library operations.
- A stylesheet-load failure is isolated to that window and reported in Zotero's debug log.
- A missing selector simply retains Zotero's original visual styling.
- No data, library content, reader annotations, or Zotero preferences outside the plugin namespace are modified.

## Verification

1. Package validation confirms the XPI manifest, bootstrap entry points, and the exact 9.0.6 compatibility cap.
2. Automated bridge tests mock `ctypes` and assert correct DWM attribute IDs, values, `HWND` handling, HRESULT fallback, snapshot restoration, and no helper-process APIs.
3. A clean Zotero 9.0.6 profile validates the URI/type matrix and native-handle path before a release package is produced.
4. Manual smoke tests cover every eligible matrix row: main library, PDF reader, notes, preferences, Zotero-rendered menus, and each finite whitelisted dialog.
5. Test Standard Mica and Enhanced Readability in both Zotero light and dark modes, checking the stated contrast targets and native frame behavior.
6. Repeatedly open/close reader and preferences documents and assert one owned stylesheet link per eligible document, one DWM bridge record per top-level window, no duplicate injection, and no error output.
7. Run enable/disable/uninstall/re-enable cycles and assert zero owned links, observers, preference listeners, menu registrations, or active bridge records after each cleanup, with stored DWM values restored.

## Acceptance criteria

- Zotero's library and standalone reader receive system-drawn Windows 11 Mica when DWM accepts the attributes; other eligible utility windows receive the documented DWM material or CSS fallback.
- Zotero-owned UI content is consistently Mica-styled across the stated in-scope documents.
- Readability remains intact in light and dark modes.
- The user can switch off the effect or use Enhanced Readability without restarting.
- An unsupported window never prevents Zotero from starting or working.
- Disable/uninstall leaves no visible style residue.
