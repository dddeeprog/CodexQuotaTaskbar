# Zotero Mica Glass Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows 11/Zotero 9.0.6 XPI that applies genuine DWM Mica to eligible Zotero windows and safe, readable glass styling to their Zotero-owned documents.

**Architecture:** A bootstrapped Zotero extension uses a small `ctypes` DWM bridge to snapshot, transactionally apply, and restore native window attributes. A separate lifecycle manager classifies known Zotero windows/documents, coordinates native success markers with a single scoped CSS bundle, and cleans up every listener and injected node. The plugin has no helper process or bundled native code.

**Tech Stack:** Zotero 9 bootstrapped extension, JavaScript, Mozilla `ctypes`, Windows `dwmapi.dll`, XUL/HTML/CSS, Python `unittest`, Python `zipfile`.

---

## Planned file structure

- `zotero-mica-glass/manifest.json` — Zotero 9.0.6 add-on metadata.
- `zotero-mica-glass/bootstrap.js` — lifecycle entry points and runtime chrome registration.
- `zotero-mica-glass/prefs.js` — namespaced defaults.
- `zotero-mica-glass/chrome/content/mica-glass.svg` — extension icon declared by the manifest.
- `zotero-mica-glass/chrome/content/micaGlass.js` — coordinator, window classification, preferences, menus, and cleanup.
- `zotero-mica-glass/chrome/content/dwmBridgeCore.mjs` — pure, fake-adapter-testable DWM transaction state machine, exposed through the registered `chrome://zotero-mica-glass/content/` URL.
- `zotero-mica-glass/native/dwmBridge.js` — 64-bit-safe DWM FFI, injectable adapter boundary, and transactional native-state ownership.
- `zotero-mica-glass/chrome/content/mica-glass.css` — active/fallback visual rules and scoped reader styles.
- `zotero-mica-glass/chrome/content/preferences.xhtml` — small in-app settings dialog.
- `zotero-mica-glass/chrome/content/preferences.js` — settings dialog bridge to the coordinator API.
- `zotero-mica-glass/scripts/build.py` — deterministic XPI packager.
- `zotero-mica-glass/tests/test_plugin_contract.py` — manifest, packaging, forbidden-API, CSS-state, and lifecycle contract tests.
- `zotero-mica-glass/tests/dwmBridge.test.mjs` — executable fake-adapter tests for DWM transactions and theme refresh.

### Task 1: Create a minimal, testable XPI scaffold

**Files:**

- Create: `zotero-mica-glass/manifest.json`
- Create: `zotero-mica-glass/bootstrap.js`
- Create: `zotero-mica-glass/prefs.js`
- Create: `zotero-mica-glass/chrome/content/mica-glass.svg`
- Create: `zotero-mica-glass/scripts/build.py`
- Create: `zotero-mica-glass/tests/test_plugin_contract.py`

- [ ] **Step 1: Write the failing manifest/package tests**

```python
def test_manifest_targets_only_zotero_906():
    manifest = json.loads((PLUGIN / "manifest.json").read_text(encoding="utf-8"))
    target = manifest["applications"]["zotero"]
    assert manifest["manifest_version"] == 2
    assert target["strict_min_version"] == "9.0.6"
    assert target["strict_max_version"] == "9.0.6"

def test_build_contains_files_at_xpi_root(tmp_path):
    artifact = build(tmp_path / "mica-glass.xpi")
    with ZipFile(artifact) as archive:
        assert "manifest.json" in archive.namelist()
        assert "bootstrap.js" in archive.namelist()
        assert not any(name.startswith("zotero-mica-glass/") for name in archive.namelist())

def test_bootstrap_guards_non_windows_before_loading_plugin_runtime():
    source = (PLUGIN / "bootstrap.js").read_text(encoding="utf-8")
    guard = source.index("if (!Zotero.isWin)")
    assert guard < source.index("registerChrome")
    assert guard < source.index("loadSubScript")
```

- [ ] **Step 2: Run the new tests and verify they fail**

Run: `python -m unittest zotero-mica-glass.tests.test_plugin_contract -v`  
Expected: FAIL because the plugin directory and build module do not exist.

- [ ] **Step 3: Add the minimal manifest, bootstrap, defaults, and packager**

`manifest.json` must contain only the required identity, icon, and Zotero application block; use ID `zotero-mica-glass@tomatok.local`. `prefs.js` must define:

```javascript
pref("extensions.zotero-mica-glass.enabled", true);
pref("extensions.zotero-mica-glass.variant", "standard");
```

