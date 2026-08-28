# ReproStudio

A little tool for reproducing WinUI / Windows App SDK bugs. You give it some XAML
(and optional C#), and it renders live in a preview window using whatever WASDK
version you pick. Great for "does this repro on 1.6 but not 2.2?"

There are two front ends over the same engine:

| | `ReproStudio.exe` (console) | `ReproStudio.Host.exe` (WinUI) |
|---|---|---|
| What it is | Runs a `.cs` repro file, watches it, logs what it's doing | A GUI with editors, dropdowns, and a file picker |
| Needs WASDK | No | Yes |
| Good for | Shipping to a test machine, diagnosing a broken runner, agent/script driving | Authoring a repro on your dev box |

The console host is the one you copy to another machine. It has no Windows App SDK
dependency at all, so it still runs and can still tell you what went wrong when the
WASDK build under test refuses to start.

## Quick start

If you cloned the repo, build a bundle first. There is no exe in the tree:

```powershell
.\pack.ps1
cd artifacts\ReproStudio-x64
```

That takes a few minutes and leaves you with the same folder the zip contains.
If you got the zip instead, just unzip it and skip straight here.

Point it at a repro file:

```powershell
ReproStudio.exe samples\hello.cs
```

```
ReproStudio
  file      C:\ReproStudio-x64\samples\hello.cs
  cache     C:\Users\you\AppData\Local\winui-repro-app
  runner    C:\ReproStudio-x64\runner-base  (portable)
  . No version asked for, using the newest: 2.3.1

> provision
  wasdk     2.3.1
  packaged  no
  ready: ...\versions\2.3.1\ReproStudio.Runner.exe

> launch
  running (unpackaged), pid 30528

> watching
  . Save the file to push changes. Ctrl+C to stop.
```

A preview window opens. Leave the console running, edit the file in your editor,
and save:

```
  21:08:55  pushed  Hello from a file
```

Same window, same process - your XAML just re-rendered. That's the loop.

The first run for a given version downloads it and unpacks a private copy of the
runner, so budget a minute. Each version costs 156 to 260 MB on disk, depending
on the version, plus the NuGet packages it caches. After that the version is
cached and starts right away.

Stuck? `ReproStudio.exe --doctor` checks the machine and says what's wrong.

## Compare two WASDK versions

This is what the tool is for. While it's watching, change the `wasdk:` header and
save:

```csharp
// wasdk: 1.6
```

```
  . 1.6 resolved to 1.6.250602001
  21:09:01  relaunching: 1.6.250602001
  21:09:02  running 1.6.250602001
```

`wasdk` is a launch-time key, so rather than re-rendering in place it provisions
that version and starts a fresh runner on it. Change the header back and save
again to flip the other way. Your XAML and C# never move.

Two things worth knowing:

- The Runner's footer shows the exact `Microsoft.ui.xaml.dll` it loaded, so you can
  confirm you really are on the version you asked for.
- Partial versions are fine. `1.6` picks the newest 1.6.x.

You can also drive it from the command line, without editing the file:

```powershell
ReproStudio.exe repro.cs --wasdk 1.6      # override the header
ReproStudio.exe --list                    # what versions can I ask for?
```

## The repro file

A repro is one `.cs` file: a tiny `// key: value` header up top, the XAML in a
`Xaml` raw-string, and your `Setup` method. It stays valid C#, so your editor's C#
tooling still works. Both hosts read the same format.

The console host takes it as an argument. The WinUI host takes it through **Open
file...**, after which the in-app editors turn into a read-only mirror.

Want something to open right now? There are ready-made repros in
[`samples/`](samples/) - try `samples/hello.cs`.

```csharp
// repro:      My cool bug
// wasdk:      1.7            <- partial is fine; newest 1.7.x wins (see the guide)
// winui:      default        <- or a version, or a path to a local .nupkg
// payload:    none           <- or a folder of files to copy over the runner
// packaged:   no             <- give the runner package identity
// theme:      Dark           <- Default | Light | Dark
// flow:       LeftToRight    <- LeftToRight | RightToLeft
// dpi:        100            <- 100 to 400
// background: #202020        <- stage colour behind your XAML
// topmost:    no             <- keep the runner above other windows

class Repro
{
    const string Xaml = """
        <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    Padding="24" Spacing="12">
            <TextBlock Text="Hello from a file!" FontSize="28" />
            <Button x:Name="HelloButton" Content="Click me" />
        </StackPanel>
        """;

    static void Setup(FrameworkElement root, Window window)
    {
        Log("Loaded from file.");
        if (root.FindName("HelloButton") is Button b)
        {
            b.Click += (s, e) => b.Content = "Clicked!";
        }
    }
}
```

The `class Repro { }` wrapper is required. The snippet is compiled as a library,
so top-level statements fail with `CS8805`, and a compile error means no window
and no output - it just looks like nothing happened.

Every header key is optional, and order doesn't matter. Two kinds of keys:

- **Live** (`theme`, `flow`, `background`, `topmost`) and the XAML/C# itself: a
  save just re-renders in place. Fast.
- **Launch-time** (`wasdk`, `winui`, `payload`, `packaged`, `dpi`): these pick
  which Runner exe runs and how, so a change re-provisions and relaunches.

## Where to go next

| Doc | What's in it |
|---|---|
| [Guide](docs/guide.md) | Everything else you can do: take it to another machine, test a private WASDK build, test a WinUI repo build, run the WinUI host, and more of the repro file format |
| [How it works](docs/how-it-works.md) | How the two processes fit together, how a runner gets built for each WASDK version, the cache, packaged mode, the IPC, and the gotchas |
