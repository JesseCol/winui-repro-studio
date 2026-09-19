# How it works

Back to the [README](../README.md).

Skip this unless you want the guts. Short version: it's two processes talking
through JSON files, and your C# gets compiled at runtime with Roslyn.

## Where to change what

There are three production projects, not three ways to launch the tool. The root
`ReproStudio.csproj` is the real console host; its C# stays under
`src\ReproStudio.Cli`. `dotnet run` builds its dependencies and launches it.

```text
ReproStudio.csproj              host entry point
ReproStudio.slnx                all three production projects
src\ReproStudio.Cli\            commands, console output, file watching
src\ReproStudio.Shared\         contracts, provisioning, process launch
src\ReproStudio.Runner\         snippet compilation, UI, capture
samples\                       small teaching repros
tools\smoke.ps1                 end-to-end workflow checks
```

| Change | Start here |
|---|---|
| CLI flags or usage | `src\ReproStudio.Cli\CliOptions.cs` |
| First run, save/relaunch behavior, version shortcut | `src\ReproStudio.Cli\ReproSession.cs` |
| File format or request/result contract | `src\ReproStudio.Shared\SnippetFileParser.cs`, `Snippet.cs`, `RunnerResult.cs` |
| Version resolution, downloads, runtime overlays | `src\ReproStudio.Shared\VersionResolver.cs`, `RunnerProvisioner.cs`, `NuGetFeed.cs` |
| Starting/stopping a Runner, package identity | `src\ReproStudio.Shared\RunnerHost.cs`, `PackagedRunnerLauncher.cs` |
| C# imports, diagnostics, generated Win32 bindings | `src\ReproStudio.Runner\Services\RoslynCompiler.cs`, `Win32Generator.cs` |
| Preview, errors, log panel | `src\ReproStudio.Runner\MainWindow.cs`, `Services\RenderEngine.cs` |
| Pin, Code and runtime dialog | `MainWindow.Toolbar.cs`, `RuntimeDialog.cs` in Runner; `ReproSession.Controls.cs` in Cli |
| Runtime source edits and control IPC | `RuntimeHeaderEdit.cs`, `RunnerControl.cs` in Shared |
| SDK/API payload, compatibility and pair cache | `SdkPackageGraph.cs`, `SdkPayload.cs`, `ManagedCompatibility.cs`, `RunnerPairManifest.cs` in Shared |
| Cloaking and screenshot capture | `src\ReproStudio.Runner\Services\ScreenshotCapture.cs`, `WgcFrameSource.cs`, `WindowCaptureInterop.cs` |
| Build layout or portable bundles | `Directory.Build.props`, `ReproStudio.csproj`, `pack.ps1` |

The host's Runner reference is **build-only**: a fresh `dotnet run` refreshes
`runner-base`, but it does not give the host a WinUI assembly or WASDK runtime
dependency. Keep that boundary. The host must still diagnose a broken Runner.

`dotnet run --project tools\RunnerContractTests` runs the lightweight contract
checks (no test framework or WinUI dependency): encoding-preserving header edits,
editor conflicts, preference writes, literal Code arguments, correlated IPC,
SDK dependency/asset selection and pair-cache integrity.
`tools\smoke.ps1 -SkipPackaging` covers root build/run and passive capture.

## The processes

```
 +---------------------+
 |  ReproStudio.exe    |  console host, no WASDK
 +---------------------+
            |  request.json (JSON on disk)
            v
 +----------------------+
 |  ReproStudio.Runner  |  the preview window
 |  self-contained WASDK|  one SDK/runtime pair
 +----------------------+

  both processes reference ReproStudio.Shared
  (Snippet, RunnerHost, RunnerProvisioner, PackagedRunnerLauncher, AppLayout)
```

- **Host** (`ReproStudio.csproj`, source in `ReproStudio.Cli`) is the console
  process. No WASDK assembly/runtime dependency.
- **Runner** is a separate, throwaway process that does the actual rendering.
  Each Runner loads one prepared SDK/API and native runtime pair.
- **Shared** holds the `Snippet` contract and IPC used by both processes, plus
  the CLI's launcher, provisioner, and file-layout rules.

Why separate processes? So we can render the same snippet against *different*
SDK/API and native runtime combinations. Each prepared folder has one coherent
managed API set and one native runtime. The host starts a fresh process when
either choice changes.

Notably, `Shared` uses `Windows.Management.Deployment.PackageManager` to register
the packaged runner. That comes from the Windows SDK projection (free with a
`net10.0-windows` TFM) and needs **no** Windows App SDK, which is what lets the
console host stay WASDK-free.

## Running different WASDK versions

This is the heart of the tool, and the trickiest part. The goal is to compile a
snippet against one API surface and run it on a selected native implementation,
without building a new Runner on the target machine.

### The base runner (built once)

We build the Runner **once**, self-contained, against its baseline WASDK. That
build - the "base" - provides the application and support runtime, and is found
in one of two places:

| Deployment | Where the base comes from |
|---|---|
| Normal build or portable bundle | `runner-base\` next to the host exe |
| Legacy fallback | `%LOCALAPPDATA%\winui-repro-app\runner-base` |

`dotnet build` and `dotnet run` write the CLI directly into `out\<Configuration>\<Platform>\`
and the Runner into its `runner-base\` subfolder. `pack.ps1` uses that same build
and copies the built app for distribution, taking repro files from the source
tree rather than editable build copies. The build needs no separate step to
assemble the app. The legacy cache fallback remains for old layouts, but a
normal build no longer uses it. `--doctor` tells you which one is in play.

Provisioned runtimes and downloads go to the cache root. Requests and logs go to
the temporary directory; requested screenshots go to their explicit path or the
working directory. The bundle itself can stay read-only on a share or USB stick,
as long as the chosen screenshot location is writable.

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

### Making a runner for an SDK/runtime pair

The host assembles a folder before launching it:

```text
base application + selected managed SDK/API + selected native runtime
                                      + optional private payload