`bootstrap.js` must set an explicit `inactivePlatform` state and return immediately on `!Zotero.isWin` *before* runtime chrome registration, script loading, menu setup, observers, or `ctypes` initialization. When `inactivePlatform` is true, `shutdown`, `onMainWindowLoad`, and `onMainWindowUnload` must be no-ops; repeated enable/disable must not dereference `pluginScope` or `chromeHandle`. On Windows it must register `chrome/content/` at runtime, load `native/dwmBridge.js` into the private plugin scope with `Services.scriptloader.loadSubScript(rootURI + "native/dwmBridge.js", pluginScope)`, then load `chrome/content/micaGlass.js` into that same scope. Delegate `startup`, `shutdown`, `onMainWindowLoad`, and `onMainWindowUnload`, and destruct the chrome handle on shutdown.

`scripts/build.py` must exclude `tests/`, `__pycache__/`, `.git/`, and `dist/`; add files in sorted POSIX order using explicit `ZipInfo` records with the fixed timestamp `(1980, 1, 1, 0, 0, 0)`, fixed permissions, and `ZIP_DEFLATED` compression.

- [ ] **Step 4: Run scaffold tests**

Run: `python -m unittest zotero-mica-glass.tests.test_plugin_contract -v`  
Expected: PASS for manifest/default/package tests.

- [ ] **Step 5: Commit the scaffold**

```bash
git add zotero-mica-glass/manifest.json zotero-mica-glass/bootstrap.js zotero-mica-glass/prefs.js zotero-mica-glass/chrome/content/mica-glass.svg zotero-mica-glass/scripts/build.py zotero-mica-glass/tests/test_plugin_contract.py
git commit -m "feat: scaffold Zotero Mica Glass plugin"
```

### Task 2: Implement the safe Windows DWM bridge

**Files:**

- Create: `zotero-mica-glass/native/dwmBridge.js`
- Create: `zotero-mica-glass/chrome/content/dwmBridgeCore.mjs`
- Create: `zotero-mica-glass/tests/dwmBridge.test.mjs`
- Create: `zotero-mica-glass/tests/test_dwm_wrapper_contract.py`
- Modify: `zotero-mica-glass/tests/test_plugin_contract.py`

- [ ] **Step 1: Write failing DWM contract tests**

```javascript
import test from "node:test";
import assert from "node:assert/strict";
import { createBridgeCore } from "../chrome/content/dwmBridgeCore.mjs";

test("partial native apply restores backdrop before fallback", () => {
  const fake = new FakeDwm({ failSet: [20] });
  const bridge = createBridgeCore(fake);
  const result = bridge.apply("0x7FFF0000", { backdrop: 2, dark: 1 });
  assert.equal(result.state, "fallback");
  assert.deepEqual(fake.calls, ["get:38", "get:20", "set:38:2", "set:20:1", "set:38:0"]);
});

test("refresh failure restores prior plugin dark value", () => {
  const fake = new FakeDwm({ failSet: [20] });
  const bridge = createBridgeCore(fake);
  bridge.records.set("h", recordWithLastDark(1));
  assert.equal(bridge.refreshDark("h", 0).state, "fallback");
  assert.deepEqual(fake.calls, ["set:20:0", "set:20:1"]);
});
```

Add wrapper contract tests before writing the wrapper:

```python
def test_wrapper_uses_only_system32_and_correct_ctypes_abi():
    source = WRAPPER.read_text(encoding="utf-8")
    assert 'ChromeUtils.importESModule("resource://gre/modules/ctypes.sys.mjs")' in source
    assert 'Services.dirsvc.get("SysD", Ci.nsIFile).path' in source
    assert 'ctypes.open("dwmapi.dll")' not in source
    assert source.count("ctypes.winapi_abi") >= 2
    assert "ctypes.int32_t" in source
    assert "hr < 0" in source

def test_wrapper_never_uses_number_for_hwnd_and_imports_registered_core():
    source = WRAPPER.read_text(encoding="utf-8")
    assert "Number(nativeHandle)" not in source
    assert "ctypes.UInt64" in source
    assert 'chrome://zotero-mica-glass/content/dwmBridgeCore.mjs' in source
```

- [ ] **Step 2: Run the test file and verify it fails**

Run: `node --test zotero-mica-glass/tests/dwmBridge.test.mjs && python -m unittest zotero-mica-glass.tests.test_dwm_wrapper_contract -v`  
Expected: FAIL because the core and wrapper do not exist.

- [ ] **Step 3: Implement the bridge with explicit ABI and ownership rules**

