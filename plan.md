# WinUI Repro App - Plan

A "Godbolt for XAML + C#", but local-first and built for WinUI3 bug triage.

Status: BUILDING. MVP host runs with in-process XAML live preview.

## Progress
- [x] Host app scaffolded: `src/ReproStudio.Host` (WinUI Blank template, .NET 10,
      WindowsAppSDK 2.2.0, root namespace `ReproStudio_Host`).
- [x] In-process Fast mode (first cut): proved the live-edit loop, then replaced
      by the runner split below.
- [x] `ReproStudio.Shared` (net8.0): `Snippet` schema + `SnippetIo` (atomic
      write + tolerant read). Referenced by host and runner. The contract.
- [x] `ReproStudio.Runner` (WinUI, UNPACKAGED): reads `--request <file>`, renders
      XAML via `XamlReader.Load`, compiles C# with Roslyn into a collectible ALC
      and calls `Setup(FrameworkElement root)`, watches the file for hot reload,
      applies theme/flowDirection, shows parse/compile/runtime errors in an
      InfoBar without crashing.
- [x] Host rewired: two editors (XAML + C#), debounced writes of `request.json`,
      launches + manages the runner process (path via REPROSTUDIO_RUNNER_EXE env
      var or `%LOCALAPPDATA%\winui-repro-app\runner-path.txt`), "Restart runner"
      button, kills the runner on close. Preview now lives in the runner window.
- [x] Verified end to end: host spawns runner, writes request, runner renders;
      hot reload + theme switch + graceful XAML errors all confirmed.
- [x] Repro C# can take the Window too. `Setup` parameters are filled by type, so
      `Setup(root)`, `Setup(window)`, and `Setup(root, window)` all work. Lets
      snippets repro windowing/AppWindow/titlebar/backdrop bugs. Verified by
      setting `window.Title` and `window.AppWindow.Resize(...)` from a snippet.
- [x] Quality-of-life polish:
      - Tab in the host editors inserts 4 spaces (Shift+Tab still navigates).
      - Snippets can call `Log("...")` (unqualified, via `using static ReproApi`)
        and lines show in a scrolling Log panel at the bottom of the runner window.
        Verified the snippet's `Log` reaches the panel sink at runtime.
      - Draggable splitter between the XAML and C# editors (CommunityToolkit
        `GridSplitter`).
