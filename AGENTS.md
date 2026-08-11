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

## Testing a private WASDK build

Drop the files into a folder and point at it:

```powershell
ReproStudio.exe samples\hello.cs --payload D:\my-winui-build
```

They get copied over the provisioned runner, so a private `Microsoft.ui.xaml.dll`
beats the stock one. A `payload\` folder next to the exe is used automatically, and
`--payload none` ignores it. Payload runners provision into a separate
`<version>+payload` cache folder so stock runs stay stock.

`--winui <path.nupkg>` is the other route, for a full built package rather than
loose files. Use `--payload` for a fast edit-build-test loop.

## Measuring, when that is the task

This tool gets pointed at questions like "is this row of pixels the right
colour". Some hard-won rules:

- **Always take a stock reading before a payload reading.** It is the only thing
  that tells you the harness itself was working. A broken harness produces
  confident wrong answers, which is worse than no answer.
- **Sanity-check raw output, not just the verdict.** `GetPixel` returning
  `0xFFFFFFFF` and `GetForegroundWindow` returning `0` sat in plain sight in a
  run that reported success.
- **On a VM, check the session is actually rendering.** A Hyper-V enhanced
  session whose client detached reports Active in `qwinsta`, runs `dwm`, and
  draws nothing. Fix: `tscon 1 /dest:console` as SYSTEM. Details in
  `investigations\ecitb-8948\README.md`.
- **Screen capture, not `PrintWindow`,** for anything involving DWM frame
  composition. `PrintWindow` does not run it.

Harnesses and their findings go in `investigations\<bug>\`, not `samples\`.

## Writing a repro file

Traps that look like the tool is broken:

- **The `const string Xaml = """..."""` literal is mandatory.** Without it the
  console prints `! No 'string Xaml = ...' literal found` and `Setup` is never
  called.
- **`Path` is ambiguous.** The runner auto-imports `Microsoft.UI.Xaml.Shapes`.
  Write `System.IO.Path` in full; adding `using System.IO;` makes it worse.
- **A missing `// wasdk:` header is silent** - the file runs against whatever the
  default resolves to. Pin it in every file you intend to compare.
- **A compile error is invisible from outside**: no window, no crash, no output.
  If a run produces nothing at all, suspect the compile first.

`src\ReproStudio.Runner\Services\RoslynCompiler.cs` (`Usings`, line 33) lists what
is auto-imported. Check it before adding a `using` to a repro file.

## When something is broken

```powershell
ReproStudio.exe --doctor
```

Checks the OS floor, deployment mode, whether the base runner exists and is
self-contained, cache state, and Developer Mode. It has caught real bugs (a stale
base runner) that were otherwise silent. Use it before guessing.
