# ReproStudio

A little WinUI 3 app for reproducing WinUI / Windows App SDK bugs. You paste in
some XAML (and optional C#), and it renders live in a preview window using
whatever WASDK version you pick. Great for "does this repro on 1.6 but not 2.2?"

## Run it

From this folder:

```powershell
dotnet run --project .\src\ReproStudio.Host
```

That's it. First run pulls packages and builds, so give it a minute. A window
pops up. Paste XAML, watch it render.

> Heads up: `dotnet run` prints an AUMID and a PID, not the usual console spam.
> That's expected, the app runs with package identity. See "How running works"
> below if you're curious why.

## Using it

Two windows show up: the **Host** (your editor) and the **Runner** (the live
preview, docked to its right).

In the Host:
- Pick a **WASDK** version. Optionally pick a **WinUI** version, or **Browse
  .nupkg** to try a local WinUI build.
- Type in the **XAML** or **C#** tab. They're tabs, not side by side, so you get
  the full width for whichever you're editing. Edits stream to the preview live
  (a short debounce).
- **Relaunch** restarts the preview. **Clear cache** wipes the provisioned
  versions and rebuilds the current one.
- **Keep on top** pins the Runner window above everything else. It's a live
  toggle, so no relaunch, and it sticks when you switch versions.
- **Packaged** launches the preview runner *with package identity* (registered as
  a loose-layout package and activated by AUMID) instead of unpackaged. It's **on
  by default** - so the runner has identity out of the box, handy for reproing bugs
  that only show up when the app is packaged. Uncheck it to run unpackaged. Toggling
  relaunches the runner, and the status line tells you which mode you're in
  (`packaged` vs `unpackaged`).

The Runner window shows your rendered snippet, a log panel, and a footer with the
exact `Microsoft.ui.xaml.dll` version it loaded - so you always know what's really
running.

> **Careful what you paste.** The Runner compiles and runs your C# for real, with
> **no sandbox**. Only paste code you trust. (The app warns you about this too.)

## Drive it from your own editor (VS Code, etc.)

Don't like the built-in editors? Hit **Open file...** and point the Host at a
single `.cs` repro file. From then on the Host just watches that file. Every time
you save it (in VS Code, or from a script, or from an LLM), the Runner refreshes.
The in-app editors turn into a read-only mirror so you can see what's rendering.

Want something to open right now? There are ready-made repros in
[`samples/`](samples/) - try `samples/hello.cs`.

### Skip the picker: launch straight into a file

For hands-off / agent driving, point the app at a file on launch and it opens it
for you - no clicking:

```powershell
dotnet run --project .\src\ReproStudio.Host -p:WinAppLaunchArgs="--file D:\path\to\repro.cs"
```

Use an **absolute path** (the app is packaged, so its working directory isn't your
shell's). After that it's the same watch-and-refresh loop: the agent just writes
and saves the file.

The whole repro lives in one `.cs` file: a tiny `// key: value` header up top, the
XAML in a `Xaml` raw-string, and your `Setup` method. It stays valid C#, so your
editor's C# tooling still works.

```csharp
// repro: My cool bug
// wasdk: 1.7            <- partial is fine; newest 1.7.x wins (see below)
// winui: default        <- or a version, or a path to a local .nupkg
// theme: Dark           <- Default | Light | Dark
// flow:  LeftToRight    <- LeftToRight | RightToLeft

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

Every header key is optional, and order doesn't matter. Two kinds of keys:

- **Live** (`theme`, `flow`) and the XAML/C# itself: a save just re-renders in
  place. Fast.
- **Launch-time** (`wasdk`, `winui`): these pick which Runner exe runs, so a change
  re-provisions and relaunches. Same machinery as the dropdowns.

### You don't have to type the whole WASDK version

`wasdk: 1.7` is enough. The Host matches your text against the real version list
by dotted segments and picks the newest one that fits, so `1.7` finds
`1.7.250401001`. An exact version still works too. If nothing matches, it tries
your text as-is (and tells you if that fails).

## Build it (without running)

Everything is x64. Build the whole solution with:

```powershell
dotnet build .\ReproStudio.slnx -c Debug
```

---

# How it works

Skip this unless you want the guts. Short version: it's two apps talking through
a JSON file, and your C# gets compiled at runtime with Roslyn.

## The two processes

```
+-------------------+   request.json   +----------------------+
|  ReproStudio.Host | ---------------> |  ReproStudio.Runner  |
|  (the UI you use) |   (JSON on disk) |  (the preview window)|
|  packaged, WinUI  | <--- launches -- |  unpackaged, WinUI   |
+-------------------+                  +----------------------+
         \                                      /
          \________ ReproStudio.Shared ________/
                   (the Snippet contract)
```

- **Host** is the app you interact with. It's packaged (MSIX identity).
- **Runner** is a separate, throwaway process that does the actual rendering.
  There's one Runner per WASDK version.
- **Shared** is the tiny contract both sides agree on (the `Snippet` type plus
  the read/write helpers).

Why two processes? So we can render the same snippet against *different* WASDK
versions side by side. Each version gets its own Runner exe with that version's
runtime DLLs next to it. The Host just launches whichever one you asked for.

## Running different WASDK versions

This is the heart of the tool, and the trickiest part. The goal: run the same
Runner against WASDK 1.5, or 1.7, or 2.2, without rebuilding it each time.

### The base runner (built once)

We build the Runner **once**, self-contained, against the latest WASDK. That
build - the "base" - lives at `%LOCALAPPDATA%\winui-repro-app\runner-base`. It
has three kinds of files:

- The app itself: `ReproStudio.Runner.exe`, its dll, its PRI, Roslyn, the .NET bits.
- The **managed** WASDK projections: `Microsoft.WinUI.dll`,
  `Microsoft.Windows.SDK.NET.dll`, `WinRT.Runtime.dll`, the `*.Projection.dll`s.
- The **native** WASDK runtime: `Microsoft.ui.xaml.dll`,
  `Microsoft.WindowsAppRuntime.dll`, `MRM.dll`, and friends.

> **Build the base with `dotnet build`, not `dotnet publish`.** Publish drops the
> app resource index (`ReproStudio.Runner.pri`), and without it the runner crashes
> at startup with *"Cannot locate resource ms-appx:///Microsoft.UI.Xaml/Themes/
> themeresources.xaml"*. Copy the self-contained output of
> `dotnet build .\src\ReproStudio.Runner -c Release -p:Platform=x64` into
> `runner-base`.

### Making a runner for version X

When you pick version X, the Host provisions a folder for it (see
`RunnerProvisioner.EnsureRunnerAsync`):

```
  fresh copy of runner-base        version X's NATIVE dlls
  (app + managed, left as-is)  +   (overlaid on top)         =  versions\X\
```

1. Copy the base into `versions\X\`.
2. Download version X's NuGet packages (cached under `nupkgs\`, so this only
   happens once per version).
3. Overlay **only the native DLLs** from those packages over the copy.
4. Launch `versions\X\ReproStudio.Runner.exe`.

The managed projections stay at the base version. Only the native DLLs change.

### Two package layouts (the 1.8 split)

Where the native DLLs live *inside* the NuGet package changed at WASDK 1.8:

- **1.7 and earlier:** the metapackage has no component dependencies. The native
  runtime is zipped inside a framework **`.msix`** at
  `tools\MSIX\win10-x64\Microsoft.WindowsAppRuntime.<ver>.msix`. We unzip that
  msix to get the DLLs.
- **1.8 and later:** the metapackage pulls in component sub-packages
  (`.Foundation`, `.WinUI`, `.Runtime`, ...), each carrying loose native files
  under `runtimes-framework\win-x64\native`.

We tell them apart by asking "does the metapackage have `Microsoft.WindowsAppSDK.*`
dependencies?" - not by a version number - so the boundary is detected on its own
and won't break if Microsoft moves it again.

### Why swap only the native DLLs?

Because the Runner was **compiled** against the base version's managed assemblies,
and those assembly versions are baked into the exe. Overlay a different version's
managed `Microsoft.WinUI.dll` and the app dies at startup with "Could not load
... Version=X". So we leave the managed side alone and swap only native.

It works because WinRT keeps a stable ABI for released types: the base's managed
`Button` projection can drive version X's native `Button`, as long as the type
existed in both. For repro scenarios (layout, rendering, common controls) that
holds. The catch to know: the C# API surface your snippet sees is always the base
version's, even though the *rendering* is version X's.

### Why self-contained, not framework-dependent?

A **self-contained** build loads its WASDK native DLLs from right next to the
exe. That's exactly what lets us drop a different version's DLLs there and have
them win.

A **framework-dependent** build does the opposite: it uses the bootstrapper to
find an *installed* WASDK framework and loads from there, ignoring what's next to
the exe. We tried it - the runner just popped "This application could not be
started". So the base has to be self-contained.

### Seeing which version really loaded

The Runner window has a footer showing the version of the `Microsoft.ui.xaml.dll`
it *actually* loaded (see `MainWindow.GetLoadedWinUiVersion`). It reads the loaded
module, so it's ground truth, not a guess. If an overlay ever goes wrong, the
wrong number shows up right there.

### The cache

Everything lives under `%LOCALAPPDATA%\winui-repro-app\`:

| Folder | What |
|---|---|
| `runner-base\` | The self-contained base runner (built once, copied in by you). |
| `nupkgs\`      | Downloaded + extracted WASDK NuGet packages. |
| `versions\`    | One assembled runner per version you've used. |
| `local-winui\` | Extracted local WinUI `.nupkg` overrides. |

The **Clear cache** button in the Host wipes `versions\` and `local-winui\` (it
keeps `nupkgs\`, so re-provisioning is fast), then rebuilds the current version.
Handy after you rebuild the base, or if a version folder ever gets wedged.

## The IPC: it's just a file

No pipes, no sockets, no localhost server. The whole channel is one JSON file.

1. The Host makes a temp folder like
   `%TEMP%\winui-repro-app\runner-<8 hex>\request.json` (see `RunnerHost.cs`).
2. When you edit a snippet, the Host serializes it to that file. It writes to a
   `.tmp-<guid>` file first, then renames it over the real one. Rename is atomic
   on the same drive, so the Runner never sees a half-written file
   (`SnippetIo.WriteAtomic`).
3. The Host launches the Runner exe pointed at that file:
   `ReproStudio.Runner.exe --request <path> --bounds <x y w h>`.
4. The Runner puts a `FileSystemWatcher` on the file. On a change it waits 150ms
   (debounce, so a burst of saves collapses into one), then re-reads and
   re-renders. If the read catches a mid-write or locked file, `TryRead` returns
   null and it just waits for the next event.

So "switch WASDK version" is really just "launch a different Runner exe watching
the same request file." No rebuild. Nice and dumb.

## Rendering a snippet

A `Snippet` carries some XAML and optional C#. The Runner turns it into live UI
in `RenderEngine.Render`:

**1. XAML -> tree.** The XAML string is parsed at runtime with `XamlReader.Load`.
There's no compiled XAML anywhere in the Runner (more on that below), so the
Runner's `App` implements `IXamlMetadataProvider` by hand. That's what lets
`XamlReader` resolve built-in controls like `Button` and `Grid`.

**2. C# -> in-memory assembly, via Roslyn.** If the snippet has C#, we compile it
with Roslyn (`Microsoft.CodeAnalysis.CSharp`) in `RoslynCompiler.Compile`:

- We prepend a fixed block of `using`s so snippets stay short. One of them is
  `using static ReproStudio_Runner.ReproApi;`, which is how a snippet can just
  call `Log("hi")` and have it show up in the Runner's log panel.
- The references are the Runner's *own already-loaded assemblies*
  (`AppDomain.CurrentDomain.GetAssemblies()`). So your snippet compiles against
  the very same WinUI assemblies the Runner is using - no separate SDK reference
  that could drift out of sync. (That's the *managed* projection, which is pinned
  to the base version - see "Running different WASDK versions" for why the native
  side can be a different version.)
- It emits to a `MemoryStream` and loads that into a **collectible**
  `AssemblyLoadContext`. Each edit unloads the previous one, so hammering the
  editor doesn't leak an assembly per keystroke.

**3. Wire it up.** We reflect over the compiled assembly for a `public static`
method named `Setup`, and call it. Parameters are filled by type, so any of
these work:

```csharp
static void Setup(FrameworkElement root)             { ... }
static void Setup(Window window)                     { ... }
static void Setup(FrameworkElement root, Window win) { ... }
```

`root` is the parsed XAML tree, so your C# can find elements and hook up events.

Any failure is tagged by phase (`xaml`, `csharp-compile`, or `runtime`) and shown
in an InfoBar instead of taking down the Runner. Compile errors even get their
line numbers fixed up so they match your snippet, not the prepended usings.

## Why the Runner has no XAML files

The Runner is built entirely in code, no `App.xaml`, no `MainWindow.xaml`. That's
deliberate. Compiled XAML bakes in a WASDK version stamp, and we specifically
want one Runner build that can load *any* version's runtime DLLs dropped next to
the exe. So `App.cs` and `MainWindow.cs` hand-write the few things the XAML
compiler would normally generate (registering `XamlControlsResources`,
implementing `IXamlMetadataProvider`).

## How running works (the AUMID thing)

The Host references `Microsoft.Windows.SDK.BuildTools.WinApp`. That package hooks
`dotnet run` and, instead of launching the raw exe, hands off to the `winapp` CLI
which registers a loose-layout package and launches the app with real package
identity (via AUMID). That's why you see an AUMID and a PID printed instead of
console output. To attach a debugger, attach to that PID.

## Gotchas

- A running Runner **locks its own .exe**, so a rebuild can fail with a file-lock
  error. Kill leftover `ReproStudio.Runner` processes first.
- Use `dotnet` (SDK 10.x), not VS2022's MSBuild. VS resolves an older SDK and
  chokes on net10 (NETSDK1045).
- The provisioned runners and downloaded packages live under
  `%LOCALAPPDATA%\winui-repro-app\` (see the cache table above) and aren't in the
  repo. Use the **Clear cache** button to wipe the provisioned versions.
