# How it works

Back to the [README](../README.md).

Skip this unless you want the guts. Short version: it's two processes talking
through a JSON file, and your C# gets compiled at runtime with Roslyn.

## The processes

```
 +---------------------+
 |  ReproStudio.exe    |  console host, no WASDK
 +---------------------+
            |  request.json (JSON on disk)
            v
 +----------------------+
 |  ReproStudio.Runner  |  the preview window
 |  self-contained WASDK|  one per version
 +----------------------+

  both processes reference ReproStudio.Shared
  (Snippet, RunnerHost, RunnerProvisioner, PackagedRunnerLauncher, AppLayout)
```

- **Cli** is the console host. No WASDK reference at all.
- **Runner** is a separate, throwaway process that does the actual rendering.
  There's one Runner per WASDK version.
- **Shared** holds the `Snippet` contract and IPC used by both processes, plus
  the CLI's launcher, provisioner, and file-layout rules.

Why separate processes? So we can render the same snippet against *different*
WASDK versions. Each version gets its own Runner exe with that version's runtime
DLLs next to it. The host just launches whichever one you asked for.

Notably, `Shared` uses `Windows.Management.Deployment.PackageManager` to register
the packaged runner. That comes from the Windows SDK projection (free with a
`net10.0-windows` TFM) and needs **no** Windows App SDK, which is what lets the
console host stay WASDK-free.

## Running different WASDK versions

This is the heart of the tool, and the trickiest part. The goal: run the same
Runner against WASDK 1.5, or 1.7, or 2.2, without rebuilding it each time.

### The base runner (built once)

We build the Runner **once**, self-contained, against the latest WASDK. That
build - the "base" - is found in one of two places:

| Deployment | Where the base comes from |
|---|---|
| Normal build or portable bundle | `runner-base\` next to the host exe |
| Legacy fallback | `%LOCALAPPDATA%\winui-repro-app\runner-base` |

`dotnet build` writes the CLI directly into `out\<Configuration>\<Platform>\`
and the Runner into its `runner-base\` subfolder. `pack.ps1` uses that same build
and copies the built app for distribution, taking repro files from the source
tree rather than editable build copies. The build needs no separate step to
assemble the app. The legacy cache fallback remains for old layouts, but a
normal build no longer uses it. `--doctor` tells you which one is in play.

Either way, everything the tool *writes* goes to the cache root, so the bundle
folder itself is read-only and can live on a share or a USB stick.

The base has three kinds of files:

- The app itself: `ReproStudio.Runner.exe`, its dll, its PRI, Roslyn, the .NET bits.
- The **managed** WASDK projections: `Microsoft.WinUI.dll`,
  `Microsoft.Windows.SDK.NET.dll`, `WinRT.Runtime.dll`, the `*.Projection.dll`s.
- The **native** WASDK runtime: `Microsoft.ui.xaml.dll`,
  `Microsoft.WindowsAppRuntime.dll`, `MRM.dll`, and friends.

> **Build the base with `dotnet build`, not `dotnet publish`.** Publish drops the
> app resource index (`ReproStudio.Runner.pri`), and without it the runner crashes
> at startup with *"Cannot locate resource ms-appx:///Microsoft.UI.Xaml/Themes/
> themeresources.xaml"*.
>
> A stray **`resources.pri`** in the runner output causes the exact same crash,
> because it shadows `ReproStudio.Runner.pri`. MSIX tooling has written one into
> `bin` in the past and nothing cleans it up, so the runner's build deletes it
> every time and `pack.ps1` refuses to ship one.

### Making a runner for version X

When you pick version X, the host provisions a folder for it (see
`RunnerProvisioner.EnsureRunnerAsync`):

```
  fresh copy of runner-base        version X's NATIVE dlls
  (app + managed, left as-is)  +   (overlaid on top)         =  versions\X\