```

The SDK/API selection normally matches the runtime source. An explicit `sdk`
version chooses a Windows App SDK managed surface independently. `sdk: base`
is the explicit compatibility mode that retains the bundled managed API surface.

Provisioning resolves the selected package graphs, chooses compatible managed
assets for the target framework/RID, and prepares dependency metadata as well as
DLLs. A WASDK-prefix-only dependency walk is not sufficient: external projection
and support packages can be part of the managed closure. Reference-only
assemblies are not executable projections.

Compatible host framework/support assemblies can remain newer than a projection's
minimum requirement. They must not be blindly downgraded: the Runner binary has
its own requirements too. Conversely, old SDK-owned assemblies must not silently
remain when the selected SDK has replaced or removed them.

Cache identity includes both choices, their resolved assets, the base/support
identity and architecture. The old native-only folder or a matching version label
is not evidence that a managed SDK payload is correct.

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

### Why a coherent managed payload matters

The original implementation swapped only native DLLs and pinned managed APIs to
the base. That reproduced rendering changes, but selecting a newer runtime could
not make a new C# property appear.

Changing Roslyn's references alone is not a fix. The Runner's `Window` and the
snippet's `Window` must be the same managed type. The selected projection must
load consistently into the default assembly context before WinUI starts; snippet
assemblies share it rather than loading a conflicting projection in their
collectible contexts.

Assembly identity and required APIs both matter. Equal assembly versions do not
prove API compatibility, and higher compatible support dependencies should not be
downgraded just to imitate a package's minimum dependency versions.

With a newer SDK and older native runtime, common controls may work while a new
API fails when its native interface is queried. That is an intentional
compatibility experiment, distinct from a compiler rejecting a member absent
from the selected SDK.

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

The SDK/API identity and native runtime selection are reported separately.
The native WinUI footer still reads the `Microsoft.ui.xaml.dll` module actually
loaded (see `MainWindow.GetLoadedWinUiVersion`). Package versions, managed assembly
versions and native file versions are different facts; none should be used as a
substitute for the others.

### The cache

Everything lives under `%LOCALAPPDATA%\winui-repro-app\`, or wherever
`REPROSTUDIO_CACHE` points:

| Folder | What |
|---|---|
| `runner-base\` | Legacy base runner fallback. Normal builds and bundles carry their own. |
| `nupkgs\`      | Downloaded + extracted WASDK NuGet packages. |
| `versions\`    | Prepared SDK/API and runtime combinations. |
| `local-winui\` | Extracted local WinUI `.nupkg` overrides. |
| `runner-preferences.json` | Version-independent Pin preference, not deleted by `--clear-cache`. |

A provisioned folder's identity describes its SDK/runtime inputs and payload,
not just one WASDK version string. Private-payload and stock configurations stay
separate. Existing native-only cache folders are not upgraded by overwriting them
with an unrelated API set.

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

The fixed package identity is per Windows user. The host holds an exclusive
`%LOCALAPPDATA%\winui-repro-app\packaged-runner.lock` file handle across launches,
independent of `REPROSTUDIO_CACHE`. A competing host cannot replace the active
registration. Switching pairs first removes the owned registration, then verifies
the new `InstalledLocation`: registering the same identity/version at a new path
can otherwise leave activation pointing at the old folder.

Unregistration removes only the manifest and identity logos it staged. It must
not delete `Assets\`, which also holds immutable files from the base Runner.
Failed removal retains registration state and staged files so it can be retried.

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

IPC readers, including test scripts, must share `ReadWrite | Delete`. A reader
without delete sharing can block the writer's atomic replacement on Windows.

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

The request also carries the original absolute source path, a host-resolved
preference path, effective runtime selection, and (watch mode only) the host's
session GUID/PID/start time. These are host metadata, not source headers.
Legacy `topmost` JSON/header values are ignored.

The toolbar's small reverse channel uses `request.control.json` and
`request.control-result.json`. Only `list` and `apply` operations exist. Every
command/response is correlated by command and session IDs; apply also validates
the render request and Runner PID. The client serializes its operations, rejects
stale responses, detects host exit, and imposes 45-second list / 5-minute apply
deadlines. Cancellation withdraws the command, which cancels host provisioning.
The host polls alongside its existing watch loop; listings use the existing
provisioner's WASDK/WinUI methods and repro-local NuGet configuration.

Runtime apply shares the reload/health semaphore. The host preflights the original
source against the text attached to the current render request, so a save still
waiting for debounce cannot be overwritten by a stale dialog. It provisions
while the old Runner is usable, then compares the original
bytes under an exclusive handle before writing just the runtime headers. Concurrent
editor changes produce a retry error, never a blind overwrite. Only after commit
does it retire runtime CLI overrides and replace the Runner. The header save's
watcher event is deduplicated. New runtime requests are not published to the old
Runner while preparation is pending. A launch failure after source commit is
reported by the host; save the source to retry.

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
- References come from the Runner's resolvable managed assemblies: the host's
  trusted-platform list and app-local DLLs, with already-loaded assemblies taking
  precedence by name. Lazy-loaded APIs are available without creating a second,
  mismatched type identity. The managed projection is the SDK/API payload loaded
  for this Runner, not necessarily its build-time SDK or selected native runtime.
- It emits to a `MemoryStream` and loads that into a **collectible**
  `AssemblyLoadContext`. Each edit requests unloading of the previous context;
  collection can finish once nothing still holds its types, objects, or delegates.

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
