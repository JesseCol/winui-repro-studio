// repro:      ECITB reserved top row
// wasdk:      2.3.1
// background: #202020

// Extend-content-into-title-bar reserves a one-pixel row at the top of the client
// area. The bug: that row was left as the HWND background instead of being painted
// by WM_ERASEBKGND, so it shows up as a stray line above the app's own content.
//
// Eyeballing one pixel over a VM console is unreliable, so this repro measures it. It
// opens a window whose client area is one flat colour and reads its top four rows from
// two different surfaces, because they answer two different questions:
//
//   PrintWindow  the window's own surface, underneath DWM. The reserved row is not
//                covered by XAML, so this is the only place its true colour can be
//                read. THIS DRIVES THE VERDICT.
//
//   screen       what the user sees after DWM composites. The reserved row blends with
//                whatever is behind the window there, so it varies run to run. Reported
//                for context only.
//
// Three outcomes, not two, because "painted" and "painted with the right colour" are
// different things:
//
//   row 0 == rows 1-3        PASS. The reserved row matches the client content.
//   row 0 == #000000         FAIL (unpainted). Bare HWND background, the original bug.
//   row 0 == anything else   FAIL (painted, wrong colour). Something erased the row,
//                            but not with the content colour. Compare it against the
//                            window background reported on the same line: if they
//                            match, WM_ERASEBKGND is filling the row with the window
//                            background. For a normal app that is invisible; this
//                            repro uses magenta content on purpose so it is not.
//
// A second, identical window opens alongside it WITHOUT extend-into-title-bar, as a
// control. It has no reserved row, so its row 0 must already match its content. If it
// does not, row 0 differs for some structural reason on this OS and no verdict is
// given. Without that control the test reports FAIL on a correct build, which is worse
// than no answer.
//
// Each row is sampled at several columns well away from the rounded corners and the
// resize border. A row only gets a colour if every column agrees, otherwise it reads
// as "mixed" rather than silently trusting one pixel.
//
// Do not try to expose the erase surface by leaving a transparent strip below the
// reserved row. It was tried. A transparent XAML region composites to black in
// PrintWindow whether or not the window was erased, so it reads identically on a fixed
// and an unfixed build and turns the verdict upside down.
//
// The verdict shows in the preview window and is also appended to
// %TEMP%\winui-repro-app\ecitb-verdict.txt, so it can be copied off a test machine.

using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using WinRT.Interop;

class Repro
{
    const string Xaml = """
        <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Padding="24" Spacing="14">
            <TextBlock Text="ECITB reserved top row" FontSize="26" />
            <TextBlock TextWrapping="Wrap" Opacity="0.8"
                       Text="Two windows open with a flat magenta client area: one with the title bar extended into it, and one without as a control. The top row of pixels of each is read back off the screen." />
            <Border x:Name="Verdict" Padding="14" CornerRadius="6" Background="#333333">
                <TextBlock x:Name="Result" FontSize="18" Text="measuring..." TextWrapping="Wrap" />
            </Border>
            <TextBlock x:Name="Detail" FontFamily="Consolas" FontSize="13" Opacity="0.8" TextWrapping="Wrap" />
        </StackPanel>
        """;

    // The flat colour the whole client area is painted with. Nothing in the default
    // theme is near it, so anything else showing through is unpainted background.
    const byte FillR = 255, FillG = 0, FillB = 255;

    const string TestWindowTitle = "ECITB top row test";

    // The control. Identical window, identical fill, but WITHOUT extend-into-title-bar.
    // Its top client row must always be the fill colour. If it is not, then row 0 is
    // picking up a window frame or border artifact rather than the reserved ECITB row,
    // and a plain "row 0 differs" test would report FAIL on a correct build.
    const string ControlWindowTitle = "ECITB control (no extend)";

    // Sample well inside the window horizontally, so rounded corners, the resize border
    // and any drop shadow cannot influence the reading. Several columns, because a row
    // that is genuinely a flat surface reads the same at all of them; if they disagree,
    // something is overlapping and the reading is not trustworthy.
    static readonly int[] SampleColumns = { 80, 220, 400 };

