# ECITB top row on Windows 10 (issue #8948)

Measurement harnesses for [microsoft-ui-xaml#8948][issue]: extending content into
the title bar leaves a stripe along the top edge on Windows 10 that isn't there on
Windows 11.

These aren't samples. They're instruments built to answer one question each, and
they're kept because the numbers below came out of them and someone will want to
re-take those numbers against a candidate fix.

[issue]: https://github.com/microsoft/microsoft-ui-xaml/issues/8948

## The two harnesses ask different questions

| File | Question |
|---|---|
| `ecitb-top-row.cs` | Does the reserved row match the app's own **content**? |
| `ecitb-border-match.cs` | Does the reserved row match the window's own **native border**? |

They can disagree and both be right, so check which one you're reading.

The first question turned out to be the wrong one. A fix that hands the reserved
row to DWM makes it render as *frame*, not content, so it will never equal the
content colour. That isn't a failure: the other three sides of every window are
frame too. What matters is whether the top edge is consistent with the left,
right and bottom edges of the same window. `ecitb-border-match.cs` was written
after that correction, and it's the one to trust.

We got this wrong once. An early FAIL verdict came from `ecitb-top-row.cs` and
the right response was to change the success criterion, not to reject the fix.

## Running one

```powershell
ReproStudio.exe investigations\ecitb-8948\ecitb-border-match.cs --no-watch
```

Both pin `// wasdk: 2.3.1`. Keep that pinned when comparing runs, or you risk
comparing WASDK versions rather than the thing you meant to change.

Each appends its verdict plus the full table to `%TEMP%\winui-repro-app\`
(`ecitb-border.txt`, `ecitb-top-row.txt`). **They append**, so delete the file
before a run or two runs interleave.

To measure a private build, drop the DLL into `payload\` and run again. Take the
stock reading first.

`ecitb-border-match.cs` has a `BuildSubjects` constant near the top: letters
`w`, `a`, `c` select which windows get built. Useful for bisecting when a runtime
under test misbehaves.

## Stock Windows 10 1809 baseline (WASDK 2.3.1)

Measured four times over two sessions, identical every time, across two backdrop
colours and both focus states.

| Subject | content starts (30% / 50% / 70% across) | top edge at depth 0 |
|---|---|---|
| `Window.ExtendsContentIntoTitleBar` | 1px / 1px / 1px | `#FFFFFF`, opaque |
| `AppWindow.TitleBar.ExtendsContentIntoTitleBar` | 0px / 0px / 32px | app content, `#FFFFFF` under the caption buttons |
| control, no ECITB | 31px / 31px / 31px | native glass, matches its own sides |

Three things fall out of that:

- **The `Window` entry point leaves exactly one row of opaque white.** That's
  #8948, measured.
- **The `AppWindow` entry point already reaches 0px.** The `70% = 32px` is the
  caption-button block, which is expected.
- **The two entry points do not converge on Windows 10 1809.** This matters
  because the issue's repro uses the `AppWindow` one and its expected behaviour
  cites the `Window` one. `IsCustomizationSupported()` returns `True` and both
  properties read back `true`, so neither is a silent no-op.

The control window **passes** the border-match test. That's the instrument
validating itself: if the control had failed, the harness would be wrong.

## Native Win10 border blend

Fitting `edge = a * background + k` per channel. All three channels agreed to
within 1/255, which is what makes these numbers worth writing down.

| Surface | a | k | implied source colour |
|---|---|---|---|
| native side border, active | 0.556 | 15.3 | `#232323` |
| native side border, inactive | 0.50 | 43 | `#565656` |
| DWM-glass prototype top row | 0.558 | 42.9 | `#616161` |

The prototype's top row has the alpha of an **active** border and the tint of an
**inactive** one. Close, not matching.

## The DWM-glass prototype: unresolved

A candidate fix (a chk build of `Microsoft.ui.xaml.dll` overlaid on retail WASDK
2.3.1) crashes on Windows 10 1809:

- `System.InvalidCastException: No such interface supported` - an E_NOINTERFACE
  from a COM/WinRT QI. Logged as `UnhandledException` in
  `%TEMP%\winui-repro-app\runner.log`. The process dies, so no verdict is written.
- **Timing is exact:** about 170 ms after the windows are built, during the first
  composite.
- **The throw is asynchronous.** It escapes a try/catch that wraps the entire
  body, which is why a flush-per-line breadcrumb file found it and exception
  handling didn't.
- **It needs a rendering desktop.** No paint, no crash. See the gotcha below.

Ruled out by bisection: both ECITB entry points, a plain control window with no
ECITB, window count, the measurement phase, and WASDK version differences.
Every single subject crashes on its own.

**Don't report this as a defect in the fix without settling one thing first.**
The payload is a *checked* build mixed with *retail* WASDK components. A chk/retail
mix can produce QI failures on internal interfaces whose IIDs or vtables differ
between flavours. Test a retail build of the same change before concluding
anything about the change itself.

## Gotchas that cost real time

**A Hyper-V enhanced session whose client has detached looks alive but doesn't
render.** `qwinsta` reports the session Active, `explorer` and `dwm` are running,
apps launch - and every pixel read comes back garbage while the harness cheerfully
reports success. This invalidated a whole round of results.

Tells, all cheap to check:

| Symptom | Meaning |
|---|---|
| `GetPixel` returns `0xFFFFFFFF` (CLR_INVALID) | No readable screen DC |
| `GetForegroundWindow()` returns `0` | The thread's desktop isn't the input desktop |
| `qwinsta` SESSIONNAME is a GUID-ish RDP transport | Enhanced session, needs a live client |
| SESSIONNAME is `console` | Fine - Hyper-V's synthetic video always composites it |

Fix, run as SYSTEM via a scheduled task, once at the start of a measurement
session:

```powershell
$a  = New-ScheduledTaskAction -Execute 'C:\Windows\System32\tscon.exe' -Argument '1 /dest:console'
$pr = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
Register-ScheduledTask -TaskName MoveToConsole -Action $a -Principal $pr -Force
Start-ScheduledTask -TaskName MoveToConsole
```

**Always run a stock control before trusting a result.** That's the only reason
the above was caught. Without it the report would have been "the crash is fixed",
from a desktop that was never drawing anything.

**Screen capture is the only truthful surface for a translucent border.**
`PrintWindow` with `PW_RENDERFULLCONTENT` doesn't run DWM frame composition, and
reported a visibly see-through row as opaque alpha 255.

**One reading of a translucent surface proves nothing.** Vary the background and
measure the delta. Both harnesses do this.

**Use `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`, not `GetWindowRect`.**
The latter includes the invisible resize border, which shifts every measurement.

**Report disagreement instead of collapsing it.** Sampling each edge at three
points along its length, so a partly covered edge reads `mixed` rather than
returning whatever the first pixel happened to be, is what caught the
caption-button block.
