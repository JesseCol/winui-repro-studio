# ReproStudio guide

Everything past the quick start: taking it to another machine, testing your own
builds, running the WinUI host, and the rest of the repro file format.

Back to the [README](../README.md).

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
runner - see [The base runner](how-it-works.md#the-base-runner-built-once), or just run
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

## More about the repro file

Two more folders hold repro files with a job to do:

| Folder | What's in it |
|---|---|
| [`probes/`](../probes/) | One-file checks that settle a single question about platform behaviour, each with its measured answer and the WASDK version it was taken against |
| [`investigations/`](../investigations/) | Bigger measurement harnesses written to chase a specific bug, each with a write-up of what it found |

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
[`samples/pinvoke.cs`](../samples/pinvoke.cs).

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