    // Rows to read, top down. Row 0 is the reserved ECITB row; rows 1-3 are ordinary
    // client rows and read back as the XAML fill.
    static readonly int[] SampleRows = { 0, 1, 2, 3 };
    const int ReservedRow = 0;
    static readonly int[] ContentRows = { 1, 2, 3 };

    // Row read back as more than one colour across SampleColumns.
    const uint Mixed = 0xFEFEFEFE;

    const uint BadPixel = 0xFFFFFFFF;

    // Pre-fill colour for the PrintWindow capture buffer: opaque green, in DIB byte
    // order (B,G,R,A). Chosen because this repro never draws green, so a pixel that
    // still reads green afterwards is one PrintWindow simply did not touch.
    const uint Sentinel = 0xFF00FF00;

    // The same value as a COLORREF, which is the form every comparison below uses.
    const uint SentinelColour = 0x0000FF00;

    static readonly IntPtr HwndTopmost = new IntPtr(-1);

    // The on-screen verdict is fine locally, but this repro is meant to be run on a
    // VM over a remote session, where reading a panel and typing the result back is
    // both fiddly and error-prone. Drop the same text on disk so it can be copied.
    static readonly string VerdictPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "winui-repro-app", "ecitb-verdict.txt");

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader info,
        uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
        ref int value, int size);

    // A 32bpp top-down DIB, so row 0 is the first row in memory and each pixel is a
    // plain BGRA uint. CreateCompatibleBitmap plus GetPixel cannot do this job: GetPixel
    // returns a COLORREF, which has no alpha, and the new fix paints the reserved row
    // black *with alpha 0*. Read as RGB that is indistinguishable from never painting
    // it at all, which is exactly the wrong answer.
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
                                            int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformationW(IntPtr obj, int index,
        System.Text.StringBuilder info, int length, out int lengthNeeded);

    /// <summary>
    /// The name of the desktop currently receiving input. "Default" is the normal
    /// interactive desktop. When the machine is locked, or a UAC prompt is up, it is
    /// "Winlogon" instead, nothing composites to the visible screen, and reading
    /// pixels gives the lock screen rather than the window under test.
    /// </summary>
    static string InputDesktopName()
    {
        const uint DesktopReadObjects = 0x0001;
        const int UoiName = 2;

        IntPtr desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == IntPtr.Zero) { return "unavailable"; }

        try
        {
            var name = new System.Text.StringBuilder(256);
            return GetUserObjectInformationW(desktop, UoiName, name, name.Capacity, out _)
                ? name.ToString()
                : "unknown";
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    static void Setup(FrameworkElement root, Window window)
    {
        // Every save re-runs Setup in a fresh assembly, so a static field would not
        // survive. Find the previous test window through the OS instead. Bounded,
        // because a window that ignores WM_CLOSE would otherwise spin here forever.
        const uint WmClose = 0x0010;
        foreach (string title in new[] { TestWindowTitle, ControlWindowTitle })
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                IntPtr stale = FindWindowW(null, title);
                if (stale == IntPtr.Zero) { break; }
                if (!PostMessageW(stale, WmClose, IntPtr.Zero, IntPtr.Zero)) { break; }
                System.Threading.Thread.Sleep(150);
            }
        }

        var test = MakeWindow(TestWindowTitle, extend: true, x: 120);
        var control = MakeWindow(ControlWindowTitle, extend: false, x: 720);

        // Sampling has to wait for the windows to actually be composited on screen.
        DispatcherQueueTimer timer = window.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(1200);
        timer.IsRepeating = false;
        timer.Tick += (s, e) => Measure(root, test, control);
        timer.Start();
    }

    static Window MakeWindow(string title, bool extend, int x)
    {
        // The whole client area is one flat colour. A margin was tried, to expose the
        // erase surface below the reserved row as a reference, and it does not work: a
        // transparent XAML region composites to black in PrintWindow whether the window
        // was erased or not, so it reads the same on a fixed and an unfixed build.
        var w = new Window
        {
            Title = title,
            ExtendsContentIntoTitleBar = extend,
            Content = new Grid
            {
                Background = new SolidColorBrush(ColorHelper.FromArgb(255, FillR, FillG, FillB)),
            },
        };

        w.AppWindow.MoveAndResize(new RectInt32(x, 120, 560, 320));
        w.Activate();

        // GetPixel reads the screen, so anything covering the window would be measured
        // instead of it, and the answer would be wrong with no sign that it was wrong.
        // Pin it above everything, including the preview window that spawned it.
        IntPtr hwnd = WindowNative.GetWindowHandle(w);
        const uint SwpNoMove = 0x0002, SwpNoSize = 0x0001, SwpNoActivate = 0x0010;
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

        // Square off the corners. On Win11 the rounded corner and its antialiasing sit
        // right on top of the reserved row, so the row that matters is partly hidden by
        // DWM before anything gets measured. Win10 has no rounded corners at all, so
        // forcing square here makes this machine behave more like the one the bug was
        // reported on. Fails silently on Win10, where the attribute does not exist.
        const int DwmwaWindowCornerPreference = 33;
        const int DwmwcpDoNotRound = 1;
        int corner = DwmwcpDoNotRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

        return w;
    }

    static void Measure(FrameworkElement root, Window test, Window control)
    {
        var result = root.FindName("Result") as TextBlock;
        var detail = root.FindName("Detail") as TextBlock;
        var verdict = root.FindName("Verdict") as Border;

        IntPtr testHwnd = WindowNative.GetWindowHandle(test);
        IntPtr ctrlHwnd = WindowNative.GetWindowHandle(control);

        // Two different surfaces, and they answer two different questions.
        //
        //   screen      - what the user sees, after DWM has composited everything.
        //   PrintWindow - the window's own erase surface, underneath DWM. This is the
        //                 one WM_ERASEBKGND paints, so it is the surface the fix
        //                 changes. XAML content does not appear here, because WinUI 3
        //                 composes through DirectComposition, and that is fine: the
        //                 test is whether the reserved row matches the rows below it.
        var testScreen = SampleFromScreen(testHwnd);
        var ctrlScreen = SampleFromScreen(ctrlHwnd);
        var testPrint = SampleFromPrintWindow(testHwnd, out var testAlpha);
        var ctrlPrint = SampleFromPrintWindow(ctrlHwnd, out _);

        CaptureThemeBackground();

        Diagnostics =
            $"screen  test={Describe(testScreen)}  control={Describe(ctrlScreen)}"
            + $" | printwindow  test={Describe(testPrint)}  control={Describe(ctrlPrint)}"
            + $" | alpha  test={DescribeAlpha(testAlpha)}";

        // PrintWindow is the surface the fix actually changes, so prefer it. But it is not
        // always available: on Win10 1809 it fails outright on the ECITB window while
        // succeeding on the control. Falling back to the screen keeps the repro useful
        // there instead of reporting "inconclusive" while holding a perfectly good read.
        uint[]? testSurface = testPrint;
        uint[]? ctrlSurface = ctrlPrint;
        string surfaceName = "erase surface (PrintWindow)";

        if (testSurface is null)
        {
            testSurface = testScreen;
            ctrlSurface = ctrlScreen;
            surfaceName = "screen (PrintWindow unavailable on this OS)";
            testAlpha = null;
        }

        if (testSurface is null)
        {
            Report(result, verdict, detail,
                "inconclusive: neither PrintWindow nor the screen could be read. "
                + "The window is probably covered, or the desktop is locked.",
                false, null);
            return;
        }

        // The content rows prove the window actually rendered. Without that check, an
        // all-black read from a window that never painted looks exactly like a window
        // whose reserved row was erased correctly.
        uint expectedFill = (uint)(FillR | (FillG << 8) | (FillB << 16));
        uint? content = ContentColour(testSurface);
        if (content is null || content.Value != expectedFill)
        {
            Report(result, verdict, detail,
                $"inconclusive: the test window did not render its fill "
                + $"(rows 1-3 read {Describe(testSurface)}, expected {Hex(expectedFill)}). "
                + "Nothing can be concluded from the reserved row.",
                false, testSurface);
            return;
        }

        // The control has no reserved row, so its row 0 must already match its content.
        // If it does not, row 0 differs for some structural reason on this OS and the
        // test window cannot be judged on it either.
        if (ctrlSurface is not null)
        {
            uint? ctrlContent = ContentColour(ctrlSurface);
            if (ctrlContent is null || ctrlSurface[ReservedRow] != ctrlContent.Value)
            {
                Report(result, verdict, detail,
                    $"inconclusive: the control window's top row does not match its own "
                    + $"content either ({Describe(ctrlSurface)}), so row 0 differs for a "
                    + "reason other than the reserved row. No verdict on this OS.",
                    false, testSurface);
                return;
            }
        }

        uint reserved = testSurface[ReservedRow];
        bool pass = reserved == content.Value;
        uint reservedAlpha = testAlpha is not null && ReservedRow < testAlpha.Length
            ? testAlpha[ReservedRow]
            : BadPixel;

        // Four outcomes, not two. "Painted, but not with the content colour" is a real
        // and distinct state: it means WM_ERASEBKGND now covers the reserved row, but
        // with the window background rather than whatever the app drew. And a row that
        // is black *because a fix deliberately painted it transparent* is different
        // again from one that was never touched. Collapsing any of these into a bare
        // FAIL hides the exact thing this repro exists to measure.
        string headline;
        if (pass)
        {
            headline = $"PASS - the reserved top row matches the client content ({Hex(reserved)})";
        }
        else if (reserved == SentinelColour)
        {
            headline = "FAIL (untouched) - PrintWindow never wrote the reserved top row at all; "
                + "it still holds the capture buffer's fill colour. Nothing erased it.";
        }
        else if (reserved == 0 && reservedAlpha == 0)
        {
            headline = "AMBIGUOUS (transparent black) - the reserved top row was written, but as "
                + "black with alpha 0. That is what a fix painting the row transparent looks "
                + $"like, and also what a cleared surface looks like. Content below is "
                + $"{Hex(content.Value)}. Judge this one on screen pixels, not here.";
        }
        else if (reserved == 0)
        {
            headline = $"FAIL (unpainted) - the reserved top row is pure black, the bare HWND "
                + $"background. Content below it is {Hex(content.Value)}.";
        }
        else
        {
            headline = $"FAIL (painted, wrong colour) - the reserved top row is {Hex(reserved)}, so "
                + $"something erased it, but the content below it is {Hex(content.Value)}. "
                + $"Window background is {ThemeBackground}.";
        }

        string screenNote = testScreen is null
            ? "  screen: not readable (locked or covered)"
            : $"  screen: {Describe(testScreen)}";

        Report(result, verdict, detail,
            headline + $"   judged on: {surfaceName} = {Describe(testSurface)}"
            + $"  alpha: {DescribeAlpha(testAlpha)}" + screenNote,
            pass, testSurface);
    }

    static string DescribeAlpha(uint[]? rows)
    {
        if (rows is null) { return "unavailable"; }
        var parts = new string[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            parts[i] = rows[i] == Mixed ? "mixed" : rows[i].ToString();
        }
        return string.Join("/", parts);
    }

    /// <summary>
    /// The colour of the ordinary client rows below the reserved row, or null if they do
    /// not agree with each other.
    /// </summary>
    static uint? ContentColour(uint[] rows)
    {
        uint first = rows[ContentRows[0]];
        if (first == Mixed) { return null; }
        foreach (int i in ContentRows)
        {
            if (rows[i] != first) { return null; }
        }
        return first;
    }

    /// <summary>Reads the top rows off the visible screen, or null on failure.</summary>
    static uint[]? SampleFromScreen(IntPtr hwnd)
    {
        var origin = new Point { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref origin)) { return null; }

        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) { return null; }

        try
        {
            return ReadRows((x, y) => GetPixel(screen, origin.X + x, origin.Y + y));
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// Reads the same rows from the window's own erase surface. PW_RENDERFULLCONTENT
    /// asks DWM for the window's backing surface rather than repainting into the DC.
    /// </summary>
    /// <summary>
    /// Captures the window's own erase surface and reads both colour and alpha.
    /// <para>
    /// Uses a 32bpp top-down DIB rather than a compatible bitmap so the alpha channel
    /// survives. That matters: a fix that paints the reserved row black with alpha 0
    /// produces the same RGB as a row that was never painted, and only the alpha tells
    /// the two apart.
    /// </para>
    /// </summary>
    static uint[]? SampleFromPrintWindow(IntPtr hwnd, out uint[]? alphaRows)
    {
        alphaRows = null;
        if (!GetClientRect(hwnd, out Rect client)) { return null; }

        int width = client.Right - client.Left;
        int height = client.Bottom - client.Top;
        if (width <= SampleColumns[SampleColumns.Length - 1]
            || height <= SampleRows[SampleRows.Length - 1]) { return null; }

        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) { return null; }

        IntPtr memDc = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            memDc = CreateCompatibleDC(screen);
            if (memDc == IntPtr.Zero) { return null; }

            var header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                // Negative height means top-down, so y maps straight to a row index.
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0, // BI_RGB
            };

            const uint DibRgbColors = 0;
            bmp = CreateDIBSection(memDc, ref header, DibRgbColors, out IntPtr bits, IntPtr.Zero, 0);
            if (bmp == IntPtr.Zero || bits == IntPtr.Zero) { return null; }

            old = SelectObject(memDc, bmp);

            // Pre-fill with a colour the test never uses. Without this, "PrintWindow
            // never touched this pixel" and "the fix painted it black with alpha 0" both
            // read as 0x00000000, because a fresh DIB is already zeroed. With it, an
            // untouched pixel is unmistakable and a deliberate transparent-black paint
            // is finally visible as a distinct third state.
            int pixelCount = width * height;
            for (int i = 0; i < pixelCount; i++)
            {
                Marshal.WriteInt32(bits, i * 4, unchecked((int)Sentinel));
            }

            const uint PwClientOnly = 0x00000001;
            const uint PwRenderFullContent = 0x00000002;
            if (!PrintWindow(hwnd, memDc, PwClientOnly | PwRenderFullContent)) { return null; }

            // Stride is width * 4: a 32bpp DIB is always DWORD aligned already.
            uint Pixel(int x, int y)
            {
                if (x >= width || y >= height) { return BadPixel; }
                return unchecked((uint)Marshal.ReadInt32(bits, ((y * width) + x) * 4));
            }

            // Memory order is B,G,R,A, so a little-endian uint reads 0xAARRGGBB. Everything
            // else in this file works in COLORREF order (0x00BBGGRR, what GetPixel returns
            // and what Hex formats), so swap R and B on the way out rather than making the
            // rest of the file care where the pixel came from.
            alphaRows = ReadRows((x, y) =>
            {
                uint p = Pixel(x, y);
                return p == BadPixel ? BadPixel : (p >> 24) & 0xFF;
            });

            return ReadRows((x, y) =>
            {
                uint p = Pixel(x, y);
                if (p == BadPixel) { return BadPixel; }
                uint r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;
                return (b << 16) | (g << 8) | r;
            });
        }
        finally
        {
            if (old != IntPtr.Zero) { SelectObject(memDc, old); }
            if (bmp != IntPtr.Zero) { DeleteObject(bmp); }
            if (memDc != IntPtr.Zero) { DeleteDC(memDc); }
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// Reads each row at every sample column. A row only gets a colour if all the
    /// columns agree, so a partly covered or non-flat row is reported as Mixed rather
    /// than silently taking the first pixel it happened to land on.
    /// </summary>
    static uint[]? ReadRows(Func<int, int, uint> read)
    {
        var rows = new uint[SampleRows.Length];
        for (int i = 0; i < SampleRows.Length; i++)
        {
            int y = SampleRows[i];
            uint first = read(SampleColumns[0], y);
            if (first == BadPixel) { return null; }

            rows[i] = first;
            for (int c = 1; c < SampleColumns.Length; c++)
            {
                uint next = read(SampleColumns[c], y);
                if (next == BadPixel) { return null; }
                if (next != first) { rows[i] = Mixed; break; }
            }
        }
        return rows;
    }

    static void Report(TextBlock? result, Border? verdict, TextBlock? detail, string message, bool pass, uint[]? rows)
    {
        Log(message);
        if (result is not null) { result.Text = message; }
        if (verdict is not null)
        {
            verdict.Background = new SolidColorBrush(pass
                ? ColorHelper.FromArgb(255, 0x1B, 0x5E, 0x20)
                : ColorHelper.FromArgb(255, 0x7F, 0x1D, 0x1D));
        }

        var text = new System.Text.StringBuilder();
        if (rows is not null)
        {
            for (int i = 0; i < rows.Length && i < SampleRows.Length; i++)
            {
                text.Append($"row {SampleRows[i]}: {Hex(rows[i])}   ");
            }
        }

        string rowDetail = text.ToString().TrimEnd();
        if (detail is not null) { detail.Text = rowDetail; }

        WriteVerdictFile(message, rowDetail);
    }

    static string Diagnostics = "not measured";

    // The window background the theme resolves to. Reported so a reserved row that is
    // painted, but not with the content colour, can be checked against it: if they match,
    // WM_ERASEBKGND is filling the row with the window background, which is what a real
    // app would usually want and what this repro's magenta fill deliberately is not.
    static string ThemeBackground = "unknown";

    static void CaptureThemeBackground()
    {
        try
        {
            if (Application.Current?.Resources["ApplicationPageBackgroundThemeBrush"] is SolidColorBrush b)
            {
                ThemeBackground = $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}";
            }
        }
        catch (Exception)
        {
            // Diagnostic only. A missing theme key must not stop the measurement.
        }
    }

    // Renders one sample set as "#RRGGBB/#RRGGBB/..." so both readings fit on one line.
    static string Describe(uint[]? rows)
    {
        if (rows is null) { return "failed"; }
        var parts = new string[rows.Length];
        for (int y = 0; y < rows.Length; y++)
        {
            parts[y] = Hex(rows[y]);
        }
        return string.Join("/", parts);
    }

    static string Hex(uint bgr) => bgr == Mixed
        ? "mixed"
        : $"#{bgr & 0xFF:X2}{(bgr >> 8) & 0xFF:X2}{(bgr >> 16) & 0xFF:X2}";

    static void WriteVerdictFile(string message, string rowDetail)
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(VerdictPath);
            if (dir is not null) { System.IO.Directory.CreateDirectory(dir); }

            var lines = new[]
            {
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  ECITB reserved top row",
                $"  OS       {Environment.OSVersion.Version}",
                $"  desktop  {InputDesktopName()}",
                $"  rows     {string.Join("/", SampleRows)}  (0 reserved, "
                    + $"{string.Join("/", Array.ConvertAll(ContentRows, i => SampleRows[i]))} content "
                    + $"#{FillR:X2}{FillG:X2}{FillB:X2}, window background {ThemeBackground})",
                $"  reads    {Diagnostics}",
                $"  {rowDetail}",
                $"  {message}",
                string.Empty,
            };
            System.IO.File.AppendAllLines(VerdictPath, lines);
            Log("verdict written to " + VerdictPath);
        }
        catch (Exception ex)
        {
            // A missing verdict file must never take the repro down with it.
            Log("could not write the verdict file: " + ex.Message);
        }
    }
}