- [x] DYNAMIC version pipeline WORKS (no per-version build):
      - `ReproStudio.Shared/RunnerProvisioner.cs`: lists stable WASDK versions from
        the NuGet flat-container, resolves component versions from the metapackage
        nuspec, downloads the component nupkgs, unzips, and overlays their loose
        runtime files onto a copy of the base runner in `versions\<ver>\`.
      - Base runner: code-only, self-contained, built ONCE at the lowest offered
        version (2.0.1) so picks forward-roll. Cached at `runner-base\`.
        IMPORTANT: build with `dotnet build` (self-contained), NOT publish - build
        keeps the app resources.pri the PRI resolution needs.
      - Host has a Windows App SDK version ComboBox. Selecting a version provisions
        (download/extract if not cached) then relaunches the runner against the
        same request file. Verified: host listed versions, provisioned + launched
        2.0.1 / 2.1.3 / 2.2.0, each loading its matching ui.xaml (2.0 / 2.1 / 2.2).
      - Antivirus can briefly lock freshly extracted files; the provisioner retries
        the directory move.
- [ ] save/open .wrepro snippets (last core todo).

- [x] Separate WinUI package picker + Browse for a local .nupkg:
      - Second dropdown overrides just the WinUI component, independent of the
        WASDK metapackage. "Default (matches WASDK)" plus stable WinUI versions
        from the `Microsoft.WindowsAppSDK.WinUI` feed.
      - "Browse .nupkg..." adds a local WinUI package (e.g. a private/nightly
        build) to the dropdown and selects it. Uses FileOpenPicker parented to the
        host HWND (MainWindow.Instance).
      - Provisioner: `WinUiOverride` (NuGet version or local nupkg). When set, the
        metapackage's WinUI is SKIPPED in the component loop and the override is
        overlaid last so it wins. Cache folder is `<wasdk>__winui-<ver>` or
        `<wasdk>__winui-local-<contentHash>` (rebuilt local package re-provisions).
      - Verified: WASDK 2.2.0 + WinUI override 2.1.0 -> folder 2.2.0__winui-2.1.0,
        ui.xaml 2.1 with Foundation from 2.2.0, runs. WASDK 2.2.0 + a local WinUI
        2.2.1 .nupkg -> folder 2.2.0__winui-local-<hash>, ui.xaml 2.2, runs.
      - Switch coalescing: if the user changes a picker while one provision is in
        flight, it re-runs with the latest selection when the current one finishes.

Build gotchas learned (important):
- The parent repo's `D:\jecollin\directory.build.props` redirects OutDir/IntDir to
  `_build\...`, which hides the packaged .exe from the winapp CLI. We added an
  empty `winui-repro-app/Directory.Build.props` to isolate our app and restore the
  default `bin\<Platform>\<Config>\<TFM>\win-<rid>` layout.
- `BuildAndRun.ps1` prefers VS2022's MSBuild, which resolves .NET SDK 9.0.315 and
  fails on net10 (NETSDK1045). Use `dotnet build` / `dotnet run` instead (SDK
  10.0.201). `dotnet run` auto-launches via winapp (package identity).
- A running unpackaged Runner LOCKS its own .exe, so rebuilding the runner fails
  with MSB3027 file-lock errors until you kill all ReproStudio.Runner processes.
- Packaged (AUMID-activated) host does not reliably inherit console env vars, so
  the runner path also falls back to a per-user config file.
- Snippet JSON reads are case-insensitive (`PropertyNameCaseInsensitive`). The C#
  field serializes as `cSharp` (camelCase of "CSharp"); without case-insensitive
  reads, a hand-written `"csharp"` key silently bound to null and the C# was
  skipped. Roslyn only sees ALREADY-LOADED assemblies, so the compiler touches
  `AppWindow`/`SizeInt32` types at startup to force their assemblies to load,
  otherwise `using Windows.Graphics;` in the default usings fails to resolve.

## Decisions so far
- Local-first desktop playground (not hosted, for now).
- Fast mode first (XamlReader.Load + Roslyn for C#). Full mode is the escape hatch.
- Repro runs in a separate Runner.exe, self-contained WASDK, version isolated.
- Versions acquired by NuGet restore on-demand, cached per version.
- Runner shows its OWN separate window (not reparented into the host) for now.
- Editor is a plain TextBox for the MVP. Monaco can come later.
- C# is in scope, so we need Roslyn.
- No sandboxing of the Roslyn C# for now. It runs arbitrary local code. Revisit later.


## The problem

We file a lot of WinUI3 bugs. A ton of them are tiny: a few lines of XAML or C#.
Spinning up a whole repo + project for each one is annoying. We want to paste a
snippet, see it run, tweak it, and share it.

## The pitch

```
  edit XAML/C#  ->  parse/compile  ->  swap into "stage"  ->  see it live
       ^                                                         |
       +---------------- hot reload on change (debounced) -------+