Implement a pure `chrome/content/dwmBridgeCore.mjs` with an injected `getAttribute`/`setAttribute` adapter and a Zotero-facing `native/dwmBridge.js` wrapper. The core owns transaction, ownership checks, records, rollback, and theme refresh; it can therefore be exercised by the fake adapter. The wrapper imports it with `ChromeUtils.importESModule("chrome://zotero-mica-glass/content/dwmBridgeCore.mjs")`; this URL is valid because bootstrap's runtime registration exposes `chrome/content/`. The wrapper alone performs `ctypes` setup and handle conversion.

The wrapper's constants and declarations must be:

```javascript
const HRESULT = ctypes.long;
const HWND = ctypes.voidptr_t;
const DWORD = ctypes.uint32_t;
const INT = ctypes.int32_t;

const DwmSetWindowAttribute = dll.declare(
  "DwmSetWindowAttribute", ctypes.winapi_abi, HRESULT,
  HWND, DWORD, ctypes.voidptr_t, DWORD
);
const DwmGetWindowAttribute = dll.declare(
  "DwmGetWindowAttribute", ctypes.winapi_abi, HRESULT,
  HWND, DWORD, ctypes.voidptr_t, DWORD
);
```

Use `FAILED(hr) { return hr < 0; }`. Snapshot both attribute 38 and attribute 20 as four-byte `INT` buffers before mutation. Convert handle strings via `ctypes.UInt64` CData to `ctypes.voidptr_t`; reject null/invalid/truncated handles. Apply backdrop first, then dark mode. If either write fails, restore every already changed attribute before returning fallback. Keep an `HWND` record containing original values, last plugin values, failure state, and a cleanup method that restores only plugin-owned current values. Do not use `SetWindowCompositionAttribute`, `nsIProcess`, `runAsync`, `cmd.exe`, or external DLLs.

Add executable tests for: both snapshots happen before the first write; successful apply marks active; dark-write failure rolls back backdrop before fallback; snapshot failure makes zero writes; refresh failure restores the prior plugin dark value; failed refresh rollback restores originals/blocks the handle; ownership-safe cleanup does not overwrite a changed third-party value; and high-bit string handles reach the adapter without a JavaScript-number conversion.

- [ ] **Step 4: Run DWM contract tests**

Run: `node --test zotero-mica-glass/tests/dwmBridge.test.mjs && python -m unittest zotero-mica-glass.tests.test_dwm_wrapper_contract -v`  
Expected: PASS.

- [ ] **Step 5: Commit the bridge**

```bash
git add zotero-mica-glass/native/dwmBridge.js zotero-mica-glass/chrome/content/dwmBridgeCore.mjs zotero-mica-glass/tests/dwmBridge.test.mjs zotero-mica-glass/tests/test_dwm_wrapper_contract.py zotero-mica-glass/tests/test_plugin_contract.py
git commit -m "feat: add transactional DWM Mica bridge"
```

### Task 3: Add window discovery, classification, and state coupling

**Files:**

- Create: `zotero-mica-glass/chrome/content/micaGlass.js`
- Modify: `zotero-mica-glass/bootstrap.js`
- Modify: `zotero-mica-glass/tests/test_plugin_contract.py`

- [ ] **Step 1: Write failing classification/lifecycle tests**

```python
def test_window_matrix_is_finite_and_enumerates_existing_windows():
    source = MAIN.read_text(encoding="utf-8")
    for uri in [
        "chrome://zotero/content/zoteroPane.xhtml",
        "chrome://zotero/content/reader.xhtml",
        "chrome://zotero/content/note.xhtml",
        "chrome://zotero/content/preferences/preferences.xhtml",
    ]:
        assert uri in source
    assert "Services.wm.getEnumerator(null)" in source
    assert "onOpenWindow" in source
    assert "chrome://zotero/content/**/*.xhtml" not in source

def test_transparent_css_requires_native_success_marker():
    source = MAIN.read_text(encoding="utf-8")
    assert 'data-mica-glass-native' in source
    assert '"active"' in source
    assert '"fallback"' in source
```

- [ ] **Step 2: Run lifecycle tests and verify failure**

Run: `python -m unittest zotero-mica-glass.tests.test_plugin_contract -v`  
Expected: FAIL because the lifecycle manager has not been implemented.

- [ ] **Step 3: Implement `MicaGlass` coordinator**

Implement explicit matrix predicates for main, reader shell/content, standalone note, note editor iframe, preferences, and the four design-approved dialog URIs. On startup/enable, enumerate `Services.wm.getEnumerator(null)` before registering the same `onOpenWindow` listener for future top-level windows. Main and reader shells must use a `MutationObserver` and one-shot `load` listeners for `resource://zotero/reader/reader.html` and `resource://zotero/note-editor/editor.html`.