```

1. Copy the base into `versions\X\`.
2. Download version X's NuGet packages (cached under `nupkgs\`, so this only
   happens once per version).
3. Overlay **only the native DLLs** from those packages over the copy.
4. Launch `versions\X\ReproStudio.Runner.exe`.

The managed projections stay at the base version. Only the native DLLs change.

A provisioned folder is reused as-is next time, *unless* the base runner has been
rebuilt since. The provisioner compares the base's `ReproStudio.Runner.dll`
timestamp against the copy, and re-provisions when they differ. Without that check
a fix to the Runner would never reach versions provisioned earlier - they'd serve
the old binary forever and the bug would look like it came back. Re-provisioning
reuses the cached downloads, so it re-copies but doesn't re-download.

### Two package layouts (the 1.8 split)

Where the native DLLs live *inside* the NuGet package changed at WASDK 1.8:

- **1.7 and earlier:** the metapackage has no component dependencies. The native
  runtime is zipped inside a framework **`.msix`** at
  `tools\MSIX\win10-x64\Microsoft.WindowsAppRuntime.<ver>.msix`. We unzip that
  msix to get the DLLs.
- **1.8 and later:** the metapackage pulls in component sub-packages
  (`.Foundation`, `.WinUI`, `.Runtime`, ...), each carrying loose native files
  under `runtimes-framework\win-x64\native`.

We tell them apart by asking "does the metapackage have `Microsoft.WindowsAppSDK.*`
dependencies?" - not by a version number - so the boundary is detected on its own
and won't break if Microsoft moves it again.

### Why swap only the native DLLs?

Because the Runner was **compiled** against the base version's managed assemblies,
and those assembly versions are baked into the exe. Overlay a different version's
managed `Microsoft.WinUI.dll` and the app dies at startup with "Could not load
... Version=X". So we leave the managed side alone and swap only native.

It works because WinRT keeps a stable ABI for released types: the base's managed
`Button` projection can drive version X's native `Button`, as long as the type
existed in both. For repro scenarios (layout, rendering, common controls) that
holds. The catch to know: the C# API surface your snippet sees is always the base
version's, even though the *rendering* is version X's.

### Why self-contained, not framework-dependent?

A **self-contained** build loads its WASDK native DLLs from right next to the
exe. That's exactly what lets us drop a different version's DLLs there and have
them win.

A **framework-dependent** build does the opposite: it uses the bootstrapper to
find an *installed* WASDK framework and loads from there, ignoring what's next to
the exe. We tried it - the runner just popped "This application could not be
started". So the base has to be self-contained.

Everything is also self-contained for **.NET**, for a different reason: so the
target machine doesn't need a .NET runtime installed.

### Seeing which version really loaded

The Runner window has a footer showing the version of the `Microsoft.ui.xaml.dll`
it *actually* loaded (see `MainWindow.GetLoadedWinUiVersion`). It reads the loaded
module, so it's ground truth, not a guess. If an overlay ever goes wrong, the
wrong number shows up right there.

### The cache

Everything lives under `%LOCALAPPDATA%\winui-repro-app\`, or wherever
`REPROSTUDIO_CACHE` points:

| Folder | What |
|---|---|
| `runner-base\` | Legacy base runner fallback. Normal builds and bundles carry their own. |
| `nupkgs\`      | Downloaded + extracted WASDK NuGet packages. |
| `versions\`    | One assembled runner per version you've used. |
| `local-winui\` | Extracted local WinUI `.nupkg` overrides. |

A folder in `versions\` is named after what went into it: `1.7.250401001`, or
`1.7.250401001__<winui-key>` with a `--winui` override, plus a `+payload` suffix
when a payload folder was applied. Payload runners are kept separate so a run
without one still gets untouched stock bits, and the suffix is fixed rather than
per-payload so iterating on a DLL replaces the folder instead of leaving a
350 MB copy behind every time.

`--clear-cache` wipes `versions\` and `local-winui\`, keeping `nupkgs\` so
re-provisioning is fast. Handy after you rebuild the base, or if a version folder
ever gets wedged.

## Packaged mode

The CLI can give the runner real package identity, without any MSIX build step
(see `PackagedRunnerLauncher`):

1. Copy `RunnerIdentity\Package.appxmanifest` into the version folder as
   `AppxManifest.xml`, along with its `Assets\`.
2. `PackageManager.RegisterPackageAsync(uri, null, DeploymentOptions.DevelopmentMode)`
   registers that folder **in place** - no staging copy of 150+ MB.
3. Activate by AUMID through `IApplicationActivationManager`.

There's no `makepri` step, and no `resources.pri`: the manifest uses literal
strings and unqualified asset names, so there is no `ms-resource:` indirection to
resolve. The manifest and `Assets\` are inert for a plain `CreateProcess`, so the
same folder still works for an unpackaged launch while registered.

This needs Developer Mode. Without it, registration fails with `0x80073CFF` and
the host falls back to an unpackaged launch, saying so.

## The IPC: it's just a file

No pipes, no sockets, no localhost server. The host sends a JSON request file;
capture runs also write a JSON result beside it.

1. The host makes a temp folder like
   `%TEMP%\winui-repro-app\runner-<8 hex>\request.json` (see `RunnerHost.cs`).
2. When the snippet changes, the host serializes it to that file. It writes to a
   `.tmp-<guid>` file first, then renames it over the real one. Rename is atomic
   on the same drive, so the Runner never sees a half-written file
   (`SnippetIo.WriteAtomic`).
3. The host launches the Runner exe pointed at that file:
   `ReproStudio.Runner.exe --request <path> --bounds <x y w h>`.
4. The Runner puts a `FileSystemWatcher` on the file. On a change it waits 150ms
   (debounce, so a burst of saves collapses into one), then re-reads and
   re-renders. If the read catches a mid-write or locked file, `TryRead` returns
   null and it just waits for the next event.

So "switch WASDK version" is really just "launch a different Runner exe watching
the same request file." No rebuild. Nice and dumb.

Each host write assigns a new `Snippet.RequestId`. When `--screenshot` is passed
to the Runner, it writes `request.result.json` atomically after rendering and
capture. `RunnerResult` records the request ID, render success/error, PNG path,
capture method, fallback reason, and capture error separately. The CLI ignores
results for older requests so a stale PNG or error cannot satisfy a newer run.

`--headless` and the absolute `--screenshot` path travel as launch arguments in
both packaged and unpackaged modes, not as repro headers. The CLI supplies a
default image path in its own working directory for headless runs. It reports
capture results while watching, and waits for the matching result in one-shot
mode. A headless one-shot always stops its Runner; the existing visible
no-watch mode leaves its window alive.

## Rendering a snippet

A `Snippet` carries some XAML and optional C#. The Runner turns it into live UI
in `RenderEngine.Render`:

**0. CLI launch hook.** When the file has a parameterless
`static void OnProcessLaunch()`, the console host tells the Runner to compile and
invoke it before `Application.Start`. Changing that method relaunches the process.
Because this happens before a window exists, hook compile or runtime failures are
written to `runner.log`; watch mode notices the exited process and prints that log.

**1. XAML -> tree.** The XAML string is parsed at runtime with `XamlReader.Load`.
There's no compiled XAML anywhere in the Runner (more on that below), so the
Runner's `App` implements `IXamlMetadataProvider` by hand. That's what lets
`XamlReader` resolve built-in controls like `Button` and `Grid`.

**2. C# -> in-memory assembly, via Roslyn.** If the snippet has C#, we compile it
with Roslyn (`Microsoft.CodeAnalysis.CSharp`) in `RoslynCompiler.Compile`:

- We prepend a fixed block of `using`s so snippets stay short. One of them is
  `using static ReproStudio_Runner.ReproApi;`, which is how a snippet can just
  call `Log("hi")` and have it show up in the Runner's log panel.
- The references are the Runner's *own already-loaded assemblies*
  (`AppDomain.CurrentDomain.GetAssemblies()`). So your snippet compiles against
  the very same WinUI assemblies the Runner is using - no separate SDK reference
  that could drift out of sync. (That's the *managed* projection, which is pinned
  to the base version - see "Running different WASDK versions" for why the native
  side can be a different version.)
- It emits to a `MemoryStream` and loads that into a **collectible**
  `AssemblyLoadContext`. Each edit unloads the previous one, so hammering the
  editor doesn't leak an assembly per keystroke.

When the leading comment header contains `// win32:`, `Win32Generator` merges
the comma-separated requests into an in-memory `NativeMethods.txt`. A fresh
`CSharpGeneratorDriver` runs CsWin32 before emit, adding bindings and recursively
required declarations to that same compilation. Nothing is generated into the
Runner assembly or shared between snippet assemblies. Without a request the
generator is not loaded or run. No IPC changes are needed: the existing C# field
preserves the complete source, including headers.

