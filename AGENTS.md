# AGENTS.md — WingetUI

Windows desktop GUI for `winget` (install/upgrade/uninstall, search, update checks, presets): single-file C# WinForms host embedding WebView2, serving a vanilla HTML/CSS/JS frontend from `ui/`.

## Commands
- Build: `.\build.ps1` (kills running `WingetUI.exe`, compiles with in-box `csc.exe`, fetches WebView2 DLLs if missing, assembles `dist\WingetUI\` + `dist\WingetUI-portable.zip`)
- Run: `.\WingetUI.exe` (add `--devtools` to enable F12/DevTools)
- Installer: `npx --yes gloria-setup@0.2.0 --app dist\WingetUI --manifest gloria.toml --out dist` (GloriaSetup). CI: `.github/workflows/release.yml` builds and publishes `v<version>` when a push to `main` changes the version.
- Test / Lint: none exist. Verify by building and exercising the app.

## Architecture
- `WingetUI.cs`: whole host. Maps `ui/` to `https://winget.app` via `SetVirtualHostNameToFolderMapping`; JS→C# via `sendHost({action})` → `OnWebMessageReceived` switch; C#→JS via `SendToUi(new { type })` → `switch (msg.type)` at the bottom of `ui/app.js`.
- `ui/app.js`: single IIFE, one mutable `state`, full `innerHTML` re-renders (`renderAll`, `renderPresets`) with listeners re-bound each render. Not React.
- **winget calls:** `RunWingetCapture` (blocking; `list`/`upgrade`/`search`/`show`) parsed by `ParseWingetTable` (fixed-width slicing from header text above `-----`; fragile vs localized/truncated output). `HandleRunWingetTask` runs winget under a ConPTY (`PtyProcess`, pipe fallback) because winget only emits progress to a terminal; `WingetOutputParser` strips VT codes and turns OSC `9;4` + "MB / MB" into `task_progress`, other lines into `task_log`. After success it polls `winget list -e --id` (`VerifyTaskOutcome`) since Store installs and many uninstallers finish after winget exits; `task_done` carries `verify` + the fresh `installed` row.
- **Task queue lives in JS:** `enqueueTask` → `runNextTask` sends one `run_winget_task`; `task_done`/`task_cancelled` (both keyed by `taskId`) trigger the next; `applyTaskResult` patches state immediately, and the drained queue re-requests `scan_installed`. `renderAll` must cover every view showing install state (incl. Search and the details modal).
- **Metadata/icons:** `AttachLocalRegistryIcons` (Uninstall keys HKLM/WOW6432Node/HKCU) → `ResolvePackageMetadata` (Parallel.ForEach, max 12; raw.githubusercontent winget-pkgs manifests → `winget show` → HEAD `Last-Modified`) streams `package_metadata`, cached in `metaCache`. JS `resolveIconSources` layers `KNOWN_ICONS`/`KNOWN_DOMAINS`; `onAppIconLoad`/`onAppIconError` walk primary → fallback → local → initials (S2 16×16 globe = failure).
- **State:** mutable data (presets, `meta-cache.json`, WebView2 profile, `winget.txt`) lives in `Data\` next to the exe, falling back to `%LOCALAPPDATA%\WingetUI`; root `presets.json` is only a first-run seed.
- **Startup:** splash in `ui/index.html` waits for `scan_complete` (~20s metadata cap); `scan_progress` drives splash icons.
- **Version Manifests:** `WingetUI.cs` (`AssemblyVersion`, `AssemblyFileVersion`, `AssemblyInformationalVersion`), `gloria.toml` (`version`). Keep in lockstep.

## Personal Rules (Brain)
- **Commit Format:** Write one plain sentence stating what changed and why; do not use `feat:`/`fix:`/`chore:` conventional-commit prefixes.
- **Commit & Push Policy:** Never run `git commit`, `git tag`, or `git push` unless explicitly asked; never force-push to `main`. When the user says "commit and push", verify the build and tests pass, confirm version manifests are bumped and synced for code/packaging changes, commit on `main`, and push to `origin main` (no manual release tags; pushing to `main` triggers `.github/workflows/release.yml` to build with `gloria-setup` and publish to GitHub Releases).
- **Path Privacy:** Always use `C:\Users\you` in documentation, comments, and examples; never hardcode the local machine's real username.
- **Release Workflow Check:** Before pushing to `main`, check `.github/workflows/release.yml` against `.brain/github-actions.md` (runs on `push: branches: [main]`, builds and releases only when the version changed). If it's missing or set up differently, bring it in line in the same push and tell the user what changed.
- **Window Chrome:** Disable native OS titlebars; build an integrated frameless titlebar with drag region, double-click maximize, and custom min/max/close controls.
- **Desktop Shell Lock:** Lock the app shell to `100vw x 100vh` (`overflow: hidden`) with internal scroll panes and `user-select: none` on UI chrome.
- **Scrollbars:** Ban raw OS scrollbars; use thin (4–6px) themed scrollbars with transparent tracks and zero layout shift.
- **Interactive Controls:** Custom-style all form controls; define explicit `hover`, `active`, `focus-visible`, and `disabled` states with snappy 120–180ms transitions.
- **Git Branching:** Never create new branches or git worktrees. Always work directly on `main` until explicitly told otherwise.
- **Versioning (Minimal Changes):** When runtime code or packaging changes, bump the alpha number by `+1` (e.g., `0.1.0-alpha.1` -> `0.1.0-alpha.2`) across all version manifests at most once per uncommitted change set (check `git diff HEAD` first; if the working tree version is already ahead of `HEAD`, do not bump again). Skip bumps on docs-only or read-only tasks.
- **Versioning (Major/Minor Changes):** Before changing minor/major versions or dropping `-alpha` on breaking or major feature work, stop and ask for the exact target version.

## Playbooks (Read Before Task)
- **Building & Packaging:** Read `.brain/building-the-project.md` before packaging, compiling release binaries, or building an installer.
- **GitHub Actions & Releases:** Read `.brain/github-actions.md` before creating/modifying `.github/workflows/*.yml`, setting up release automation, or preparing a first release push.

## Repo Guardrails & Gotchas
- **C# 5 Only:** `build.ps1` uses `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`: no `$"..."`, `?.`, `??=`, `nameof`, expression-bodied members, `out var`, or tuples. Use `string.Format` and explicit null checks.
- **Runtime Layout:** The three WebView2 DLLs, `ui/`, and `presets.json` must sit next to `WingetUI.exe` (paths resolve from `AppDomain.CurrentDomain.BaseDirectory`).
- **Frontend Edits:** `ui/*` changes need no recompile, just an app restart.
- **Bridge Field Names:** A feature usually needs a new C# `action` case plus a JS `type` case; nothing checks field names, so keep them in sync by hand.
- **One Mutating Process:** Only one mutating winget process runs at a time; `activeTaskId` is claimed synchronously in the message handler; `cancel_task` (by `taskId`) and window close kill the tree via `activePid`.
- **Escaping:** Route every interpolated string through `escapeHtml`; names/descriptions come from external manifests.
- **No Framework:** Don't introduce a framework or build step for `ui/` unless asked.
- **Presets File:** `presets.json` schema is `{ categories: [{ id, name, icon, apps: [{ id, name, description?, domain?, badge?, altIds? }] }] }`; `save_presets` rewrites it minified, so hand edits get reformatted.

## Done Criteria
- `.\build.ps1` completes with "Build succeeded" and produces `dist\WingetUI-portable.zip`.
- For UI/bridge changes, launch `.\WingetUI.exe` (with `--devtools` if needed) and exercise the affected view.
- For code/packaging changes, versions match across all three `WingetUI.cs` assembly attributes and `gloria.toml` (the Release workflow fails if they differ).

<!-- brain-sync: 5f20fae6c0e6 -->
