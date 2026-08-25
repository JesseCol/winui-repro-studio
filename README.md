# ReproStudio

A little tool for reproducing WinUI / Windows App SDK bugs. You give it some XAML
(and optional C#), and it renders live in a preview window using whatever WASDK
version you pick. Great for "does this repro on 1.6 but not 2.2?"

There are two front ends over the same engine:

| | `ReproStudio.exe` (console) | `ReproStudio.Host.exe` (WinUI) |
|---|---|---|
| What it is | Runs a `.cs` repro file, watches it, logs what it's doing | A GUI with editors, dropdowns, and a file picker |
| Needs WASDK | No | Yes |
| Good for | Shipping to a test machine, diagnosing a broken runner, agent/script driving | Authoring a repro on your dev box |

The console host is the one you copy to another machine. It has no Windows App SDK
dependency at all, so it still runs and can still tell you what went wrong when the
WASDK build under test refuses to start.

## Quick start

If you cloned the repo, build a bundle first. There is no exe in the tree:

```powershell
.\pack.ps1
cd artifacts\ReproStudio-x64
```

That takes a few minutes and leaves you with the same folder the zip contains.
If you got the zip instead, just unzip it and skip straight here.

Point it at a repro file:

```powershell
ReproStudio.exe samples\hello.cs
```

```
ReproStudio
  file      C:\ReproStudio-x64\samples\hello.cs
  cache     C:\Users\you\AppData\Local\winui-repro-app
  runner    C:\ReproStudio-x64\runner-base  (portable)
  . No version asked for, using the newest: 2.3.1

> provision
  wasdk     2.3.1
  packaged  no
  ready: ...\versions\2.3.1\ReproStudio.Runner.exe

> launch
  running (unpackaged), pid 30528

> watching
  . Save the file to push changes. Ctrl+C to stop.
```

A preview window opens. Leave the console running, edit the file in your editor,
and save:

```
  21:08:55  pushed  Hello from a file
```

Same window, same process - your XAML just re-rendered. That's the loop.

The first run for a given version downloads it and unpacks a private copy of the
runner, so budget a minute. Each version costs 156 to 260 MB on disk, depending
on the version, plus the NuGet packages it caches. After that the version is
cached and starts right away.

Stuck? `ReproStudio.exe --doctor` checks the machine and says what's wrong.

## Compare two WASDK versions

This is what the tool is for. While it's watching, change the `wasdk:` header and
save:

```csharp
// wasdk: 1.6
```

```
  . 1.6 resolved to 1.6.250602001
  21:09:01  relaunching: 1.6.250602001
  21:09:02  running 1.6.250602001
```

`wasdk` is a launch-time key, so rather than re-rendering in place it provisions
that version and starts a fresh runner on it. Change the header back and save
again to flip the other way. Your XAML and C# never move.

Two things worth knowing:

- The Runner's footer shows the exact `Microsoft.ui.xaml.dll` it loaded, so you can
  confirm you really are on the version you asked for.
- Partial versions are fine. `1.6` picks the newest 1.6.x.

You can also drive it from the command line, without editing the file:

```powershell
ReproStudio.exe repro.cs --wasdk 1.6      # override the header
ReproStudio.exe --list                    # what versions can I ask for?
```

## Take it to another machine

```powershell
.\pack.ps1
```

That produces `artifacts\ReproStudio-x64.zip`. Unzip it anywhere on the target
machine and run it:

```powershell
ReproStudio.exe samples\hello.cs
```

The target machine needs **nothing installed** - no SDK, no .NET runtime, no
Windows App SDK runtime. It does need internet, because WASDK versions are pulled
from NuGet on demand. The floor is **Windows 10 1809 (build 17763)**, which is the
minimum for .NET 10 and for every WASDK version this tool provisions.

If something doesn't work, ask it:

```powershell
ReproStudio.exe --doctor
```

That prints the OS build, whether it clears the 17763 floor, where the base runner
came from, what's in the cache, and whether Developer Mode is on.

## Run the console host

```powershell
ReproStudio.exe <file.cs> [options]
```

| Option | What |
|---|---|
| `--wasdk <version>` | WASDK version. Partial is fine (`1.6` picks the newest 1.6). Overrides the file header. |
| `--winui <ver\|path>` | Override just the WinUI component: a version, or a local `.nupkg`. |
| `--payload <dir>` | Copy every file in `<dir>` over the runner. The quick way to test a private build. `none` disables it. |
| `--packaged` / `--unpackaged` | Force package identity on or off. |
| `--prerelease` | Include prerelease versions when resolving and listing. |
| `--no-watch` | Launch and exit, leaving the runner running. |
| `--provision-only` | Prepare the runner, then exit without launching. Warms the cache. |
| `--clear-cache` | Delete provisioned runners first (downloads are kept). |
| `--list` | List available WASDK versions and exit. |
| `--doctor` | Print environment diagnostics and exit. |

Set `REPROSTUDIO_CACHE` to move downloads and provisioned runners off
`%LOCALAPPDATA%`.

While it's watching, saving the file pushes the change. Editing a *launch-time*
header key (`wasdk`, `winui`, `packaged`, `dpi`) re-provisions and relaunches
instead. If the runner dies on its own, the console says so and prints whatever
the runner appended to its crash log.

Ctrl+C stops the runner and unregisters the package.

## Test a private build: the payload folder

Provisioning a runner is really just "copy the base runner, then copy a WASDK
version's native files over it". The payload folder adds one more copy on the end,
so testing a private build of `Microsoft.ui.xaml.dll` is a matter of dropping the
file somewhere and running:

```powershell
ReproStudio.exe samples\hello.cs --payload D:\my-winui-build
```

Whatever is in that folder wins over the stock file of the same name. Files keep
their relative paths, so a subfolder like `Microsoft.UI.Xaml\` (the themes
directory) works the same as a loose DLL.

Three ways to point at one, in priority order:

| How | Example |
|---|---|
| `--payload <dir>` | `--payload D:\my-winui-build` |
| `// payload:` header | `// payload: ..\my-build` (relative to the repro file) |
| A `payload\` folder next to `ReproStudio.exe` | just run it |

That last one is why the packed bundle ships an empty `payload\` folder. Copy a
DLL in, run, and you are testing it. Nothing to configure.

The folder is watched, so rebuilding the DLL and copying it in relaunches the
repro on its own. Use `--payload none` to ignore the default folder for one run,
which is how you get a stock comparison without moving files around.

A few things worth knowing:

- Payload runners are provisioned into a separate `<version>+payload` folder, so
  runs without a payload keep using untouched stock bits.
- Changing the payload rebuilds that folder. An overlaid file can't be
  un-overlaid in place, because nothing recorded what it used to be.
- `.txt` and `.md` files are ignored, so the folder can carry a README without
  that counting as content.
- Nothing is validated. Drop in a binary that doesn't load and the runner will
  fail to start and say so.

**Always take a stock reading before you trust a payload reading.** If the
private build changes nothing, that's worth knowing; if it changes everything,
you want to be sure the harness itself was working.

### `--payload` or `--winui`?

Both put private bits in front of the runner. They solve different problems.

| | `--payload <dir>` | `--winui <ver\|path.nupkg>` |
|---|---|---|
| Input | Loose files | A version, or a built `.nupkg` |
| Best for | One rebuilt DLL, iterating fast | A full WinUI build you want to keep and share |
| Setup | Copy a file in | Build a nupkg first |
| Granularity | Any file, any subfolder | The WinUI component |
| Knows what stack it needs | No | Yes, if the nupkg declares dependencies |

For a tight edit-build-test loop, use `--payload`. To hand someone a bundle that
runs a specific build, use `--winui`.

## Test a WinUI repo build

This is the path to use for a real WinUI build. In the WinUI repo:

```
build.cmd /version 3.9.9-mybuild
```

That produces a `Microsoft.WindowsAppSDK.WinUI.3.9.9-mybuild.nupkg` that declares
the Base, Foundation and InteractiveExperiences versions it was compiled against.
Point ReproStudio at it and it works out the rest:

```powershell
ReproStudio.exe bug.cs --winui D:\winui\...\Microsoft.WindowsAppSDK.WinUI.3.9.9-mybuild.nupkg
```

```
> provision
  winui     Microsoft.WindowsAppSDK.WinUI.3.9.9-mybuild.nupkg
  . No Windows App SDK version asked for, so this package's own dependencies pick the stack.
  . Resolving components from the WinUI package...
  . Fetching Microsoft.WindowsAppSDK.Base 2.0.4...
  . Fetching Microsoft.WindowsAppSDK.Foundation 2.3.5...
  . Fetching Microsoft.WindowsAppSDK.InteractiveExperiences 2.1.3...
  . Applying local WinUI package ...
```

No `--wasdk` needed. The package is self-describing, so the versions it gets are
the versions it was built against.

### Why this matters

A WinUI build compiled against Foundation 3.0.0 will happily load on a WASDK
2.3.1 runner, which ships Foundation 2.3.5. Nothing complains at provision time.
The mismatch surfaces much later as an unexplained `E_NOINTERFACE` or an
`InvalidCastException`, and you lose a day to it. Letting the package pick its own
stack removes the guess.

### Mixing both flags

Pass `--wasdk` too and you get a middle ground:

```powershell
ReproStudio.exe bug.cs --wasdk 2.2.0 --winui 2.3.0
```

The WASDK version supplies everything (AI, ML, Widgets, DWrite and the rest), and
the WinUI package raises anything below what it needs:

```
  . WinUI 2.3.0 needs Microsoft.WindowsAppSDK.Foundation 2.3.5, but this Windows
    App SDK provides 2.1.0. Raising it.
  . WinUI 2.3.0 needs Microsoft.WindowsAppSDK.InteractiveExperiences 2.1.3, but
    this Windows App SDK provides 2.0.15. Raising it.
```

Versions are floors, not pins. Stock combinations already "disagree" numerically
(WASDK 2.3.1 ships Foundation 2.3.5 while its WinUI asks for `>= 2.3.1`), so
anything higher is fine and only lower gets raised.

### Packages with no dependency metadata

`tools\pack-local-winui.ps1` produces a shape-only nupkg. It has the right folder
layout but no nuspec, so it cannot decide a stack:

```
x Could not prepare a runner: ...nupkg declares no Windows App SDK dependencies,
  so it cannot decide the stack on its own. Pass a Windows App SDK version as
  well, or build the package with the WinUI repo's 'build.cmd /version <version>'.
```

Add `--wasdk <version>` and it works as a plain overlay, exactly like `--payload`.

### NuGet sources

ReproStudio uses your real NuGet configuration, so an internal feed just works.
Drop a `nuget.config` next to the repro file to add one for a single repro:

```xml
<configuration>
  <packageSources>
    <add key="winui-pr" value="https://pkgs.dev.azure.com/.../nuget/v3/index.json" />
  </packageSources>
</configuration>
```

`--doctor` lists the sources it found. If a version cannot be fetched, the error
names the package, the version, and every source it tried.

## Run the WinUI host

From this folder:

```powershell
dotnet run --project .\src\ReproStudio.Host
```

First run pulls packages and builds, so give it a minute. It also needs a base
runner - see [The base runner](#the-base-runner-built-once) below, or just run
`.\pack.ps1` once.

## Using the WinUI host

Two windows show up: the **Host** (your editor) and the **Runner** (the live
preview, docked to its right).

In the Host:
- Pick a **WASDK** version. Optionally pick a **WinUI** version, or **Browse
  .nupkg** to try a local WinUI build.
- Type in the **XAML** or **C#** tab. They're tabs, not side by side, so you get
  the full width for whichever you're editing. Edits stream to the preview live
  (a short debounce).
- **Relaunch** restarts the preview. **Clear cache** wipes the provisioned
  versions and rebuilds the current one.
- **Keep on top** pins the Runner window above everything else. It's a live
  toggle, so no relaunch, and it sticks when you switch versions.
- **Packaged** launches the preview runner *with package identity* (registered as
  a loose-layout package and activated by AUMID) instead of unpackaged. It's **on
  by default** - so the runner has identity out of the box, handy for reproing bugs
  that only show up when the app is packaged. Uncheck it to run unpackaged. Toggling
  relaunches the runner, and the status line tells you which mode you're in
  (`packaged` vs `unpackaged`).

The Runner window shows your rendered snippet, a log panel, and a footer with the
exact `Microsoft.ui.xaml.dll` version it loaded - so you always know what's really
running.

> **Careful what you paste.** The Runner compiles and runs your C# for real, with
> **no sandbox**. Only paste code you trust. (The app warns you about this too.)

## The repro file

A repro is one `.cs` file: a tiny `// key: value` header up top, the XAML in a
`Xaml` raw-string, and your `Setup` method. It stays valid C#, so your editor's C#
tooling still works. Both hosts read the same format.

The console host takes it as an argument. The WinUI host takes it through **Open
file...**, after which the in-app editors turn into a read-only mirror.

Want something to open right now? There are ready-made repros in
[`samples/`](samples/) - try `samples/hello.cs`.

Two other folders hold repro files with a job to do:

| Folder | What's in it |
|---|---|
| [`probes/`](probes/) | One-file checks that settle a single question about platform behaviour, each with its measured answer and the WASDK version it was taken against |
| [`investigations/`](investigations/) | Bigger measurement harnesses written to chase a specific bug, each with a write-up of what it found |

```csharp
// repro:      My cool bug
// wasdk:      1.7            <- partial is fine; newest 1.7.x wins (see below)
// winui:      default        <- or a version, or a path to a local .nupkg
// payload:    none           <- or a folder of files to copy over the runner
// packaged:   no             <- give the runner package identity
// theme:      Dark           <- Default | Light | Dark
// flow:       LeftToRight    <- LeftToRight | RightToLeft
// dpi:        100            <- 100 to 400
// background: #202020        <- stage colour behind your XAML
// topmost:    no             <- keep the runner above other windows

class Repro
{
    const string Xaml = """
        <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    Padding="24" Spacing="12">
            <TextBlock Text="Hello from a file!" FontSize="28" />
            <Button x:Name="HelloButton" Content="Click me" />
        </StackPanel>
        """;

    static void Setup(FrameworkElement root, Window window)
    {
        Log("Loaded from file.");
        if (root.FindName("HelloButton") is Button b)
        {
            b.Click += (s, e) => b.Content = "Clicked!";
        }
    }
}
```

The `class Repro { }` wrapper is required. The snippet is compiled as a library,
so top-level statements fail with `CS8805`, and a compile error means no window
and no output - it just looks like nothing happened.

Every header key is optional, and order doesn't matter. Two kinds of keys:

- **Live** (`theme`, `flow`, `background`, `topmost`) and the XAML/C# itself: a
  save just re-renders in place. Fast.
- **Launch-time** (`wasdk`, `winui`, `payload`, `packaged`, `dpi`): these pick
  which Runner exe runs and how, so a change re-provisions and relaunches.

### Run code before XAML starts (CLI)

The console host recognizes one optional launch-time hook:

```csharp
static void OnProcessLaunch()
{
    EnableXamlOptionalChange(63530879);
}
```

The CLI compiles and invokes this parameterless `static void` method before
`Application.Start`, so it can configure process-wide state that must be set before
XAML initializes. `EnableXamlOptionalChange` takes the numeric `XamlChangeId`, which
also works when the runner's pinned managed projection predates that enum member.

Changing `OnProcessLaunch` changes the CLI's launch plan and restarts the Runner.
Edits elsewhere, including XAML and `Setup`, still update the existing process.
The hook is compiled separately from `Setup`, so use it for process-wide/native
configuration rather than managed static state that `Setup` expects to read. Keep
the exact block-bodied `static void OnProcessLaunch()` shape and keep its launch
configuration self-contained: only this method's text is fingerprinted, so changing
a helper or constant outside it does not trigger a relaunch.

### You don't have to type the whole WASDK version

`wasdk: 1.7` is enough. It matches your text against the real version list by
dotted segments and picks the newest one that fits, so `1.7` finds
`1.7.250401001`. An exact version still works too - and if you write a full
version, no version list is fetched at all, so a fully pinned repro runs offline.

### Packaged mode needs Developer Mode

`packaged: yes` (and `--packaged`) registers the provisioned runner folder as a
loose-layout package. Windows only allows that when Developer Mode is on:
**Settings > Privacy & security > For developers**. Without it you get
`0x80073CFF`, and the console falls back to an unpackaged launch with a warning.

### Your own usings, and P/Invoke

The runner hands your whole file to Roslyn after prepending a fixed block of
usings, so you can add your own directives and they land in the right place.
Repeating one that's already injected is a warning, not an error, so
`using System;` at the top of your repro is fine.

Injected for free:

```
System                                 Microsoft.UI.Xaml.Media
Microsoft.UI.Xaml                      Microsoft.UI.Xaml.Shapes
Microsoft.UI.Xaml.Controls             Microsoft.UI.Xaml.Input
Microsoft.UI.Xaml.Controls.Primitives  Microsoft.UI.Windowing
Windows.Graphics                       static ReproStudio_Runner.ReproApi
```

That means `[DllImport]` works, which is how you repro anything that needs Win32.
Take the `Window` that `Setup` hands you and turn it into an HWND:

```csharp
using System.Runtime.InteropServices;
using WinRT.Interop;

class Repro
{
    const string Xaml = """<TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Text="hi" />""";

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    static void Setup(FrameworkElement root, Window window)
    {
        IntPtr hwnd = WindowNative.GetWindowHandle(window);
        Log($"ex-style 0x{GetWindowLong(hwnd, -20):X8}");
    }
}
```

A fuller example, calling `DwmExtendFrameIntoClientArea`, is in
[`samples/pinvoke.cs`](samples/pinvoke.cs).

One limit worth knowing: the runner paints its own opaque stage over the client
area, so Win32 calls that rely on client-area transparency (DWM glass, layered
windows) will return `S_OK` and change nothing you can see.

## Build it (without running)

Everything is x64. `pack.ps1` is the normal way to build, because it also stages
a runnable bundle. To just compile, build the projects directly:

```powershell
dotnet build .\src\ReproStudio.Cli\ReproStudio.Cli.csproj -c Debug -p:Platform=x64
dotnet build .\src\ReproStudio.Runner\ReproStudio.Runner.csproj -c Debug -p:Platform=x64
```

Build the projects, not the `.slnx`. `-p:Platform` doesn't reach the projects
through the solution file, so a solution build writes `bin\Debug\` while
`pack.ps1` writes `bin\x64\Debug\`. Mixing the two silently leaves you running
stale binaries.

Use the `dotnet` CLI (SDK 10.x), not VS2022's MSBuild, which resolves an older
SDK and fails on net10 with NETSDK1045.

A freshly built exe under `bin\` still needs a runner to drive. It has no
`runner-base` next to it, so it falls back to the one in
`%LOCALAPPDATA%\winui-repro-app\`, which nothing refreshes. **To test a Runner
change, run `pack.ps1` and run from the bundle.** The console prints which runner
it picked, so you can check:

```
runner    ...\artifacts\ReproStudio-x64\runner-base  (portable)   <- fresh
runner    ...\AppData\Local\winui-repro-app\runner-base  (dev)    <- may be old
```

---

# How it works

Skip this unless you want the guts. Short version: it's two processes talking
through a JSON file, and your C# gets compiled at runtime with Roslyn.

## The processes

```
 +---------------------+
 |  ReproStudio.exe    |  console host, no WASDK
 |  (or .Host.exe)     |  WinUI host, has WASDK
 +---------------------+
            |  request.json (JSON on disk)
            v
 +----------------------+
 |  ReproStudio.Runner  |  the preview window
 |  self-contained WASDK|  one per version
 +----------------------+

  both hosts sit on ReproStudio.Shared
  (Snippet, RunnerHost, RunnerProvisioner, PackagedRunnerLauncher, AppLayout)
```

- **Cli** is the console host. No WASDK reference at all.
- **Host** is the WinUI GUI. Unpackaged and self-contained, same as the runner.
- **Runner** is a separate, throwaway process that does the actual rendering.
  There's one Runner per WASDK version.
- **Shared** is everything both hosts need: the `Snippet` contract, the launcher,
  the provisioner, and the file-layout rules.

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
| Portable (xcopy bundle) | `runner-base\` next to the host exe |
| Dev box | `%LOCALAPPDATA%\winui-repro-app\runner-base` |

`pack.ps1` produces the first. For the second, build
`src\ReproStudio.Runner` and copy its output there. `--doctor` tells you which one
is in play.

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
| `runner-base\` | The self-contained base runner (dev boxes only; a bundle carries its own). |
| `nupkgs\`      | Downloaded + extracted WASDK NuGet packages. |
| `versions\`    | One assembled runner per version you've used. |
| `local-winui\` | Extracted local WinUI `.nupkg` overrides. |

A folder in `versions\` is named after what went into it: `1.7.250401001`, or
`1.7.250401001__<winui-key>` with a `--winui` override, plus a `+payload` suffix
when a payload folder was applied. Payload runners are kept separate so a run
without one still gets untouched stock bits, and the suffix is fixed rather than
per-payload so iterating on a DLL replaces the folder instead of leaving a
350 MB copy behind every time.

`--clear-cache` (console) and the **Clear cache** button (GUI) wipe `versions\`
and `local-winui\`, keeping `nupkgs\` so re-provisioning is fast. Handy after you
rebuild the base, or if a version folder ever gets wedged.

## Packaged mode

Both hosts can give the runner real package identity, without any MSIX build step
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

No pipes, no sockets, no localhost server. The whole channel is one JSON file.

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

## Why the Runner has no XAML files

The Runner is built entirely in code, no `App.xaml`, no `MainWindow.xaml`. That's
deliberate. Compiled XAML bakes in a WASDK version stamp, and we specifically
want one Runner build that can load *any* version's runtime DLLs dropped next to
the exe. So `App.cs` and `MainWindow.cs` hand-write the few things the XAML
compiler would normally generate (registering `XamlControlsResources`,
implementing `IXamlMetadataProvider`).

## Gotchas

- A running Runner **locks its own .exe**, so a rebuild can fail with a file-lock
  error. Kill leftover `ReproStudio.Runner` processes first. `pack.ps1` does this
  for you.
- Use `dotnet` (SDK 10.x), not VS2022's MSBuild. VS resolves an older SDK and
  chokes on net10 (NETSDK1045).
- `Directory.Build.props` at the repo root is intentionally almost empty. It stops
  MSBuild's upward search from finding a parent repo's props file that would
  redirect `OutDir`. Don't delete it.
- Self-contained builds put the output under a RID subfolder
  (`bin\x64\Debug\<tfm>\win-x64\`). `pack.ps1` asks MSBuild for `OutDir` rather
  than guessing.
- Both hosts and the runner derive their `RuntimeIdentifier` from the *build*
  machine's architecture. `pack.ps1` passes `-p:RuntimeIdentifier` explicitly so
  cross-architecture packing is correct.
- The provisioned runners and downloaded packages live under
  `%LOCALAPPDATA%\winui-repro-app\` (see the cache table above) and aren't in the
  repo.