The Runner references the pinned package's Roslyn 5 generator as a copy-local
runtime library, not a build-time analyzer. Its non-framework dependencies
(including MessagePack's StringTools dependency) are bundled beside the exe;
the host's Roslyn and self-contained .NET provide the framework assemblies.
`CsWin32\Windows.Win32.winmd` comes from the package's matching SDK metadata
dependency and is passed via `build_property.CsWin32InputMetadataPaths`.
Optional API documentation data and WDK metadata are not used. This all travels
with the normal Runner copy/pack path: generation never probes the SDK/NuGet
cache or downloads anything. Recheck package layout and dependency closure when
updating CsWin32.

Snippet compilation enables unsafe code and selects x64, x86 or ARM64 from the
running process. CsWin32 uses runtime marshalling, without a second
LibraryImport/COM source-generator pass. Generator warnings are failures too:
CsWin32 reports unknown API names as warnings. The error text distinguishes
generation from ordinary C# errors. Only locations in the user's syntax tree
have the prepended-usings offset subtracted; generated files and the in-memory
request list retain their own filenames and coordinates.

**3. Wire it up.** We reflect over the compiled assembly for a `public static`
method named `Setup`, and call it. Parameters are filled by type, so any of
these work:

```csharp
static void Setup(FrameworkElement root)             { ... }
static void Setup(Window window)                     { ... }
static void Setup(FrameworkElement root, Window win) { ... }
```

`root` is the parsed XAML tree, so your C# can find elements and hook up events.

Any failure is tagged by phase (`xaml`, `csharp-compile`, or `runtime`), shown
in an InfoBar, and appended with full diagnostics to
`%TEMP%\winui-repro-app\runner.log`. Compile errors even get their line numbers
fixed up so they match your snippet, not the prepended usings.

If the Runner dies before it can show anything, it writes the exception to
`%TEMP%\winui-repro-app\runner.log`. The console host notices the process is gone
and prints whatever was appended since it launched.

## Cloaking and screenshots

Headless mode applies `DWMWA_CLOAK` to the main HWND before showing the window or
running `Setup`, verifies `DWM_CLOAKED_APP`, and shows without activation. It does
not minimize the window or hide it with `SW_HIDE`. Additional windows opened by
repro code are outside this guarantee.

`ScreenshotCapture` keeps a `WgcFrameSource` alive before changing the scene.
It discards the capture session's initial cached snapshot before the first
snippet render. After layout, it waits for a compositor commit and outstanding
DWM updates, then takes a subsequent frame. Starting a fresh WGC session after
rendering returned stale pixels during development, even with a new timestamp.
Do not replace this ordering with a timestamp-only check or insert artificial
visuals into the repro to force capture.

The capture path uses system D3D11/WinRT APIs and PNG encoding, with no Win2D or
new WASDK-native dependency. Unsupported HWND capture (including Windows 10
1809), backend errors, and bounded capture timeouts select an explicitly
reported RenderTargetBitmap fallback. Buffer dimensions and lengths are
validated, but black or transparent pixels are not automatically failures:
those can be the correct output of a repro.

New requests cancel and supersede older captures. Only a still-current capture
may atomically replace the PNG and publish its matching result. File-output
errors stay errors rather than silently changing the backend or destination.
The fallback captures the current XAML root, not native frames or disconnected
windows, and a successful error-panel screenshot does not erase the original
render failure.

## Why the Runner has no XAML files

The Runner is built entirely in code, no `App.xaml`, no `MainWindow.xaml`. That's
deliberate. Compiled XAML bakes in a WASDK version stamp, and we specifically
want one Runner build that can load *any* version's runtime DLLs dropped next to
the exe. So `App.cs` and `MainWindow.cs` hand-write the few things the XAML
compiler would normally generate (registering `XamlControlsResources`,
implementing `IXamlMetadataProvider`).

## Gotchas

- A running app **locks its own files**, so a rebuild can fail with a file-lock
  error. Close the app using that build output before rebuilding. Builds and
  packing do not automatically terminate running repros.
- Use `dotnet` (SDK 10.x), not VS2022's MSBuild. VS resolves an older SDK and
  chokes on net10 (NETSDK1045).
- Root build settings keep outputs under `out\<Configuration>\<Platform>\`
  and shield this repo from unrelated parent build settings. Don't delete them.
- The CLI and Runner have separate output directories but share one output root.
  `pack.ps1` asks MSBuild for `OutDir`, which can be an absolute path.
- The selected target architecture determines the runtime identifier, so an
  ARM64 build carries the ARM64 runtime even when built on an x64 machine.
- The provisioned runners and downloaded packages live under
  `%LOCALAPPDATA%\winui-repro-app\` (see the cache table above) and aren't in the
  repo.
- **A live re-render is not a fresh start.** Saving the file re-runs your XAML and
  `Setup` inside the window that is already open. Anything painted during the *first*
  show is not redone: `WM_ERASEBKGND`, `WM_NCCALCSIZE` and the DWM frame all happen
  once, inside `ShowWindow`, long before your edit landed. If that is what you are
  measuring, a hot push quietly reads a stale window and it looks like a real result.
  Relaunch the runner - `--no-watch` is the simple way - and confirm a new pid before
  believing the number.
- **`theme:` does not change the colour the window fills itself with.** It sets
  `RequestedTheme` on your snippet's root element, which sits inside the host's own
  layout. WinUI reads `Window.Content.ActualTheme` to pick the HWND erase colour, and
  `Window.Content` is that host layout, which stays Light. So the fill is white even
  with `theme: Dark`. That stays invisible until you read pixels near the window edge
  and find yourself measuring white against white. Paint a distinctive colour yourself
  when a pixel value has to mean something.
