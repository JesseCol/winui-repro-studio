# Sample repros

Ready-to-open single-file repros. In the Host, hit **Open file...** and pick one.
The Host watches it, so every save refreshes the Runner. The in-app editors turn
into a read-only mirror.

| File | What it shows |
|---|---|
| [hello.cs](hello.cs) | The basics: a header, XAML in a raw-string, a button wired up in `Setup`. |
| [counter.cs](counter.cs) | C# driving the XAML (a click counter), plus `theme: Dark`. |

## The format, in one breath

```csharp
// repro: My cool bug        <- friendly name (optional)
// wasdk: 1.7                 <- partial ok; newest 1.7.x wins (optional)
// winui: default             <- version | path to a .nupkg | default (optional)
// theme: Dark                <- Default | Light | Dark (optional)
// flow:  LeftToRight         <- LeftToRight | RightToLeft (optional)

class Repro
{
    const string Xaml = """ <StackPanel/> """;
    static void Setup(FrameworkElement root, Window window) { /* your logic */ }
}
```

Every header key is optional and order doesn't matter. Leave out `wasdk`/`winui`
to keep whatever the Host already has selected. The whole thing stays valid C#, so
your editor's C# tooling keeps working.

> These files aren't part of any project - they're inputs to the tool, compiled at
> runtime by the Runner. Only open repros you trust; the Runner has no sandbox.
