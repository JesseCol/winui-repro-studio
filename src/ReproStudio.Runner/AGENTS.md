# Agent notes - ReproStudio.Runner (the preview process)

Read `docs\how-it-works.md` first, especially "Running different WASDK versions".
This file only covers what is surprising about *this* project.

## What it is

The process that actually renders a repro. It is built **once**, self-contained,
against the newest WASDK. That build gets copied into a folder per WASDK version,
and each copy has that version's **native** DLLs overlaid on top. So one build has
to be able to run on top of any version's native runtime.

That constraint drives almost everything below.

## Hard rules

**No XAML files. Ever.** No `App.xaml`, no `MainWindow.xaml`, no `.xaml` of any
kind in this project. Compiled XAML bakes in a WASDK version stamp, which would
break the version-overlay trick. `App.cs` hand-writes what the XAML compiler
normally generates: registering `XamlControlsResources` and implementing
`IXamlMetadataProvider` by hand. If you need new UI, write it in C#.

**Build with `dotnet build`, never `dotnet publish`.** Publish drops
`ReproStudio.Runner.pri`, and without it the runner dies at startup with
*"Cannot locate resource from 'ms-appx:///Microsoft.UI.Xaml/Themes/
themeresources.xaml'"*.

**Never let a `resources.pri` into the output.** Same crash, different cause. When
a file literally named `resources.pri` exists next to the exe, WinUI uses it
*instead of* `ReproStudio.Runner.pri`, and the framework themes are not in it.
MSIX tooling has written one into build output in the past. Incremental builds
do not remove unrelated files, so it can survive rebuilds and then get copied
into every provisioned version folder.

There is a `RemoveStaleResourcesPri` target at the bottom of the `.csproj` that
deletes it after every build, and `pack.ps1` throws if it sees one. **Both are
load-bearing.** If you are chasing a themeresources crash, check for this file
first - it is almost always the answer.

**Stay self-contained.** A framework-dependent build uses the bootstrapper to find
an *installed* WASDK and loads from there, ignoring the DLLs next to the exe -
which defeats the entire design. It fails with "This application could not be
started".

**Do not use APIs newer than Windows 10 1809** without a runtime feature check.
That is the floor for this project.

**Be careful adding managed WASDK API usage.** The managed projections are pinned
at the base version even when the native DLLs are older, so a call that compiles
fine can hit a native entry point that does not exist on an older runtime.

## Launch modes

The same runner supports both unpackaged and packaged launches. From the repo
root after building:

```powershell
.\out\Debug\x64\ReproStudio.exe samples\hello.cs --unpackaged
.\out\Debug\x64\ReproStudio.exe samples\hello.cs --packaged
```

`--packaged` (or `// packaged: yes`) uses `PackagedRunnerLauncher` in Shared to
copy `RunnerIdentity\Package.appxmanifest` and its assets into the provisioned
runner folder, register that whole folder, and activate it with package identity.
It needs Developer Mode. If registration fails, the CLI warns and falls back to
unpackaged mode, so do not count a fallback as a successful packaged-mode run.

Do not add an MSIX installer, signing certificates, or a `winapp` dependency to
this workflow. Keep the identity manifest and its assets. The separate
`app.manifest` supplies Win32 compatibility and DPI settings and must also stay.

## Debugging a runner that will not start

`--headless --no-watch` on the CLI cloaks the Runner, captures `ReproStudio.png`
in the working directory, and stops it. The CLI reports whether the image came
from WGC or the XAML-only RenderTargetBitmap fallback. Only the main HWND is
cloaked; arbitrary windows opened by repro code are not.

Keep WGC active before changing the scene and discard its initial cached frame.
Starting a capture session after rendering can return old pixels with a new
timestamp. Do not add capture-only visuals to make the repro render differently.

It has no console. Unhandled exceptions go to
`%TEMP%\winui-repro-app\runner.log`. The console host prints whatever was appended
since it launched, so `ReproStudio.exe <repro.cs>` is usually the fastest way to
see the failure.

`ReproStudio.exe --doctor` checks the base runner: whether it exists, whether it
is self-contained, and whether a stale `resources.pri` is present.

## Build

```powershell
dotnet build
```

Use the `dotnet` CLI (SDK 10.x); VS2022's MSBuild fails with NETSDK1045 on net10.

A running Runner **locks its own exe**, so close it if it is running directly
from the build output before rebuilding.

The Runner builds directly into
`out\<Configuration>\<Platform>\runner-base\`. The CLI is one level above.
After changing anything here, rebuild and run
`.\out\Debug\x64\ReproStudio.exe samples\hello.cs` from the repo root.
No packing or manual copy is needed. `--doctor` shows which base is in use.