For every document, set `data-mica-glass-window` after classification. Call the bridge for the owning top-level window; set `data-mica-glass-native="active"` only after the bridge transaction succeeds, otherwise set `fallback`. Keep one document record per document and one DWM record per top-level window/`HWND`. An embedded reader or note-editor iframe unload removes only its link, media listener, and document record; it must not restore the owning top-level window's `HWND`. Restore/remove DWM records only when the corresponding top-level window unloads or the plugin is disabled.

- [ ] **Step 4: Run lifecycle tests**

Run: `python -m unittest zotero-mica-glass.tests.test_plugin_contract -v`  
Expected: PASS.

- [ ] **Step 5: Commit lifecycle support**

```bash
git add zotero-mica-glass/chrome/content/micaGlass.js zotero-mica-glass/bootstrap.js zotero-mica-glass/tests/test_plugin_contract.py
git commit -m "feat: manage Zotero window Mica lifecycle"
```

### Task 4: Add scoped Mica/fallback CSS

**Files:**

- Create: `zotero-mica-glass/chrome/content/mica-glass.css`
- Modify: `zotero-mica-glass/chrome/content/micaGlass.js`
- Modify: `zotero-mica-glass/tests/test_plugin_contract.py`

- [ ] **Step 1: Write failing style-safety tests**

```python
def test_css_has_active_and_opaque_fallback_layers():
    css = CSS.read_text(encoding="utf-8")
    assert ':root[data-mica-glass-native="active"]' in css
    assert ':root[data-mica-glass-native="fallback"]' in css
    assert "--mica-panel-bg" in css
    assert "--mica-readable-panel-bg" in css

def test_reader_selectors_cover_real_zotero_targets():
    css = CSS.read_text(encoding="utf-8")
    for selector in ["#reader-ui", "#sidebarContainer", "#context-pane", "#thumbnailView"]:
        assert selector in css
```

- [ ] **Step 2: Run style tests and verify failure**

Run: `python -m unittest zotero-mica-glass.tests.test_plugin_contract -v`  
Expected: FAIL because the stylesheet is absent.

- [ ] **Step 3: Implement one scoped stylesheet**

Define shared CSS variables for standard/readability variants. Scope all translucent rules under `:root[data-mica-glass-native="active"]`; scope opaque, high-contrast surfaces under `fallback`. Style main panes, toolbars, lists, readers, note editor, Zotero-rendered menus, preferences, and the finite dialogs without global `*` selectors. Keep normal text at 4.5:1 and muted text at 3:1 or above in both variants.

Update the coordinator to attach exactly one identified `<link>` per eligible document and to toggle `data-mica-glass-variant` rather than adding another stylesheet.

- [ ] **Step 4: Run style tests**

Run: `python -m unittest zotero-mica-glass.tests.test_plugin_contract -v`  
Expected: PASS.

- [ ] **Step 5: Commit visual layer**

```bash
git add zotero-mica-glass/chrome/content/mica-glass.css zotero-mica-glass/chrome/content/micaGlass.js zotero-mica-glass/tests/test_plugin_contract.py
git commit -m "feat: add scoped Mica and fallback styles"
```

### Task 5: Add controls and theme synchronization

**Files:**

- Create: `zotero-mica-glass/chrome/content/preferences.xhtml`
- Create: `zotero-mica-glass/chrome/content/preferences.js`
- Modify: `zotero-mica-glass/chrome/content/micaGlass.js`
- Modify: `zotero-mica-glass/tests/test_plugin_contract.py`

- [ ] **Step 1: Write failing menu/preference/theme tests**

```python
def test_menu_and_preferences_are_namespaced():
    source = MAIN.read_text(encoding="utf-8")
    assert 'main/menubar/tools' in source
    assert 'extensions.zotero-mica-glass.enabled' in source
    assert 'extensions.zotero-mica-glass.variant' in source

def test_theme_refresh_observes_zotero_theme_pref():
    source = MAIN.read_text(encoding="utf-8")
    assert 'browser.theme.toolbar-theme' in source
    assert 'Services.prefs.addObserver' in source
    assert 'refreshTrackedWindowsForTheme' in source

def test_disable_clears_native_records_for_fresh_reenable_snapshot():
    source = MAIN.read_text(encoding="utf-8")
    assert "restoreAllTrackedWindows" in source
    assert "clearAllBridgeRecords" in source
    assert "applyToExistingWindows" in source
```

- [ ] **Step 2: Run tests and verify failure**

Run: `python -m unittest zotero-mica-glass.tests.test_plugin_contract -v`  
Expected: FAIL because controls and observer code are absent.

