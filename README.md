# ReproStudio

A CLI for reproducing WinUI / Windows App SDK bugs. Run a single `.cs` file,
pick a Windows App SDK version, and edit/save to refresh a live preview window.
Switch versions without rebuilding.

## Build

You need **Windows 10 1809 (build 17763) or newer**, the
[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), and internet
access for NuGet. From the repo root in PowerShell:

```powershell
dotnet build
```

This builds a runnable app into `artifacts\Debug\x64`, with the preview runner
in `runner-base`. No Visual Studio or `winapp` setup needed. Use `-c Release`
for Release, or `-p:Platform=ARM64` / `-p:Platform=x86` for another architecture.
To share a portable zip, run `.\pack.ps1`.

## Use

From the repo root, run `cd artifacts\Debug\x64`. If you have a zip
instead, unzip it and open a terminal in that folder. Then:

```powershell
.\ReproStudio.exe samples\hello.cs
```

A preview opens. Edit that `samples\hello.cs` in your editor and save to update
it. **Ctrl+C** stops it. Copy a sample to start your own repro: keep the
`class Repro` wrapper and `const string Xaml` literal; put optional C# in `Setup`.

```powershell
.\ReproStudio.exe samples\hello.cs --wasdk 1.6    # newest 1.6.x
.\ReproStudio.exe samples\hello.cs --payload D:\my-winui-build
.\ReproStudio.exe --list                         # available SDK versions
.\ReproStudio.exe --help                         # all options
.\ReproStudio.exe --doctor                       # diagnose problems
```

You can also change `// wasdk: 1.6` at the top of your repro and save to switch
versions. The preview footer shows the WinUI DLL version actually loaded.

The bundle needs **no SDK, .NET, or Windows App SDK installation** on the target
machine. First use downloads the chosen version from NuGet; later runs reuse
the cache. Developer Mode is only needed for `--packaged`.

**Only run repros you trust.** Their C# runs with your permissions, with no sandbox.

[Samples and file format](samples/README.md) |
[Guide: private builds and more](docs/guide.md) |
[How it works](docs/how-it-works.md)