```

One window. Editors on the left, a live clickable repro on the right. Pick a
WASDK version, pick a theme, hit share.

Why not real Godbolt? Godbolt's output is static text (assembly). Ours is a
live, clickable window. Lots of WinUI bugs only show up when you hover, click,
resize, or change DPI/theme. A screenshot won't cut it.

Why not a browser? WinUI3 only runs on Windows with the Windows App SDK. No web
sandbox like Blazor. So we need a real host process. That is fine - we are
local-first anyway.

## How a snippet becomes running UI

Two modes:

### Fast mode (start here)
- `XamlReader.Load(xamlString)` parses XAML at runtime. No build step. Instant.
- Roslyn compiles any C# into an in-memory assembly for event handlers / logic.
- Great for: layout, controls, styling, theming, RTL, DPI bugs.
- Honest limits:
  - No `x:Bind` (compiled bindings). Only `{Binding}` works.
  - No `x:Class` code-behind wiring. We wire events by hand after load.
  - Custom types must be in an assembly the runner already references.

### Full mode (the escape hatch)
- "Doesn't repro? Click here." We generate a real temp WinUI3 project,
  `msbuild` it, and launch it.
- Slower (seconds, not instant) but 100% fidelity. x:Bind, x:Class, everything.

Plan: ship Fast mode first. Add Full mode when Fast mode can't repro something.

## The big one: switching WASDK / WindowsAppRuntime versions

Goal: pick any WASDK version (1.5 vs 1.6 vs nightly) and run the repro on it,
fast, without polluting anything. This is huge for bisecting regressions.

Key facts that shape the design:
- A process loads exactly ONE WASDK runtime. You cannot have 1.5 and 1.6 live in
  the same process.
- Self-contained deployment copies the WASDK runtime DLLs next to the exe. No
  machine-wide install, no bootstrapper picking a framework package. The exe
  loads its own local copy. This is how we get ARBITRARY versions cleanly.
- DPI awareness is per-process. Theme/FlowDirection are per-content. So a fresh
  process per repro gives us clean DPI toggles too.
- A repro can crash. A separate process means the host survives and can show the
  crash + stack. Big win.

So: run the repro in a separate child process ("the runner"), self-contained at
the chosen version.

```
+-------------------------------------------------------+
|  Playground host  (UI, editors, version picker)       |
|  - manages snippets                                   |
|  - spawns + talks to runner processes                 |
|  - shows the rendered repro                           |
+-------------------------------------------------------+
        | launch child process (per chosen version)
        | + send {xaml, csharp, theme, dpi} over IPC
        v