- [ ] **Step 3: Implement controls and synchronisation**

Use Zotero's `main/menubar/tools` menu target to expose Enable/Disable, Standard Mica, Enhanced Readability, and Preferences. Persist only the two defined plugin preferences. The dialog must provide those controls and call a narrow API exposed by the coordinator.

Observe `extensions.zotero-mica-glass.enabled`, `extensions.zotero-mica-glass.variant`, and `browser.theme.toolbar-theme`. On `enabled=false`, immediately remove every owned document link, mark all documents fallback, restore every tracked native window, and clear all DWM bridge records while retaining the menu and preference observers. On `enabled=true`, enumerate/reapply all existing eligible windows and documents, taking fresh snapshots for every `HWND`; it must never reuse a record from before disable. On a variant change, retain the one-link-per-document invariant and update `data-mica-glass-variant` on every tracked document.

Resolve `browser.theme.toolbar-theme` `0` as dark, `1` as light, and `2` from the system color-scheme listener. On a theme change, transactionally update only the bridge dark-mode attribute while preserving original snapshots. On failed refresh, restore the last plugin value; if that fails, restore originals, mark fallback, and stop touching that handle. Remove all preference/media listeners on shutdown.

- [ ] **Step 4: Run controls/theme tests**

Run: `python -m unittest zotero-mica-glass.tests.test_plugin_contract -v`  
Expected: PASS.

- [ ] **Step 5: Commit controls**

```bash
git add zotero-mica-glass/chrome/content/preferences.xhtml zotero-mica-glass/chrome/content/preferences.js zotero-mica-glass/chrome/content/micaGlass.js zotero-mica-glass/tests/test_plugin_contract.py
git commit -m "feat: add Mica controls and theme sync"
```

### Task 6: Package, verify, and run Zotero smoke tests

**Files:**

- Modify: `zotero-mica-glass/scripts/build.py`
- Modify: `zotero-mica-glass/tests/test_plugin_contract.py`
- Create: `zotero-mica-glass/README.md`

- [ ] **Step 1: Write release-contract tests**

```python
def test_plugin_never_starts_a_helper_or_uses_undocumented_composition_api():
    sources = [
        (PLUGIN / "native/dwmBridge.js").read_text(encoding="utf-8"),
        (PLUGIN / "chrome/content/dwmBridgeCore.mjs").read_text(encoding="utf-8"),
    ]
    for forbidden in ["nsIProcess", "runAsync", "SetWindowCompositionAttribute", "cmd.exe"]:
        assert all(forbidden not in source for source in sources)

def test_build_artifact_is_installable_xpi(tmp_path):
    artifact = build(tmp_path / "Zotero-Mica-Glass.xpi")
    assert artifact.suffix == ".xpi"
    assert artifact.exists()

def test_two_builds_are_byte_identical(tmp_path):
    assert build(tmp_path / "one.xpi").read_bytes() == build(tmp_path / "two.xpi").read_bytes()
```

- [ ] **Step 2: Run release tests and verify failure**

Run: `python -m unittest discover -s zotero-mica-glass/tests -v`  
Expected: FAIL until README/build checks are complete.

- [ ] **Step 3: Document install, supported scope, and rollback behavior**

README must state Windows 11 + Zotero 9.0.6 support, the finite dialog matrix, required DWM fallback behavior, installation through Zotero Tools → Plugins, and the fact that disabling/uninstalling restores native attributes. Update the builder to write `dist/Zotero-Mica-Glass-0.1.0.xpi` reproducibly.

- [ ] **Step 4: Run automated verification and package**

Run: `python -m unittest discover -s zotero-mica-glass/tests -v`  
Expected: PASS.

Run: `python zotero-mica-glass/scripts/build.py`  
Expected: prints the absolute path of `dist/Zotero-Mica-Glass-0.1.0.xpi`.

- [ ] **Step 5: Perform manual Zotero 9.0.6 smoke test**

Install the generated XPI in a clean Zotero 9.0.6 profile. Verify main library, standalone reader, tab reader, note editor, preferences, the four allowed dialogs, light/dark/auto theme changes, Standard/Readability controls, repeated reader open-close, disable, re-enable, and uninstall. Confirm no duplicate style links, no persistent Mica after cleanup, and normal library operations.

- [ ] **Step 6: Commit release-ready artifacts and docs**

```bash
git add zotero-mica-glass/scripts/build.py zotero-mica-glass/tests/test_plugin_contract.py zotero-mica-glass/README.md
git commit -m "docs: document and verify Zotero Mica Glass"
```
