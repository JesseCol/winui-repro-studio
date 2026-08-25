# Probes

Small repro files that each answer one factual question about platform
behaviour. Run one, read the answer, move on.

These sit between the other two folders:

| | [`samples/`](../samples/) | `probes/` | [`investigations/`](../investigations/) |
|---|---|---|---|
| Job | Teach the file format | Answer one question about the platform | Produce a number for one bug |
| Size | A page | A page or two | Hundreds of lines |
| Lifetime | Forever | Forever | Until the bug closes |
| Layout | One file | One file | A folder with a write-up |

A probe is tied to platform behaviour, not to a bug, so it stays useful after
any particular bug is fixed. It is worth re-running against a new WASDK version
to see whether the answer has changed, which is why the table below records the
version each answer was taken against.

Most of these exist because the thing they check is undocumented. The file is
the documentation, and unlike a wiki page you can run it.

## The probes

| Probe | Question | Answer | Taken against |
|---|---|---|---|
| [`skip-window-redirection-surface.cs`](skip-window-redirection-surface.cs) | Does XamlChangeId 63530879 clear `WS_EX_NOREDIRECTIONBITMAP`? | Stock does not recognise the change id, so no. Untested on a candidate build. | WASDK 2.4.0, Win11 |

## Running one

Same as any other repro file:

```powershell
ReproStudio.exe probes\skip-window-redirection-surface.cs --no-watch
```

This folder ships inside the xcopy bundle, so that command works on a test
machine as well as in a clone. That matters: the answer to "has this changed?"
usually has to be taken on the odd machine, not the dev box.

To answer the question against a candidate build rather than stock, point it at
a package or a folder of loose files:

```powershell
ReproStudio.exe probes\skip-window-redirection-surface.cs --winui <path.nupkg>
ReproStudio.exe probes\skip-window-redirection-surface.cs --payload <folder>
```

## Adding one

Keep it to a single file. The folder-per-thing ceremony in `investigations/` is
too heavy for something this size, so the write-up goes in the file's own header
where you will actually see it:

```csharp
// repro: MyProbe
//
// Question: the one thing this file settles, stated so the answer is yes or no.
// Answer:   what happened, or "not yet recorded".
//
// How it tells: why the check is trustworthy, especially if the thing being
// measured is normally invisible.
```

Then add a row to the table above, including the WASDK version, because an
answer with no version on it is not much of an answer.

Two things worth doing:

- **Take a stock reading first.** It is the only thing that tells you the probe
  itself works. A broken probe gives confident wrong answers.
- **Report rather than crash.** If the thing under test is missing, catch it and
  say so, so the probe still produces a reading. A probe that throws before the
  window appears looks identical to a probe that is broken.
