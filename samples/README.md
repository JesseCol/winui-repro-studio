# Sample repros

Ready-to-run single-file repros.

From the repository root:

```powershell
dotnet run
dotnet run -- samples\counter.cs
```

From a built or unzipped bundle folder:

```powershell
.\ReproStudio.exe
```

With no file argument, the CLI opens its bundled `samples\hello.cs`. Pass a path
to run another repro. Every save refreshes the runner; Ctrl+C stops it.

Press **V** in the watching console, or use `.\ReproStudio.exe --list`, to see
available WASDK versions. Launch with `--prerelease` to include previews.
Copy a version into the file's `// wasdk:`
header and save to switch. Unpinned samples use the newest stable version;
the pinned teaching examples use WASDK 2.x.

| File | What it shows |
|---|---|
| [hello.cs](hello.cs) | A feature quick tour, XAML, a button wired up in `Setup`, and a generated Win32 call for a dark title bar on Windows 11. |
| [counter.cs](counter.cs) | C# driving the XAML (a click counter), plus `theme: Dark`. |
| [full-header.cs](full-header.cs) | Launch and display headers, annotated. Good starting point for a new repro. |
| [pinvoke.cs](pinvoke.cs) | Your own `using` directives and `[DllImport]`. Gets the HWND and calls into `dwmapi`. |
| [cswin32.cs](cswin32.cs) | `// win32:` generates bindings with CsWin32. Reads the window rectangle and DPI. |

Start with **hello**, then **counter** to see state and events. Use **cswin32**
for generated interop, or **pinvoke** when you want to supply the declarations
yourself. **full-header** explains launch/display choices; it is not a template
you must fill out.

Copy a sample before turning it into your own bug. The default no-file launch
opens a build copy; passing `samples\hello.cs` from the repo root opens the source
file instead. Keep the `class Repro` wrapper and `const string Xaml` literal.

## The format, in one breath

```csharp
// repro:      My cool bug     <- friendly name
// sdk:        match           <- API surface: match | base | WASDK version
// wasdk:      2.2             <- partial ok; newest 2.2.x wins
// winui:      default         <- version | path to a .nupkg | default
// payload:    none            <- folder of files to copy over the runner
// packaged:   no              <- give the runner package identity
// theme:      Dark            <- Default | Light | Dark
// flow:       LeftToRight     <- LeftToRight | RightToLeft
// dpi:        100             <- 100 to 400
// background: #202020         <- stage colour behind your XAML
// win32:      GetWindowRect, GetDpiForWindow  <- generate Win32 bindings

class Repro
{
    const string Xaml = """ <StackPanel/> """;
    static void Setup(FrameworkElement root, Window window) { /* your logic */ }
}
```

Every header key is optional and order doesn't matter. The whole thing stays valid
C#, so your editor's C# tooling keeps working.

Two kinds of key:

| Kind | Keys | On save |
|---|---|---|
| Live | `theme`, `flow`, `background`, `win32`, and the XAML/C# itself | re-renders in place |
| Launch-time | `sdk`, `wasdk`, `winui`, `payload`, `packaged`, `dpi` | provisions and relaunches the runner |

The SDK/API surface matches the native runtime by default. Add `// sdk: <version>`
to compile against a different Windows App SDK, or `// sdk: base` to keep the
bundled API surface. This is separate from choosing WASDK or WinUI as the native
runtime source. New SDK APIs can compile but fail when called on an older runtime.
See the [SDK/runtime guide](../docs/guide.md#sdkapi-and-runtime).

Pin is a Runner toolbar preference shared across launches, not a repro header.
Legacy `// topmost:` comments are ignored and left untouched.

> The `Xaml` literal is required. Without a `const string Xaml = """..."""` the
> runner has nothing to render and never calls `Setup`, and the console says so.

> These files aren't part of any project - they're inputs to the tool, compiled at
> runtime by the runner. Only open repros you trust; the runner has no sandbox.

## Writing your own

Two traps that cost real time, both of which look like the tool is broken:

- **`Path` is ambiguous.** The runner auto-imports `Microsoft.UI.Xaml.Shapes`,
  which has its own `Path`. Write `System.IO.Path` in full. Adding
  `using System.IO;` doesn't fix it; it makes it worse.
- **A missing `// wasdk:` header is silent.** The file runs against whatever the
  command line or default picks. If you are comparing two repro files, pin the
  version in both or you may be comparing versions rather than code.

See [pinvoke.cs](pinvoke.cs) for how to add your own `using` directives and
`[DllImport]` declarations on top of what the runner already imports.

Or use [cswin32.cs](cswin32.cs) for generated bindings:

```powershell
# From the repository root:
dotnet run -- samples\cswin32.cs
# From an unzipped bundle:
.\ReproStudio.exe samples\cswin32.cs
```

Put `// win32: GetWindowRect, GetDpiForWindow` in the leading comment header,
before any `using` or class. Names are case-sensitive and comma-separated;
whitespace is ignored. Repeated `win32` headers append names, ignoring exact
duplicates. Blank lines and other `//` comments may separate headers. The first
nonblank, non-`//` line ends the header; strings and later comments are not scanned.
Empty entries and invalid/unknown names fail visibly rather than being ignored.

The runner supplies an in-memory `NativeMethods.txt` and generates
`Windows.Win32.PInvoke` plus supporting types (for example,
`Windows.Win32.Foundation.HWND` and `RECT`) in your snippet assembly. No extra
files, SDK, NuGet cache or network are needed for generation; WASDK provisioning
is separate. Saving the API list regenerates it live. Generator warnings/errors
appear as **CsWin32 generation failed**; diagnostics mentioning `NativeMethods.txt`
use the merged request-list line numbers. Handwritten `[DllImport]` still works.
Use `--payload none` with either command above to ignore private runtime payloads.

## Investigation harnesses

`samples\` holds small teaching examples. Larger measurement harnesses written to
chase a specific bug live in [`investigations/`](../investigations/) instead, with
a write-up of what they found.
