# Winget Baby (`WingetUI`) — Full Codebase Analysis & Bug Report

## 1. Executive Summary

**Winget Baby** is a portable Windows desktop GUI wrapper around `winget.exe` built with a single-file C# 5 WinForms host (`WingetUI.cs`, 1,851 lines) embedding Microsoft Edge WebView2 and serving a vanilla HTML/CSS/JS frontend (`ui/index.html`, `ui/app.js`, `ui/styles.css`, totaling 3,846 lines), compiled via the in-box .NET Framework 4.x compiler (`build.ps1`).

The codebase is clean, dependency-light, and compiles with zero warnings under `/warn:4`. However, a full line-by-line audit reveals **several high-severity bugs and reliability issues**—most notably in `winget` table column offset parsing, process/cancellation lifecycle races, stale UI state synchronization, HTTP connection-pool throttling, single-quote HTML attribute escaping, and brittle publisher trust heuristics.

---

## 2. Codebase Architecture Overview

| Component | Path | Size | Role |
|---|---|---|---|
| **Build & Packaging** | `build.ps1` | 73 lines | Downloads WebView2 NuGet DLLs if missing, compiles `WingetUI.cs` with .NET 4.x `csc.exe` (C# 5), assembles `dist/WingetBaby/` + `WingetBaby-portable.zip`. |
| **Native Host** | `WingetUI.cs` | 1,851 lines | Frameless WinForms host (`FormBorderStyle.None`), DWM dark/rounded attributes, single-instance `Mutex`, WebView2 virtual host (`https://winget.app`), `winget.exe` process runner/parser, registry icon extractor, GitHub/Store metadata & trust resolver, 7-day `meta-cache.json`. |
| **Frontend Markup** | `ui/index.html` | 423 lines | Startup splash, 4-tab layout (`My Presets`, `Updates`, `Installed Apps`, `Winget Search`), collapsible console drawer, `Add App` modal, `Package Details` modal. |
| **Frontend Logic** | `ui/app.js` | 1,789 lines | Single-IIFE state store (`state`), full `innerHTML` re-renders, sequential task queue (`enqueueTask` / `runNextTask`), icon fallback chain, trust filtering, WebView2 message bridge. |
| **Frontend Styles** | `ui/styles.css` | 1,634 lines | Dark theme CSS variables, custom scrollbars, responsive card grid, tables, modals, splash animations. |
| **Seed Data** | `presets.json` | 1 line (5.4 KB) | Default curated categories and apps (`categories[]` with `id`, `name`, `icon`, `apps[]`), copied to `Data/presets.json` on first run. |

### Host ↔ UI Message Protocol
- **JS → C# (`sendHost`)**: `get_presets`, `save_presets`, `export_winget_txt`, `scan_installed`, `check_updates`, `search_winget`, `run_winget_task`, `cancel_task`, `open_file`, `open_url`, `get_package_details`, `window_drag`, `window_minimize`, `window_maximize`, `window_close`.
- **C# → JS (`SendToUi`)**: `window_state_changed`, `presets_loaded`, `presets_saved`, `exported_winget_txt`, `scan_status`, `scan_progress`, `installed_list`, `scan_complete`, `updates_status`, `updates_list`, `package_metadata`, `search_results`, `search_complete`, `package_details`, `task_started`, `task_log`, `task_done`, `task_cancelled`, `error`.

---

## 3. Critical & High-Severity Bugs

### 3.1. `ParseWingetTable` Column Offset Misalignment (`WingetUI.cs:1713-1747`)
In `ParseWingetTable`, the header line is stripped on the left before finding column start indices:
```csharp
int namePos = line.IndexOf("Name", StringComparison.Ordinal);
string cleanHeader = namePos >= 0 ? line.Substring(namePos) : line.TrimStart();
idStart = cleanHeader.IndexOf("Id", StringComparison.Ordinal);
verStart = cleanHeader.IndexOf("Version", StringComparison.Ordinal);
```
**Problem:** `idStart`, `verStart`, `availStart`, `srcStart`, and `matchStart` are calculated relative to `cleanHeader`, **not** `line`. Whenever `line` has leading characters before `Name` (such as leading spaces or a `winget` spinner artifact like `"- Name   Id..."` so `namePos > 0`), every subsequent `SafeSlice(line, idStart, ...)` on the data rows is shifted left by `namePos` characters, truncating or corrupting `name`, `id`, and `version`. The same offset bug affects the localized-header fallback (`cols[1].Index`, etc. on `cleanHeader`).

### 3.2. `scanBusy` Guard & Background Metadata Task Race (`WingetUI.cs:481-548`)
In `HandleScanInstalled(bool startup)`:
- `scanBusy` is set to `1` at entry, then `ResolvePackageMetadata` is launched via `Task.Run(...)`.
- On startup (`startup == true`), `work.Wait(20000)` only waits up to 20 seconds; on manual refresh (`startup == false`), it does **not** wait at all.
- Immediately after, the `finally` block sets `scanBusy = 0` and sends `scan_complete` **while `work` is still running in the background**.
- **Consequences:**
  1. Triggering another `scan_installed` (e.g., clicking Refresh or finishing a task queue) starts a second concurrent `ResolvePackageMetadata` batch while the first is still running, multiplying `winget show` and HTTP traffic.
  2. Conversely, if `winget list` itself is slow (`scanBusy == 1`), a new `scan_installed` call from `runNextTask()` (after an install/update/uninstall completes) hits `if (Interlocked.CompareExchange(ref scanBusy, 1, 0) != 0) return;` and is **silently dropped**, leaving the UI with stale installed/update state.

### 3.3. Cancellation Lifecycle Race & Duplicate `task_done` After Cancel (`WingetUI.cs:387-390`, `1533-1583`, `1631-1657`; `ui/app.js:1457-1461`, `1722-1745`)
When the user clicks Cancel:
1. JS clears `state.taskQueue = []` and sends `cancel_task`.
2. C# `OnWebMessageReceived` calls `KillActiveProcess()` synchronously on the UI thread (which can block the UI for up to 3,000 ms waiting on `taskkill.exe`) and immediately sends `task_cancelled`.
3. Meanwhile, `HandleRunWingetTask` exits `proc.WaitForExit()` on its background thread and **also sends `task_done`** with a non-zero exit code.
4. JS receives `task_cancelled` (clears `currentTask`), then receives `task_done` (logs `⚠ ... exited with code ...`, shows an error toast, and calls `runNextTask()` which triggers an extra `scan_installed`).
5. **Dispose race in C#:** In `HandleRunWingetTask`, `using (var proc = new Process { ... })` is nested *inside* `try { ... } finally { lock (procLock) { activeProcess = null; } }`. Therefore `proc.Dispose()` runs **before** `activeProcess` is set to `null`. If `KillActiveProcess()` acquires `procLock` in that window, `activeProcess` is non-null and already disposed, causing `ObjectDisposedException`.

### 3.4. Stale `state.updatesList` After Package Updates (`ui/app.js:754-762`, `1338-1344`, `1529-1539`)
`renderUpdates()` and `#btn-update-all` merge `state.installedList` (items with `.available`) and `state.updatesList` (populated by "Deep Scan Updates"). However, when `installed_list` arrives after an upgrade or refresh (`ui/app.js:1529-1539`), `state.updatesList` is **never cleared or pruned**. Packages updated after a Deep Scan continue to appear in the Updates tab and `Update All` queue until another Deep Scan is run.

### 3.5. XSS / Attribute Injection Risk in Details Modal Icon (`ui/app.js:1220-1227`, `1707`)
In `package_details` handling (`ui/app.js:1707`):
```js
iconWrap.innerHTML = `<img class="app-icon-img" src="${escapeHtml(msg.iconUrl)}" alt="" onerror="this.onerror=null; this.src='${escapeHtml(msg.fallbackIconUrl || "")}'" />`;
```
`escapeHtml` escapes `&`, `<`, `>`, and `"`, but **does not escape single quotes (`'`)**. Because `msg.fallbackIconUrl` comes from external `winget-pkgs` YAML manifests (`IconUrl` / `Homepage`) and is interpolated inside a single-quoted JS string inside `onerror="..."`, a URL containing `'` breaks out of the attribute handler. Additionally:
- If `msg.fallbackIconUrl` is `""`, `this.src=''` causes WebView2 to request the page URL (`https://winget.app/index.html`) as an image and show a broken-image icon instead of falling back to `localIcon` or the initials avatar like `renderAppIcon` does.

### 3.6. `ServicePointManager.DefaultConnectionLimit` Bottleneck (`WingetUI.cs:82`, `721`, `1099-1118`)
`MainForm` sets `ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12` but leaves `ServicePointManager.DefaultConnectionLimit` at the .NET Framework default of **2 connections per host**.
`ResolvePackageMetadata` runs with `MaxDegreeOfParallelism = 12` against `raw.githubusercontent.com`. Ten of the twelve worker threads block waiting for a connection slot, inflating latency and risking `HttpWebRequest.Timeout = 3000` failures under contention.

---

## 4. Medium-Severity Bugs & Functional Gaps

### 4.1. Out-of-Order Search Results (`WingetUI.cs:1243-1266`; `ui/app.js:1579-1592`)
`HandleSearchWinget` has no cancellation or sequence/query check. If a user submits Query A and quickly submits Query B, Query A's `search_results` / `search_complete` can arrive after Query B's, overwriting Query B's table and prematurely clearing `state.searchPending = false`.

### 4.2. Stale `#search-trust-note` When Search Returns 0 Results (`ui/app.js:1001-1003`, `1582-1583`)
When `search_results` returns `[]`, `app.js` replaces `#search-table-wrap` directly and never calls `renderSearch()`. Any previous `#search-trust-note` text (e.g., `"12 community packages hidden"`) remains visible above the empty-state message.

### 4.3. `isBusy` Check Ignores `altIds` in `renderPresetCard` (`ui/app.js:682-684`, `696-702`)
When a preset matches an installed package via `altIds` (e.g., `OpenJS.NodeJS` matched to `OpenJS.NodeJS.LTS`), the Update/Uninstall buttons enqueue `info.id` (`OpenJS.NodeJS.LTS`). However, `isBusy` only checks `app.id` (`OpenJS.NodeJS`), so the card fails to show the `"Working..."` pill while the task is queued or running.

### 4.4. `winget.exe` Argument Escaping Edge Cases (`WingetUI.cs:985`, `1250`, `1276`, `1484`)
`safeId` / `safeQuery` strip `"` (and `'` in `HandleRunWingetTask`), but do not handle trailing backslashes (`\`). Under Windows `CommandLineToArgvW` rules, `"foo\"` escapes the closing quote, allowing argument smuggling if an ID or search query ends with `\`.

### 4.5. `RunWingetCapture` Ignores `stderr` (`WingetUI.cs:1679-1687`)
`errTask` drains `StandardError`, but its result is never read or returned. When `winget.exe` fails and writes diagnostics only to `stderr`, `HandleScanInstalled` reports `"winget returned no installed packages."` instead of the actual error message.

### 4.6. `winget` Output Truncation & Localization Fragility (`WingetUI.cs:1401-1480`, `1696-1818`)
- **Truncated IDs:** `winget list` / `winget search` truncates long `Id` values with `…` when output exceeds console column width, producing invalid IDs that fail `winget show`, `install`, `upgrade`, and cache lookups.
- **CJK/Wide-char slicing:** `SafeSlice` slices by UTF-16 `char` index while `winget` aligns columns by terminal display width.
- **English-only `winget show` & table footers:** `ParseWingetShow` and `FetchSinglePackageMeta` match English keys (`"Description"`, `"Homepage:"`, `"Release Date:"`, `"upgrades available."`). Non-English Windows locales fail to extract metadata.
- **Dead code in `ParseWingetShow` (`WingetUI.cs:1469-1472`):** `else if (currentKey == "Description" && currentLines.Count < 10)` is unreachable because `else if (currentKey == "Description")` at line 1451 already matches unconditionally.

### 4.7. `detailsCache` Never Invalidates (`WingetUI.cs:77`, `1274-1318`)
`detailsCache` stores `winget show` results for the entire process lifetime. Upgrading a package does not evict its entry, so the Details modal continues displaying the old version, release date, and SHA-256 hash.

### 4.8. `ComputeTrust` False Negatives & Spoofing Vector (`WingetUI.cs:876-914`)
- **False negatives:** `if (pub.Length < 3) return false;` automatically marks any 2-letter publisher prefix (e.g., `HP.*`, `LG.*`, `EA.*`) as untrusted. `GitHub.cli` (`cli.github.com`) is also marked untrusted because `TryExtractCustomDomain` rejects `*.github.com` and the `pub == "github"` special case only allows `/features` and `/apps/desktop`. Because `"Verified publishers only"` is enabled by default (`ui/index.html:231`), legitimate packages are hidden.
- **False positives:** `ComputeTrust` returns `true` if **any** of `packageUrl`, `publisherUrl`, or `installerUrl` matches the ID prefix. A malicious manifest with `PackageUrl` pointing to the real vendor's GitHub org and `InstallerUrl` pointing elsewhere would pass.

### 4.9. Details Modal Missing `"Update"` Action (`ui/app.js:947-967`)
For an installed package that has an available update, `openAppDetailsModal` only shows `"Uninstall"`, omitting an `"Update"` action.

### 4.10. `open_url` Case Sensitivity & `SendToUi` Startup/Shutdown Handling (`WingetUI.cs:400-406`, `309-328`)
- `url.StartsWith("http://") || url.StartsWith("https://")` is case-sensitive (`HTTPS://` is silently ignored).
- `killer` process in `KillActiveProcess()` (`WingetUI.cs:1642`) is not wrapped in `using` (minor handle leak).

---

## 5. Low-Severity / UX & Code Quality Issues

| Area | Location | Issue |
|---|---|---|
| **Dead variable** | `ui/app.js:692-693` | `meta` and `dateSuffix` are computed in `renderPresetCard` and never used. |
| **Unwired UI affordance** | `ui/styles.css:908`, `ui/app.js:1445` | `.console-bar` has `cursor: pointer`, but clicking the bar does nothing (only `#btn-toggle-console` toggles). |
| **Table flex `<td>`** | `ui/app.js:880, 1097` | Setting `display: flex` directly on `<td>` instead of an inner `<div>` can misalign table cell bottom borders. |
| **Deprecated CSS** | `ui/styles.css:495-496` | `overflow-y: overlay` is deprecated in Chromium/WebView2 (overridden by `overflow-y: auto` on the next line). |
| **Duplicate CSS rule** | `ui/styles.css:771, 1629` | `.card-actions` is defined twice. |
| **Clipboard error handling** | `ui/app.js:657, 1168, 1504` | `navigator.clipboard.writeText(id)` is called without `.catch(...)`; toast `"Copied"` fires even if clipboard access rejects. |
| **No confirmation on destructive batch actions** | `ui/app.js:1322-1330` | `"Uninstall Selected"` immediately queues silent uninstalls for all selected apps with no confirmation prompt. |
| **Duplicate preset IDs across categories** | `ui/app.js:1433` | `form-add-preset` only checks the target category for duplicates, allowing the same `id` in multiple categories. |
| **Static `winget.txt` / YAML parsing** | `WingetUI.cs:1120-1160` | `ParseManifestYamlFields` does not strip inline YAML `#` comments or handle multi-line scalars. |

---

## 6. Prioritized Recommendations

1. **Fix `ParseWingetTable` column indices (`WingetUI.cs:1714-1747`):** Compute `idStart`, `verStart`, `availStart`, `srcStart`, and `matchStart` against the original `line` (or slice `line = line.Substring(namePos)` before slicing rows) so leading characters never shift every column.
2. **Fix `scanBusy` & `activeProcess` concurrency (`WingetUI.cs`):**
   - Keep `scanBusy = 1` until `ResolvePackageMetadata` actually finishes (or separate `listBusy` from `metaBusy`), and queue/coalesce a pending `scan_installed` request instead of silently dropping it.
   - Move `activeProcess = null` cleanup so `proc.Dispose()` happens *after* clearing `activeProcess` under `procLock`, run `taskkill` off the UI thread, and suppress `task_done` when a task was explicitly cancelled.
3. **Clear/prune `state.updatesList` on `installed_list` (`ui/app.js:1529`):** Reset or filter `state.updatesList` when a fresh `installed_list` arrives so updated packages disappear from the Updates tab immediately.
4. **Harden `escapeHtml` & Details modal icon fallback (`ui/app.js:1220, 1707`):** Escape `'` (`&#39;`) in `escapeHtml`, validate `http:`/`https:` URLs, and route the Details modal icon through `renderAppIcon` / `onAppIconError` instead of inline `onerror`.
5. **Increase `ServicePointManager.DefaultConnectionLimit` (`WingetUI.cs:82`):** Set `ServicePointManager.DefaultConnectionLimit = 16;` (matching `MaxDegreeOfParallelism = 12`) to eliminate HTTP connection starvation during startup scans.
6. **Track search query sequence (`WingetUI.cs:1243`, `ui/app.js:1579`):** Attach a monotonically increasing `searchSeq` (or compare `msg.query` against the active query) so stale search responses cannot overwrite newer results, and clear `#search-trust-note` on empty results.
