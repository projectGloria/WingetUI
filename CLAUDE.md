# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

"Winget Baby": a Windows desktop GUI for `winget` (install/upgrade/uninstall, search, update checks, curated presets). It's a single-file C# WinForms host (`WingetUI.cs`) that embeds WebView2 and serves a vanilla HTML/CSS/JS frontend from `ui/`. No git repo, no package manager, no test suite, no linter.

## Build & run

```powershell
.\build.ps1        # compiles WingetUI.exe (kills any running instance first)
.\WingetUI.exe
```

- `build.ps1` uses the in-box .NET Framework compiler `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`. **That compiler only supports C# 5**: no string interpolation (`$"..."`), no `?.`/`??=`, no `nameof`, no expression-bodied members, no `out var`, no tuples. Use `string.Format` and explicit null checks, matching existing code.
- If the three WebView2 DLLs are missing, `build.ps1` downloads the `Microsoft.Web.WebView2` NuGet package and extracts them. They must sit next to `WingetUI.exe`, as must `ui/` and `presets.json` (paths resolve from `AppDomain.CurrentDomain.BaseDirectory`).
- `build.ps1` also assembles the portable package: `dist\WingetBaby\` + `dist\WingetBaby-portable.zip`. All mutable state (presets, `meta-cache.json`, WebView2 profile, `winget.txt`) lives in `Data\` next to the exe (falls back to `%LOCALAPPDATA%\WingetBaby` if read-only); root `presets.json` is only a first-run seed.
- Frontend changes (`ui/*`) need no recompile, just restart the app. Launch with `--devtools` to enable F12/DevTools; they're off otherwise.
- `test_*.png` in the root are leftover screenshots, not test fixtures.

## Architecture

**Host ↔ UI bridge.** `ui/` is mapped to the virtual host `https://winget.app` via `SetVirtualHostNameToFolderMapping`. All communication goes through WebView2 web messages:
- JS → C#: `sendHost({ action: "...", ... })` → `OnWebMessageReceived` switch in `WingetUI.cs`.
- C# → JS: `SendToUi(new { type = "...", ... })` (serialized with `JavaScriptSerializer`, marshalled to the UI thread via `BeginInvoke`) → the `switch (msg.type)` listener at the bottom of `ui/app.js`.
Adding a feature almost always means a new `action` case in C# plus a new `type` case in JS. Keep the field names in sync by hand, since nothing checks them.

**winget invocation.** Two paths in C#:
- `RunWingetCapture(args)`: blocking capture for `list`, `upgrade`, `search`, `show`. Output is parsed by `ParseWingetTable`, which locates columns by finding the `Name/Id/Version/Available/Source/Match` header text above the `-----` line and slicing fixed-width. It's fragile against localized winget output and truncated columns.
- `HandleRunWingetTask`: streams stdout/stderr char-by-char as `task_log` messages (deduping spinner frames), tracked in `activeProcess` under `procLock` so `cancel_task` / window close can kill it. Only one mutating winget process runs at a time.

**Task queue lives in JS**, not C#. `enqueueTask` → `runNextTask` sends one `run_winget_task`; the host's `task_done` triggers the next. When the queue drains, the UI re-requests `scan_installed`.

**Metadata/icon pipeline** (async, after list results are already sent):
1. `AttachLocalRegistryIcons`: matches packages to Uninstall registry keys (HKLM, WOW6432Node, HKCU) by normalized display name / key name and inlines the `DisplayIcon` as a base64 PNG `localIcon`.
2. `ResolvePackageMetadata` (Parallel.ForEach, max 6): fetches manifests straight from `raw.githubusercontent.com/microsoft/winget-pkgs/...` (fast path), falls back to `winget show` and then an HTTP HEAD `Last-Modified` for release dates. Derives `iconUrl`/`fallbackIconUrl` from manifest `IconUrl`, Google S2 favicons for custom domains, or GitHub owner avatars. Results stream back as `package_metadata` messages and are cached in `metaCache`.
3. JS `resolveIconSources` layers its own hardcoded `KNOWN_ICONS` / `KNOWN_DOMAINS` maps, preset `domain` fields, and ID-prefix heuristics on top. `onAppIconLoad`/`onAppIconError` walk primary → fallback → local → initials avatar (S2's 16×16 default globe is treated as a failure).

**Frontend** (`ui/app.js`) is a single IIFE with one mutable `state` object and full `innerHTML` re-renders (`renderAll`, `renderPresets`, etc.), with event listeners re-bound after each render. It's not React. Don't introduce a framework or build step unless asked. All interpolated strings go through `escapeHtml`. Keep that up, since package names/descriptions come from external manifests.

## Data files

- `presets.json`: `{ categories: [{ id, name, icon, apps: [{ id, name, description?, domain?, badge?, altIds? }] }] }`. `altIds` lets one preset match alternate installed IDs (e.g. LTS variants). The UI rewrites the whole file (minified) on every preset add/remove via `save_presets`, so hand edits get reformatted.
- `export_winget_txt` writes a Markdown install list to `Data\winget.txt`.
- Startup: the splash in `index.html` stays up until the host sends `scan_complete` (bounded to ~20s of metadata resolution; stragglers continue behind the UI). `scan_progress` messages drive the splash icons and are ignored once it's hidden.
