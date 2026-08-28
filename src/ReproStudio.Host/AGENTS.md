# Agent notes - ReproStudio.Host (the WinUI GUI front end)

Read the root `README.md` and `docs\guide.md` first. This file only covers what
is surprising about *this* project.

## What it is

The optional GUI front end. `src\ReproStudio.Cli` is the other one, and it is the
one that ships. Both drive the same code in `ReproStudio.Shared`, so **put shared
behaviour in Shared, not here.** If you add a feature to the GUI that the console
host would also want, it belongs in Shared.

## Things that will trip you up

**It is not packaged, and there is no `winapp`.** Earlier versions of this project
were MSIX-packaged and launched with `winapp run`. That is gone: no
`Package.appxmanifest`, no `Microsoft.Windows.SDK.BuildTools.WinApp` reference, no
AUMID. Just build and run the exe. Do not re-add MSIX tooling here.

**It is self-contained, for .NET and for WASDK.** That is on purpose - the whole
point of the project is to be xcopy-deployable to a machine with nothing
installed. Do not switch it to framework-dependent to make the build smaller.

**Self-contained means the output is under a RID subfolder**, like
`bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\`. Scripts should ask MSBuild
for `OutDir` rather than guessing the path.

**Packaged *runner* mode is a different thing.** The Host is unpackaged, but it
can launch the *runner* with package identity. That goes through
`PackagedRunnerLauncher` in Shared, which calls `PackageManager` in-proc. It needs
Developer Mode on the machine. It does not shell out to anything.

**`<MicaBackdrop />` is Win11-only.** The floor for this project is Windows 10
1809. Anything newer than 1809 has to be feature-checked at runtime, not assumed.
Same goes for any new WinUI API you reach for.

## Build

```powershell
dotnet build .\ReproStudio.slnx -c Debug -p:Platform=x64
```

Use the `dotnet` CLI (SDK 10.x). VS2022's MSBuild resolves an older SDK and fails
with NETSDK1045 on net10.

Do not delete the near-empty `Directory.Build.props` at the repo root. It exists
to stop MSBuild's upward search from finding an unrelated parent props file.

There are no tests in this repo. Verify by running it.

## Before you call a change done

The GUI is the secondary front end, so a change here is not finished until you
have checked whether the console host needs the same thing. Run both:

```powershell
dotnet run --project .\src\ReproStudio.Host
.\src\ReproStudio.Cli\bin\...\ReproStudio.exe samples\hello.cs
```
