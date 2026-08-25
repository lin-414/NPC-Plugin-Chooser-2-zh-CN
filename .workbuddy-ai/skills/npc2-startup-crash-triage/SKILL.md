---
name: npc2-startup-crash-triage
description: Triage startup crashes / 闪退 in the NPC Plugin Chooser 2 (WPF/.NET 10) repo after merging upstream or changing localization. Use when the app crashes on launch with no/empty window. Covers the built-in CrashLog.txt + LogStartup.txt diagnostics, headless build-and-run reproduction, and the common post-merge failure classes (missing embedded resources, XAML xmlns:l, CharacterViewer.Rendering version mismatch, settings migration).
agent_created: true
---

# NPC2 Startup-Crash Triage

WPF/.NET 10 app (`NPC Plugin Chooser 2.csproj`, target `net10.0-windows10.0.19041.0`).
This repo merges a large upstream (`upstream/master` = NPC2 proper) and re-applies a
localization layer on top, so post-merge startup crashes are common. Use this skill to
find the real cause fast instead of guessing.

## Step 0 — The app already logs crashes for you
Two built-in mechanisms (see `App.xaml.cs` + `BackEnd/StartupLogger.cs`):
- **`CrashLog.txt`** — written next to the exe by `AppDomain.UnhandledException` /
  `DispatcherUnhandledException` (`LogCrash`). Catches even pre-UI crashes.
- **`StartupLog.html`** — enable by dropping an empty file named **`LogStartup.txt`**
  next to the exe; the whole startup sequence streams to `StartupLog.html` (the last
  row shows exactly which phase crashed).

Always ask the user for these two files first — they pin the failing step/stack in one shot.

## Step 1 — Reproduce headlessly (no display needed)
```bash
cd "<repo>\bin\Debug\net10.0-windows10.0.19041.0"
touch LogStartup.txt
dotnet "NPC Plugin Chooser 2.dll"        # hangs without a desktop; kill after ~30s
# then read StartupLog.html and CrashLog.txt
```
A no-Mods-folder run typically completes init and just hangs (message loop, no display) —
that proves core startup logic is fine and the crash is data/environment-specific
(Mods folder + real game data → autogen mugshot render / Live Tiles 3D viewport at startup).

## Step 2 — Common post-merge failure classes (check in this order)
1. **Stale build / missing embedded resources.** After a merge, rebuild. Verify the
   output dir actually contains what csproj declares as `<Content CopyToOutputDirectory>`:
   - `Localization/strings.json` + `strings.zh-CN.json` (a missing-JSON crash was fixed in
     commit `7d7efb1`; without them `TranslationService.GetString` returns empty and some VM
     init that depends on a non-empty result throws).
   - Native renderer DLLs `glfw3.dll` / `libbsarch.dll` / `lighting.json` / `shaders\*` are
     copied ONLY when `..\..\NPC Portrait Creator\out\build\x64-Release\*` exists
     (csproj conditional `<Content>`). If absent, first real render (GLFW init) fails.
   - Deploying via MO2: ensure the Localization dir + native DLLs are packaged too.
2. **XAML missing `xmlns:l`** for `{l:Loc}` bindings → `MC1000` build error (so build fails,
   not a runtime crash). Scan: any `*.xaml` using `l:Loc` must declare
   `xmlns:l="clr-namespace:NPC_Plugin_Chooser_2.Localization"`. (Fixed for ModIssuesView in
   `9575442`.)
3. **CharacterViewer.Rendering version mismatch.** csproj references the sibling
   `..\..\SynthEBD\CharacterViewer.Rendering` when present, else NuGet
   `SynthEBD.CharacterViewer.Rendering` 2.9.0. `App.xaml.cs` sets
   `requiredViewerVersion = new Version(2,9,0)` and only WARNs if lower. The 2.8/2.9 APIs
   (`AllowLoadOrderFallback`, `Exposure`, `DeprioritizeBelowDataFolder`) are used at
   *render* time — present-but-stale sibling renderers can still throw on first autogen.
   Align the SynthEBD sibling to 2.9.0 or switch to the NuGet package.
4. **Settings migration** in `BackEnd/UpdateHandler.cs` (`InitialCheckForUpdatesAndPatch`,
   version-gated). A 2.2.4→2.2.5 jump is a no-op (no intermediate gate fires). If a future
   jump crashes, inspect the `< X.Y.Z` migration branches.

## Step 3 — What is NOT the cause (verified for the 2.2.4→2.2.5 merge)
- `InitializeApplicationState` is benign (property assignment only).
- Localization format strings: every `GetTranslation(key, "text {0}")` fallback with a
  placeholder is already wrapped in `string.Format(...)`.
- Theme loading (`Themes/ThemeManager.cs`) has full fallback (missing theme → Dark, broken
  file skipped).
- Renderer APIs are present in the SynthEBD source (builds clean), so no `MissingMethodException`
  from the version number alone.

## Quick build command
```bash
"C:/Program Files/dotnet/dotnet.exe" build "NPC Plugin Chooser 2.csproj" -c Debug --nologo
```
(The `dotnet` binary may be flagged by sandbox policy — run with sandbox disabled.)
