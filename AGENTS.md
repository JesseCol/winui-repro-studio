# Agent notes - ReproStudio

A tool for reproducing WinUI / Windows App SDK bugs against *any* WASDK version,
without rebuilding. Read `README.md` for how it works. This file is the short
version of what an agent needs to not break things.

## The shape

| Project | What | WASDK? |
|---|---|---|
| `ReproStudio.Cli` | Console host, `ReproStudio.exe`. **The one that ships.** | No |
| `ReproStudio.Host` | Optional WinUI GUI front end | Yes |
| `ReproStudio.Runner` | The preview process. One copy per WASDK version. | Yes |
| `ReproStudio.Shared` | Everything both hosts need | No |

Each project folder has its own `AGENTS.md` where the rules are sharper. Read the
one for whatever you are touching.

## Rules that apply everywhere

**Shared must never take a WASDK dependency.** The console host's whole value is
that it runs when WASDK is broken or absent. `Shared` gets package registration
from `Windows.Management.Deployment.PackageManager`, which comes free with a
`net10.0-windows` TFM and needs no Windows App SDK. Keep it that way.

**Shared behaviour goes in Shared.** Two front ends, one engine. If you add a
feature to one host that the other would want, it belongs in Shared.

**Windows 10 1809 (build 17763) is the floor.** Everything here is meant to xcopy
to an old machine with no SDK, no .NET, and no WASDK installed. Any API newer than
1809 needs a runtime check, not an assumption.

**Everything is self-contained**, for .NET and for WASDK. That is deliberate. Do
not switch anything to framework-dependent to shrink the build.

## Build

```powershell
dotnet build .\ReproStudio.slnx -c Debug -p:Platform=x64
```

- Use the `dotnet` CLI (SDK 10.x). VS2022's MSBuild resolves an older SDK and
  fails with NETSDK1045 on net10.
- Self-contained output lands under a RID subfolder
  (`bin\x64\Debug\<tfm>\win-x64\`). Ask MSBuild for `OutDir`, don't guess.
- Do not delete the near-empty `Directory.Build.props` at the repo root. It stops
  MSBuild's upward search from finding an unrelated parent props file.
- There are no tests. Verify by running it.

## Packing

```powershell
.\pack.ps1
```

Builds the Runner and the Cli, stages `artifacts\ReproStudio-x64\`, and zips it.
The staged folder is what gets xcopied to a test machine. `pack.ps1` also
refreshes `runner-base`, so run it after changing the Runner.

## When something is broken

```powershell
ReproStudio.exe --doctor
```

Checks the OS floor, deployment mode, whether the base runner exists and is
self-contained, cache state, and Developer Mode. It has caught real bugs (a stale
base runner) that were otherwise silent. Use it before guessing.