+-------------------------------------------------------+
|  Runner.exe  (self-contained WASDK 1.x.y)             |
|  - thin WinUI3 app, one blank "stage" panel           |
|  - loads snippet via XamlReader.Load (+ Roslyn C#)    |
|  - renders the repro, reports errors back             |
+-------------------------------------------------------+
```

### Where do arbitrary versions come from?  (REVISED: build-once + runtime file copy)

PROVEN by prototype (see "Version pipeline prototype" below). We do NOT build a
runner per version. Instead:
- Build the runner ONCE: code-only (no XAML), self-contained, version-agnostic.
- To run a version: copy that version's loose WASDK runtime files next to a copy
  of the one runner, then launch. Switch version = different folder = relaunch.

Why this beats per-version build: no dotnet publish per version (minutes), no .NET
SDK needed on the user's box at version-switch time, switching is just a file copy.

Where the files come from (NO MSIX cracking needed - loose files are in the nupkgs):
- `Microsoft.WindowsAppSDK.Foundation/<ver>/`
  - `runtimes-framework\win-x64\native\`  -> Microsoft.WindowsAppRuntime.dll, MRM.dll,
    Microsoft.Windows.ApplicationModel.Resources.dll, Microsoft.WindowsAppRuntime.pri
  - `runtimes\win-x64\native\`            -> Microsoft.WindowsAppRuntime.Bootstrap.dll
- `Microsoft.WindowsAppSDK.WinUI/<ver>/`
  - `lib\net6.0-windows10.0.17763.0\`     -> Microsoft.WinUI.dll (managed projection)
  - `runtimes-framework\win-x64\native\`  -> Microsoft.ui.xaml.dll, Microsoft.UI.Xaml.Controls.dll,
    Microsoft.UI.Xaml.Controls.pri, localized resource folders

Component version mapping: the metapackage version (e.g. 2.2.0) maps to specific
Foundation / WinUI / InteractiveExperiences versions (e.g. Foundation 2.1.0). Read
these from the metapackage's deps/nuspec, not assume they match.

### CRITICAL learnings from the prototype
1. The runner MUST be code-only (no .xaml files). Compiled XAML is version-stamped
   and won't load on a swapped runtime (that was the original crash). Done: the
   runner is now code-only - hand-written Program.Main, App implements
   IXamlMetadataProvider via Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider,
   adds XamlControlsResources in code, builds its window UI in C#.
2. Self-contained output MUST include the app resources.pri (ReproStudio.Runner.pri),
   or XamlControlsResources throws "Cannot locate resource from
   ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml". `dotnet publish` dropped
   it; the normal build keeps it. So: use build, or copy the app .pri into the output.
   (This is the "tell the runtime where the controls PRI is" concern - the answer is
   the app resources.pri bootstraps MRT, which then resolves ms-appx:/// to the local
   Microsoft.UI.Xaml.Controls.pri. There is no public "set pri path" API.)
3. Version binding was NOT a problem for our thin runner. A runner built against
   2.2.0 ran fine on swapped-in 2.0 native runtime (ui.xaml.dll product version 2.0
   confirmed loaded). Safest still: build against the OLDEST version we want to
   support, since forward-roll is the reliable direction.

Listing versions for the picker:
- Pull from the NuGet flat-container index, e.g.
  `https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk/index.json`
  gives every published version (stable + preview). Verified: 115 versions, stable
  includes 1.8.x, 2.0.1, 2.1.3, 2.2.0.
- "Any feed": the host writes a `NuGet.config` so we can add internal / nightly
  feeds later without changing the design.

Gotchas to handle:
- Need to download the component nupkgs (Foundation + WinUI at minimum) and extract
  the loose files. The metapackage itself is just dependencies.
- A running unpackaged runner LOCKS its files; switching versions launches a fresh
  copy in a different folder, so that is fine, but rebuilding the runner needs all
  runner processes killed first.

### Host <-> Runner IPC

We do this in two steps. v1 is dead simple (files on disk). v2 upgrades the
transport to a named pipe WITHOUT changing the message shapes.

#### v1 (DO THIS FIRST): files on disk
The file IS the protocol. Easy to debug, hand-edit, and it doubles as our
save-snippet format. One schema, two jobs.

```
  Host writes  ->  request.json (the snippet)  ->  Runner reads + renders
                        ^                                  |
                        +--- FileSystemWatcher, re-read on change (hot reload)
```

- Request in: host writes the snippet (xaml, csharp, theme, flowDirection) to a
  known file in a per-runner temp dir. Runner watches it.
- Result out: start with NONE. The runner shows its own errors in its own window
  (error overlay). Add a `result.json` (ok / error / stack / hwnd / loaded
  version) the moment we want host-side errors or window snapping.
- Race safety: write to a temp name then rename (atomic on the same drive).
  Retry the read on IO error.
- Hot reload: FileSystemWatcher + last-write-wins. Re-read the whole file, swap
  the stage. No seq needed (always read the latest).
- Version / DPI still launch args -> relaunch on change. Unchanged.

Clean upgrade path: the message shapes below (render / ready / error) map straight
onto request.json / result.json. So v2 just swaps the transport.

#### v2 (LATER): named pipe  (SPEC)

Transport: a named pipe per runner. The host is the pipe server (it outlives
runners). The host makes a GUID, names the pipe after it, and passes that name to
the runner as a launch arg. Messages are JSON Lines: one JSON object per line,
UTF-8. XAML/C# newlines get escaped inside JSON strings, so framing stays clean.

Why not stdin/stdout? The runner is a GUI app with no console, and we don't want
to tangle protocol bytes with stray Debug output. A pipe keeps it clean.

Launch:
```
Runner.exe --pipe winui-repro-<guid> --wasdk 1.6.250205 --dpi 150
```
Note: DPI awareness is per-process, so `--dpi` is a launch arg, NOT a live render
field. Changing DPI = relaunch (same as changing version). DPI handling is coarse
for now (we may just fake scale via RasterizationScale). Park the fancy version.

Envelope: every message has a `type`. Render-ish messages carry a `seq` (a number
that only goes up) so the runner can drop stale renders and the host can correlate
replies.

Host -> Runner:
- `render` - the main event. Full snippet every time (snippets are tiny, no diffing).
  ```
  { "type":"render", "seq":7,
    "xaml":"<Grid>...</Grid>",
    "csharp":"public static void Setup(FrameworkElement root){...}",  // optional
    "theme":"Dark",                 // Light | Dark | Default
    "flowDirection":"LeftToRight",  // or RightToLeft
    "background":"#202020" }        // optional stage bg
  ```
- `shutdown` - close gracefully. Host kills the process if it doesn't go.
- `ping` - optional liveness check (process-exit watching may be enough).

Runner -> Host:
- `ready` - sent once at startup, after the window is up.
  ```
  { "type":"ready", "wasdkVersion":"1.6.250205.1", "pid":12345, "hwnd":"0x000A1234" }
  ```
  The host uses `hwnd` to snap/position the runner window. The host owns
  positioning via SetWindowPos; no message needed for that.
- `rendered` - ack a good render. `{ "type":"rendered", "seq":7, "ok":true }`
- `error` - something went wrong, tied to a seq, tagged by phase.
  ```
  { "type":"error", "seq":7,
    "phase":"xaml" | "csharp-compile" | "runtime",
    "message":"Failed to assign to property 'Foo'",
    "line":12, "column":8,   // when we have it (XAML/Roslyn give positions)
    "stack":"..." }          // for runtime exceptions
  ```
- `log` - optional. Forward captured Debug.WriteLine so the host can show a
  console pane.

Three flows:

1) Startup handshake
```
Host:   launch Runner.exe --pipe G --wasdk V --dpi D
Runner: connect to pipe, init WinUI, open its window
Runner: -> ready { wasdkVersion, pid, hwnd }
Host:   snap the window, then send the first render
```

2) Hot reload (the common loop, NO relaunch)
```
user types -> debounce ~300ms -> Host -> render { seq:N, xaml, csharp, ... }
Runner:
  - if csharp changed: recompile via Roslyn into a fresh COLLECTIBLE
    AssemblyLoadContext (so old keystroke-assemblies unload, no leak)
  - XamlReader.Load(xaml) -> new root element
  - call the snippet entry point to wire handlers / set DataContext
  - swap stage.Content = newRoot
  - -> rendered { seq:N, ok:true }   (or -> error)
Stale drop: if a newer seq showed up while busy, skip rendering the old one.
On any error, keep the last good content on screen + show an error overlay.
```

3) Version (or DPI) change = relaunch
```
Host: -> shutdown (then kill if stubborn)
Host: spawn a new runner for the new version/dpi
Host: wait for ready, then re-send the current snippet
```

Error phases, concretely:
- `xaml`: XamlReader.Load throws XamlParseException. Message + position if we have it.
- `csharp-compile`: Roslyn diagnostics (line/col + text). Can send a list.
- `runtime`: exception while building the tree or in an event handler. We wrap the
  swap + handler calls in try/catch and report the stack.

C# entry-point contract (Fast mode):
- The snippet exposes a known static method the runner calls after XamlReader.Load,
  e.g. `public static void Setup(FrameworkElement root)`. The snippet uses
  `root.FindName(...)` to grab named elements and wire events / set DataContext.
- Keep it dead simple at first. We can grow the contract later.

### Showing the runner's window in the host  (DECIDED: separate window for now)
Three options, easy to hard:
1. Separate top-level window snapped beside the host. Simplest. Fine for v1.  <-- doing this
2. Reparent the runner HWND into the host window (SetParent). The integrated
   Godbolt feel. Fiddly with WinUI3 / AppWindow.
3. Frame streaming (runner renders to a shared surface, host displays). Most
   work. Only needed if we ever go hosted.

Plan: the runner just opens its own window. Revisit reparenting (option 2) later
if the two-window dance gets annoying.

### Known limit to note
Unpackaged self-contained is easiest, but some APIs need package identity. A
later "packaged mode" could matter for certain bugs. Park it for now.

## Sharing (local edition)
- Save a snippet as one self-contained file: `snippet.xaml` + `snippet.cs` +
  `meta.json` (WASDK version, theme, dpi, etc).
- Anyone with the tool opens it and gets the exact repro.
- Later: a "publish to gist" button for true one-click links to drop in ADO bugs.

## Open questions
- How do we want the version picker to source versions? nuget.org only at first,
  or wire in internal/nightly feeds from day one?
  - My pick: nuget.org only for v1. Make the feed list config-driven from the
    start (a NuGet.config the host writes) so adding internal/nightly later is a
    one-line change, not a redesign. Cache the version list, refresh on demand.
- Do we care about packaged-identity repros soon, or is unpackaged fine to start?
  - My pick: unpackaged is fine to start; it covers most bugs. Add a "packaged
    mode" checkbox later that builds the runner as MSIX + sparse/registered
    package identity. Detect "needs identity" failures and nudge the user to flip
    it on.

More open issues (my quick takes, for your review):
- XAML can't see C# types in Fast mode. XamlReader.Load won't resolve a class
  defined in the snippet's C#. Pick: code-only-behind in v1 (XAML stays pure,
  C# wires it via Setup). Full mode handles XAML-referencing-custom-types.
- Roslyn metadata references must match the runner's WASDK version. Pick: the
  runner hands its own loaded WinUI/WinRT assemblies to Roslyn as references, so
  the snippet always compiles against the exact version on screen.
- Focus stealing: a relaunching runner window grabbing focus is annoying while
  typing. Pick: open the runner no-activate and keep host focus.
- Debounce vs heavy recompiles: only recompile C# when the C# text actually
  changed; XAML-only edits skip Roslyn. Pick: hash each pane, recompile on diff.
- First-run cost: first build of a version is slow. Pick: show a clear "building
  WASDK x.y.z..." state, and pre-warm the last-used version on startup.
- Cache growth: cached runners pile up. Pick: simple LRU cap (e.g. keep last 5),
  plus a "clear cache" button.

## Parked for later
- Editor: move from plain TextBox to Monaco in a WebView2 (real syntax
  highlighting + error squiggles, the Godbolt feel).
- Window: reparent the runner HWND into the host for the integrated look.
- Sandboxing the Roslyn C#. For now it runs arbitrary local code, which is fine
  because it is our own machine and our own snippets.

## Concrete design: schema, projects, build pipeline

### 1. Snippet / request schema
One JSON file does triple duty: the saved snippet, the shared artifact, and the
IPC request the runner reads. Single file = atomic writes (temp + rename) and
easy sharing (drop one file in an ADO bug).

```
{
  "schemaVersion": 1,
  "title": "Button click crash",
  "notes": "repro for bug 12345",      // optional freeform
  "wasdkVersion": "1.6.250205",         // LAUNCH-TIME (picks the runner)
  "dpi": 100,                           // LAUNCH-TIME
  "theme": "Default",                   // LIVE: Default | Light | Dark
  "flowDirection": "LeftToRight",       // LIVE: LeftToRight | RightToLeft
  "background": null,                   // LIVE: optional stage bg, e.g. "#202020"
  "xaml": "<Grid>...</Grid>",           // LIVE
  "csharp": ""                          // LIVE, optional
}
```

- `schemaVersion` lets us evolve the format without breaking old files.
- LAUNCH-TIME fields (wasdkVersion, dpi): changing one = relaunch the runner.
- LIVE fields: the runner re-reads + swaps, no relaunch.
- The host writes this to `%TEMP%\winui-repro-app\runner-<guid>\request.json`.
  The runner watches that file. Same shape as the saved snippet.
- File extension for saved snippets: `.wrepro` (it is JSON inside; a custom
  extension lets us file-associate "open with the tool"). Bikeshed later.
- Readability note: embedded XAML/C# show as escaped `\n` in JSON, which is ugly
  to hand-read in a bug. Alternative is a sectioned text file (--- xaml --- /
  --- csharp ---). Going JSON for now since the tool is the editor. Revisit if
  raw readability in bugs matters a lot.

### 2. Project structure
Three projects. The host never changes runtime; only runners do.

```
winui-repro-app/
  plan.md
  ReproStudio.sln
  src/
    ReproStudio.Host/      WinUI3 app. The editor UI. Pinned recent WASDK.
                           (Dogfood WinUI3. WPF is the fallback if it fights us.)
    ReproStudio.Runner/    WinUI3 thin app. The stage host. Built PER-VERSION on
                           demand via dotnet publish. Lives here for dev too.
    ReproStudio.Shared/    Plain C# lib (net8.0). The schema POCO + JSON read/
                           write + atomic-write + file-watch helpers. The
                           contract. Referenced by BOTH host and runner.
```

Runner cache (NOT in repo): `%LOCALAPPDATA%\winui-repro-app\runners\<...>\`.

### 3. Runner build pipeline
Goal: given version V, produce a self-contained-WASDK runner at V, cached.

Runner csproj key props:
```
<OutputType>WinExe</OutputType>
<TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>  // WASDK self-contained
<WindowsPackageType>None</WindowsPackageType>                  // unpackaged
<SelfContained>false</SelfContained>                           // .NET stays SHARED
<WasdkVersion Condition="'$(WasdkVersion)'==''">1.6.250205</WasdkVersion>
...
<PackageReference Include="Microsoft.WindowsAppSDK" Version="$(WasdkVersion)" />
```

Host build command (run async, capture stdout/stderr):
```
dotnet publish src/ReproStudio.Runner/ReproStudio.Runner.csproj
  -c Release -r win-x64 -p:WasdkVersion=<V>
  -o <cacheDir>\<key>\<V>
```

Host flow when user picks version V:
1. If `<cacheDir>\<key>\<V>\ReproStudio.Runner.exe` exists -> launch it.
2. Else -> show "Building runner for WASDK V..." -> run publish -> on success
   launch; on failure show the build log in an error pane.

Gotchas baked in:
- Cache key must include the RUNNER's own code version, not just V. If we edit
  Runner.cs, old cached runners are stale. So key = `<runnerBuildId>\<V>`. Ship a
  new host -> runners rebuild. Simple and safe.
- Only build one version at a time (guard); never rebuild what is cached.
- First build per version downloads the WASDK package (tens of MB) -> slow once,
  then fast. Show the building state clearly + pre-warm last-used version.
- TFM/RID: target one TFM for v1 (net8.0-windows10.0.19041.0). Add a version->TFM
  map when an older/newer WASDK needs a different one.
- Needs `dotnet` SDK on PATH. Check at startup, say so clearly if missing.
- Feeds: default nuget.org for v1. Host writes a NuGet.config later for
  internal/nightly.


1. Host shell: split view, editors, blank stage, debounced change events.
2. Fast mode in-process first (no runner yet): XamlReader.Load + swap content.
   Prove the live-edit loop feels good.
3. Pull the stage into a separate Runner.exe + IPC. Host now just drives it.
4. Self-contained runner build + version cache + version picker.
5. Roslyn C# support and error reporting.
6. Save / open snippet files.
7. Nice-to-haves: theme/DPI/RTL toggles, Full mode, Monaco, gist sharing,
   HWND reparenting, crash/stack capture.
