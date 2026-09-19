# Agent notes - ReproStudio

A tool for reproducing WinUI / Windows App SDK bugs against *any* WASDK version,
without rebuilding. Read `docs\how-it-works.md` for how it works. This file is
the short version of what an agent needs to not break things.

## The shape

| Project | What | WASDK? |
|---|---|---|
| `ReproStudio.csproj` (root) | CLI, `ReproStudio.exe`; source in `src\ReproStudio.Cli`. | No |
| `ReproStudio.Runner` | The preview process. One prepared copy per SDK/API and runtime combination. | Yes |
| `ReproStudio.Shared` | Repro contracts, provisioning, and launch support | No |

Read any project-local `AGENTS.md` before changing that project.

## Rules that apply everywhere

**Shared must never take a WASDK dependency.** The console host's whole value is
that it runs when WASDK is broken or absent. `Shared` gets package registration
from `Windows.Management.Deployment.PackageManager`, which comes free with a
`net10.0-windows` TFM and needs no Windows App SDK. Keep it that way.

**Keep the CLI and Runner on the same contract.** Both reference Shared for the
repro format and IPC. Keep console interaction in Cli and rendering in Runner.

**Windows 10 1809 (build 17763) is the floor.** Everything here is meant to xcopy
to an old machine with no SDK, no .NET, and no WASDK installed. Any API newer than
1809 needs a runtime check, not an assumption.

**Everything is self-contained**, for .NET and for WASDK. That is deliberate. Do
not switch anything to framework-dependent to shrink the build.

**SDK/API and native runtime are separate choices.** Matching them is the normal
path; an explicit SDK override supports compatibility repros. Prepare one coherent
managed projection/dependency set before starting a Runner. Do not change only
Roslyn references or silently fall back to the base API surface. Target-machine
provisioning must not require MSBuild or an installed SDK.

## Build

Build from the repo root:

```powershell
dotnet run
# Or build without launching:
dotnet build
.\out\Debug\x64\ReproStudio.exe samples\hello.cs
```

- `dotnet run` discovers the actual root host project. It builds the Runner as a
  build-only dependency, then starts the host. Pass host arguments after `--`.
  Do not let the Runner reference add WASDK assemblies/packages to the host.
- Host source includes are explicit: samples, investigations, Runner source,
  and old `bin`/`obj` files must never compile into the root host project.
- The CLI builds directly into `out\<Configuration>\<Platform>\` and the
  Runner into its `runner-base\` subfolder. There is no separate assembly step.
- Defaults are Debug and x64. Use `-c Release` or `-p:Platform=ARM64` / `x86`
  as needed. Solution and direct project builds use the same output layout.
- Use the `dotnet` CLI (SDK 10.x). VS2022's MSBuild resolves an older SDK and
  fails with NETSDK1045 on net10.
- Scripts should ask MSBuild for the output path rather than reconstruct it:
  ```powershell
  dotnet msbuild <proj> -getProperty:OutDir -p:Configuration=Debug -p:Platform=x64
  ```
- Keep the root build configuration files. They set the output layout and stop
  MSBuild's upward search from finding unrelated parent settings.
- `.\tools\smoke.ps1` exercises the source-to-bundle workflow with isolated
  outputs/cache. There is no separate test framework. Run the app for UI changes.

**To test a Runner change, use `dotnet run`, or build and use the exe under `out`.**
The build refreshes `runner-base` directly. Old exes under `bin\` or an old packed
bundle are not refreshed and can still run stale code.

The console prints which one it picked, so check it:

```
runner    ...\out\Debug\x64\runner-base  (portable)              <- fresh
runner    ...\AppData\Local\winui-repro-app\runner-base  (dev)    <- may be old
```

Version folders self-heal against the base, not against source. That cannot help
when the base itself is stale.

## Packing

```powershell
.\pack.ps1
```

Runs the normal Release build, copies the runnable output into
`out\ReproStudio-x64\`, and zips it. Packing is only needed for distribution
or offline bundles, not for the local build/run loop. It ships an empty payload
folder even if the development output has private DLLs in its payload folder.
Samples, probes, and investigations come from source, not editable build copies.

## Testing a private WASDK build

Drop the files into a folder and point at it:

```powershell
ReproStudio.exe samples\hello.cs --payload D:\my-winui-build
```

They get copied over the provisioned runner, so a private `Microsoft.ui.xaml.dll`
beats the stock one. A `payload\` folder next to the exe is used automatically, and
`--payload none` ignores it. Payload content participates in the SDK/runtime
pair's cache key, so stock runs stay stock.

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

- **Everything must live inside a class.** The snippet is compiled as a library,
  so top-level statements fail with `CS8805: Program using top-level statements
  must be an executable`. Wrap it:
  ```csharp
  class Repro
  {
      const string Xaml = """<Grid/>""";
      static void Setup(FrameworkElement root) { }
  }
  ```
- **The `const string Xaml = """..."""` literal is mandatory.** Without it the
  console prints `! No 'string Xaml = ...' literal found` and `Setup` is never
  called.
- **`Path` is ambiguous.** The runner auto-imports `Microsoft.UI.Xaml.Shapes`.
  Write `System.IO.Path` in full; adding `using System.IO;` makes it worse.
- **A missing `// wasdk:` header is silent** - the file runs against whatever the
  default resolves to. Pin it in every file you intend to compare.
- **A compile error is not a crash.** Check the Runner's error panel and
  `%TEMP%\winui-repro-app\runner.log`. Headless one-shot runs also print the error
  and return a nonzero exit code, even when an error-panel PNG was saved.

`src\ReproStudio.Runner\Services\RoslynCompiler.cs` (`Usings`, line 33) lists what
is auto-imported. Check it before adding a `using` to a repro file.

## When something is broken

```powershell
ReproStudio.exe --doctor
```

Checks the OS floor, deployment mode, whether the base runner exists and is
self-contained, cache state, and Developer Mode. It has caught real bugs (a stale
base runner) that were otherwise silent. Use it before guessing.
