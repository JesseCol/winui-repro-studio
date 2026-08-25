# Investigations

Measurement harnesses written to chase a specific bug, kept with a write-up of
what they found.

These are not samples. [`samples/`](../samples/) holds small teaching examples of
what a repro file looks like; the files here are instruments, often several
hundred lines, that exist to produce a number. They're worth keeping because
someone will want to re-take that number against a candidate fix, and rebuilding
the instrument from scratch is where the mistakes come from.

They're also not probes. [`probes/`](../probes/) holds one-file checks that
settle a single question about platform behaviour. The difference that matters:
a probe is tied to the platform and stays useful forever, while an investigation
is tied to a bug and becomes history once that bug closes.

| Folder | Bug | Status |
|---|---|---|
| [`ecitb-8948/`](ecitb-8948/) | [#8948][8948] - `ExtendsContentIntoTitleBar` leaves a stripe on the top edge on Windows 10 | Baseline measured; candidate fix unresolved |

[8948]: https://github.com/microsoft/microsoft-ui-xaml/issues/8948

Run one the same way as any other repro:

```powershell
ReproStudio.exe investigations\ecitb-8948\ecitb-border-match.cs --no-watch
```

## Adding one

A folder per bug, named after the issue, with a `README.md` covering:

- **The question the harness asks**, precisely. Two harnesses measuring nearly
  the same thing can disagree and both be right.
- **The numbers**, and how many times they were reproduced.
- **What's been ruled out.** This is what stops the next person redoing a
  bisection that's already been done.
- **What's still open**, honestly, including anything that might be an artifact
  of the harness rather than a real finding.

Write down the traps you hit. Every gotcha in `ecitb-8948/README.md` cost at
least one wasted measurement run, and a couple of them silently produced
confident wrong answers, which is worse.
