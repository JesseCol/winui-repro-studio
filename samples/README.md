# Sample repros

Ready-to-open single-file repros.

From the console host:

```powershell
ReproStudio.exe samples\hello.cs
```

From the WinUI host: hit **Open file...** and pick one. Either way the file is
watched, so every save refreshes the runner.

| File | What it shows |
|---|---|
| [hello.cs](hello.cs) | The basics: a header, XAML in a raw-string, a button wired up in `Setup`. |
| [counter.cs](counter.cs) | C# driving the XAML (a click counter), plus `theme: Dark`. |
| [full-header.cs](full-header.cs) | Every header key, annotated. Good starting point for a new repro. |
| [pinvoke.cs](pinvoke.cs) | Your own `using` directives and `[DllImport]`. Gets the HWND and calls into `dwmapi`. |

## The format, in one breath

```csharp
// repro:      My cool bug     <- friendly name
// wasdk:      1.7             <- partial ok; newest 1.7.x wins
// winui:      default         <- version | path to a .nupkg | default
// payload:    none            <- folder of files to copy over the runner
// packaged:   no              <- give the runner package identity
// theme:      Dark            <- Default | Light | Dark
// flow:       LeftToRight     <- LeftToRight | RightToLeft
// dpi:        100             <- 100 to 400
// background: #202020         <- stage colour behind your XAML
// topmost:    no              <- keep the runner above other windows

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
| Live | `theme`, `flow`, `background`, `topmost`, and the XAML/C# itself | re-renders in place |
| Launch-time | `wasdk`, `winui`, `payload`, `packaged`, `dpi` | provisions and relaunches the runner |

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

## Investigation harnesses

`samples\` holds small teaching examples. Larger measurement harnesses written to
chase a specific bug live in [`investigations/`](../investigations/) instead, with
a write-up of what they found.
