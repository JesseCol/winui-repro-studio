// repro:      ECITB top edge vs native window border
// wasdk:      2.3.1
// background: #202020

// Companion to ecitb-top-row.cs, and it asks a DIFFERENT question. Read this before
// comparing their verdicts, because they can disagree and both be right.
//
//   ecitb-top-row.cs   does the reserved row match the app's own content?
//   this file          does the reserved row match the window's own native border?
//
// The first question was the wrong one. A fix that hands the reserved row to DWM makes
// it render as frame, not as content, so it will never equal the content colour - and
// that is fine, because the three other sides of every window are frame too. What
// actually matters is whether the top edge is consistent with the left, right and bottom
// edges of the same window. If it is, the window has an even border all the way round
// and looks correct. If it is not, there is a visible odd line along the top.
//
// So this repro does not compare the top row to the content. It compares the top row to
// the window's own other three borders, on the same window, at the same moment.
//
// Two things make that measurement trustworthy:
//
//   Controlled background. A translucent border takes its colour from whatever is behind
//   the window, so a single reading proves nothing. This repro paints its own backdrop
//   window a known colour, measures, repaints it a very different colour, and measures
//   again. A native border shifts by a specific amount between the two. The top edge has
//   to shift by the same amount to be the same material.
//
//   Active and inactive. DWM draws the border differently depending on focus, so a top
//   edge that matches while focused can still be wrong when it is not. Each window is
//   measured in both states.
//
// Three windows open, because there are two separate ways to turn the feature on and
// issue #8948 compares them to each other:
//
//   Window.ExtendsContentIntoTitleBar = true              the Window-level API
//   AppWindow.TitleBar.ExtendsContentIntoTitleBar = true  the AppWindow-level API
//   neither                                               control, a stock window
//
// The control is what "right" looks like. Its top edge is a title bar rather than a
// border, so it is not compared edge-to-edge with the others; it is there to show what
// the native left, right and bottom borders read on this OS, in this theme, at this
// moment. If the two ECITB windows disagree with each other, that is a bug on its own,
// and it is exactly what #8948 describes.
//
// Everything is read off the screen with GetPixel, deliberately. PrintWindow does not run
// DWM frame composition, so it cannot see a translucent border at all - it reported an
// opaque colour for a row that is visibly see-through. For this question the composited
// screen is the only surface that tells the truth.
//
// Each edge is read as a four-pixel profile going inward, sampled at three points along
// the edge. A depth of four shows how thick the border is instead of assuming one pixel,
// and three points along it mean a partly covered edge reads as "mixed" rather than
// quietly returning whatever the first pixel happened to be.
//
// The verdict, and the full table behind it, is appended to
// %TEMP%\winui-repro-app\ecitb-border.txt so it can be copied off a test machine.

using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using WinRT.Interop;

class Repro
{
    const string Xaml = """
        <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Padding="24" Spacing="14">
            <TextBlock Text="ECITB top edge vs native border" FontSize="26" />
            <TextBlock TextWrapping="Wrap" Opacity="0.8"
                       Text="Measures whether the reserved top row of an extend-into-title-bar window matches that window's own left, right and bottom borders, over two known backdrop colours and in both the active and inactive states." />
            <TextBlock x:Name="Result" FontSize="18" Text="measuring..." TextWrapping="Wrap" />
            <TextBlock x:Name="Detail" FontFamily="Consolas" FontSize="12" Opacity="0.85" />
        </StackPanel>
        """;

    // The client fill. Nothing in either theme is near it, so any edge pixel that reads
    // as this is content rather than border.
    const byte FillR = 255, FillG = 0, FillB = 255;

    const string WindowEcitbTitle = "ECITB via Window";
    const string AppWindowEcitbTitle = "ECITB via AppWindow";
    const string ControlTitle = "ECITB control (none)";
    const string BackdropTitle = "ECITB backdrop";

    // Two backdrop colours chosen to be far apart in all three channels, so a translucent
    // border shifts by a large and unambiguous amount between them. A border that does not
    // move at all between these two is opaque.
    static readonly (string Name, byte R, byte G, byte B)[] Backdrops =
    {
        ("#01204D", 0x01, 0x20, 0x4D),
        ("#E07A00", 0xE0, 0x7A, 0x00),
    };

    // How far inward from each edge to read. Four is enough to show a one or two pixel
    // border sitting on top of content, without reaching anything else.
    const int EdgeDepth = 4;

    // Where along each edge to read, as fractions of its length. Kept away from the
    // corners, where two borders meet and neither reading is clean.
    static readonly double[] AlongEdge = { 0.30, 0.50, 0.70 };

    // Edge read back as more than one colour across AlongEdge.
    const uint Mixed = 0xFEFEFEFE;

    const uint BadPixel = 0xFFFFFFFF;

    // How far down to hunt for the app's own content when starting at the top edge.
    // A four pixel profile cannot tell a one pixel band from a whole untouched title
    // bar, and that is exactly the distinction issue 8948 turns on, so scan deeper.
    const int ContentSearchDepth = 64;

    // Two colours count as the same border if every channel is within this. DWM's own
    // border is not always bit-identical along its length, and demanding an exact match
    // would report a difference no one can see.
    const int ChannelTolerance = 6;

    static readonly IntPtr HwndTopmost = new IntPtr(-1);

    // Which subjects to build, as letters: w = Window API, a = AppWindow API, c = control.
    // Being able to build a subset is how you find out which subject is responsible when
    // a runtime under test misbehaves. Against the DWM-glass prototype the answer turned
    // out to be "none of them": each letter on its own crashed the same way, including
    // the plain control window with no ECITB at all. See README.md in this folder.
    const string BuildSubjects = "wac";

    static readonly string VerdictPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "winui-repro-app", "ecitb-border.txt");

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
                                            int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    // The visible frame, excluding the invisible resize border that GetWindowRect
    // includes. Without this the "border" reads as whatever is behind the window,
    // because the outermost pixels of GetWindowRect are not drawn at all.
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute,
        out Rect value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformationW(IntPtr obj, int index,
        System.Text.StringBuilder info, int length, out int lengthNeeded);

    /// <summary>
    /// The desktop currently receiving input. "Default" is the normal interactive
    /// desktop. Anything else means nothing is compositing to the visible screen and
    /// every pixel read below is meaningless.
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

    /// <summary>One window's four edges, under one backdrop, in one focus state.</summary>
    sealed class Reading
    {
        public string Window = string.Empty;
        public string Backdrop = string.Empty;
        public bool Active;
        public uint[]? Top;
        public uint[]? Left;
        public uint[]? Right;
        public uint[]? Bottom;

        // Every individual top-edge sample, depth-major. A top edge that reads Mixed is
        // the interesting case, so keep the pixels that disagreed instead of discarding them.
        public uint[]? TopRaw;

        // How many pixels below the top edge the app's own content first appears, measured
        // at each point along the edge. -1 means it was never found within the search depth.
        public int[]? ContentDepth;
    }

    sealed class Subject
    {
        public string Title = string.Empty;
        public Window? Window;
        public IntPtr Hwnd;
        public bool IsEcitb;
        public string Note = string.Empty;
    }

    static readonly List<Reading> Readings = new();
    static readonly List<Subject> Subjects = new();
    static Window? Backdrop;
    static Grid? BackdropFill;

    /// <summary>
    /// Appends one line and flushes to disk. The glass prototype crashes asynchronously
    /// during paint, where a try/catch cannot see it, so the last line written names the
    /// step that died.
    /// </summary>
    static void Trail(string message)
    {
        try
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "winui-repro-app", "ecitb-border-trace.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            using var stream = new System.IO.FileStream(
                path, System.IO.FileMode.Append, System.IO.FileAccess.Write,
                System.IO.FileShare.ReadWrite);
            using var writer = new System.IO.StreamWriter(stream);
            writer.WriteLine($"{System.DateTime.Now:HH:mm:ss.fff}  {message}");
            writer.Flush();
            stream.Flush(true);
        }
        catch
        {
            // Never let the instrument take down the thing it is measuring.
        }
    }

    static void Setup(FrameworkElement root, Window window)
    {
        Trail("");
        Trail($"=== setup, OS {System.Environment.OSVersion.Version} ===");

        // Every save recompiles into a fresh assembly, so nothing static survives from
        // the previous run. Find the old windows through the OS and close them, or each
        // save leaves another set behind and they cover each other.
        const uint WmClose = 0x0010;
        foreach (string title in new[]
                 { WindowEcitbTitle, AppWindowEcitbTitle, ControlTitle, BackdropTitle })
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                IntPtr stale = FindWindowW(null, title);
                if (stale == IntPtr.Zero) { break; }
                if (!PostMessageW(stale, WmClose, IntPtr.Zero, IntPtr.Zero)) { break; }
                System.Threading.Thread.Sleep(150);
            }
        }
        Trail("stale windows cleared");

        Readings.Clear();
        Subjects.Clear();

        RunAsync(root);
    }

    static async void RunAsync(FrameworkElement root)
    {
        try
        {
            Trail("BuildWindows");
            BuildWindows();
            Trail("BuildWindows returned");

            // Let the whole set composite before reading anything off the screen.
            await Task.Delay(1200);

            foreach (var backdrop in Backdrops)
            {
                Trail($"backdrop -> {backdrop.Name}");
                if (BackdropFill is not null)
                {
                    BackdropFill.Background = new SolidColorBrush(
                        ColorHelper.FromArgb(255, backdrop.R, backdrop.G, backdrop.B));
                }

                // Repainting the backdrop is a composition change, so give DWM time to
                // put it on screen before sampling anything in front of it.
                await Task.Delay(700);

                // Activating each window in turn gives one active reading for it and an
                // inactive reading for the other two, so both focus states get covered
                // without measuring the same window twice in the same state.
                foreach (var focused in Subjects)
                {
                    Trail($"focus -> {focused.Title}");
                    SetForegroundWindow(focused.Hwnd);
                    focused.Window?.Activate();
                    await Task.Delay(500);

                    IntPtr foreground = GetForegroundWindow();
                    foreach (var subject in Subjects)
                    {
                        Trail($"  read {subject.Title}");
                        Readings.Add(Read(subject, backdrop.Name, subject.Hwnd == foreground));
                    }
                }
            }

            Trail("Judge");
            Judge(root);
            Trail("Judge returned");
        }
        catch (Exception ex)
        {
            Trail($"CAUGHT {ex.GetType().Name}: {ex.Message}");
            Report(root, "inconclusive: the measurement threw - " + ex.Message, false);
        }
    }

    static void BuildWindows()
    {
        // The backdrop sits behind everything and is the controlled variable. A
        // translucent border picks up its colour; an opaque one ignores it.
        Backdrop = new Window { Title = BackdropTitle };
        BackdropFill = new Grid
        {
            Background = new SolidColorBrush(
                ColorHelper.FromArgb(255, Backdrops[0].R, Backdrops[0].G, Backdrops[0].B)),
        };
        Backdrop.Content = BackdropFill;
        Trail("  backdrop content set");
        Backdrop.AppWindow.MoveAndResize(new RectInt32(40, 80, 1250, 440));
        Backdrop.Activate();
        Trail("  backdrop activated");

        // Topmost first, so the three test windows raised after it land above it. They
        // all have to be topmost or the preview window covers them and the reading
        // measures the wrong thing with no sign that it did.
        IntPtr backdropHwnd = WindowNative.GetWindowHandle(Backdrop);
        const uint SwpNoMove = 0x0002, SwpNoSize = 0x0001, SwpNoActivate = 0x0010;
        SetWindowPos(backdropHwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

        if (BuildSubjects.Contains('w'))
        {
            Trail("  make WindowApi subject");
            Subjects.Add(MakeWindow(WindowEcitbTitle, EcitbMode.WindowApi, x: 90));
        }

        if (BuildSubjects.Contains('a'))
        {
            Trail("  make AppWindowApi subject");
            Subjects.Add(MakeWindow(AppWindowEcitbTitle, EcitbMode.AppWindowApi, x: 490));
        }

        if (BuildSubjects.Contains('c'))
        {
            Trail("  make control subject");
            Subjects.Add(MakeWindow(ControlTitle, EcitbMode.None, x: 890));
        }

        Trail("  subjects built");
    }

    enum EcitbMode { None, WindowApi, AppWindowApi }

    static Subject MakeWindow(string title, EcitbMode mode, int x)
    {
        var w = new Window
        {
            Title = title,
            Content = new Grid
            {
                Background = new SolidColorBrush(ColorHelper.FromArgb(255, FillR, FillG, FillB)),
            },
        };

        string note = "off";

        // The two entry points are set through different objects and are not guaranteed
        // to behave the same, which is the whole point of issue #8948. Set exactly one
        // of them per window, and record what actually took effect rather than what was
        // asked for - the AppWindow path is not supported everywhere and can silently
        // do nothing, which would otherwise look like a rendering bug.
        if (mode == EcitbMode.WindowApi)
        {
            w.ExtendsContentIntoTitleBar = true;
            note = w.ExtendsContentIntoTitleBar ? "Window API, applied" : "Window API, DID NOT APPLY";
        }
        else if (mode == EcitbMode.AppWindowApi)
        {
            try
            {
                bool supported = AppWindowTitleBar.IsCustomizationSupported();
                w.AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                bool applied = w.AppWindow.TitleBar.ExtendsContentIntoTitleBar;
                note = $"AppWindow API, customization supported={supported}, "
                    + (applied ? "applied" : "DID NOT APPLY");
            }
            catch (Exception ex)
            {
                note = "AppWindow API, THREW: " + ex.GetType().Name;
            }
        }

        w.AppWindow.MoveAndResize(new RectInt32(x, 130, 360, 320));
        w.Activate();

        IntPtr hwnd = WindowNative.GetWindowHandle(w);
        const int GwlExStyle = -20;
        const long WsExNoRedirectionBitmap = 0x00200000;
        long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        note += $", exStyle=0x{exStyle:X8}, noRedirection={((exStyle & WsExNoRedirectionBitmap) != 0)}";

        const uint SwpNoMove = 0x0002, SwpNoSize = 0x0001, SwpNoActivate = 0x0010;
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

        return new Subject
        {
            Title = title,
            Window = w,
            Hwnd = hwnd,
            IsEcitb = mode != EcitbMode.None,
            Note = note,
        };
    }

    static Reading Read(Subject subject, string backdropName, bool active)
    {
        var reading = new Reading
        {
            Window = subject.Title,
            Backdrop = backdropName,
            Active = active,
        };

        if (DwmGetWindowAttribute(subject.Hwnd, DwmwaExtendedFrameBounds,
                out Rect frame, Marshal.SizeOf<Rect>()) != 0)
        {
            return reading;
        }

        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) { return reading; }

        try
        {
            reading.Top = Profile(screen, frame, Side.Top, out uint[]? topRaw);
            reading.TopRaw = topRaw;
            reading.Left = Profile(screen, frame, Side.Left, out _);
            reading.Right = Profile(screen, frame, Side.Right, out _);
            reading.Bottom = Profile(screen, frame, Side.Bottom, out _);
            reading.ContentDepth = ContentDepths(screen, frame);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }

        return reading;
    }

    const int DwmwaExtendedFrameBounds = 9;

    /// <summary>
    /// Walks straight down from the top edge at each point along it and reports how many
    /// pixels it took to reach the app's own fill colour. Zero means content is drawn right
    /// at the top edge, which is what extending into the title bar is supposed to achieve.
    /// A larger number is the exact thickness of whatever is covering it.
    /// </summary>
    static int[]? ContentDepths(IntPtr screen, Rect frame)
    {
        int width = frame.Right - frame.Left;
        int height = frame.Bottom - frame.Top;
        if (width <= 0 || height <= ContentSearchDepth) { return null; }

        uint fill = (uint)(FillR | (FillG << 8) | (FillB << 16));
        var depths = new int[AlongEdge.Length];
        for (int i = 0; i < AlongEdge.Length; i++)
        {
            int x = frame.Left + (int)(width * AlongEdge[i]);
            depths[i] = -1;
            for (int depth = 0; depth < ContentSearchDepth; depth++)
            {
                if (GetPixel(screen, x, frame.Top + depth) == fill) { depths[i] = depth; break; }
            }
        }
        return depths;
    }

    enum Side { Top, Left, Right, Bottom }

    /// <summary>
    /// Reads one edge as a profile going inward from it. Each depth is sampled at several
    /// points along the edge and only gets a colour if they all agree, so an edge that is
    /// partly covered reads as Mixed instead of returning one arbitrary pixel.
    /// </summary>
    static uint[]? Profile(IntPtr screen, Rect frame, Side side, out uint[]? raw)
    {
        raw = null;
        int width = frame.Right - frame.Left;
        int height = frame.Bottom - frame.Top;
        if (width <= EdgeDepth * 2 || height <= EdgeDepth * 2) { return null; }

        var profile = new uint[EdgeDepth];
        var samples = new uint[EdgeDepth * AlongEdge.Length];
        for (int depth = 0; depth < EdgeDepth; depth++)
        {
            uint agreed = BadPixel;
            for (int i = 0; i < AlongEdge.Length; i++)
            {
                int alongX = frame.Left + (int)(width * AlongEdge[i]);
                int alongY = frame.Top + (int)(height * AlongEdge[i]);

                (int x, int y) = side switch
                {
                    Side.Top => (alongX, frame.Top + depth),
                    Side.Bottom => (alongX, frame.Bottom - 1 - depth),
                    Side.Left => (frame.Left + depth, alongY),
                    _ => (frame.Right - 1 - depth, alongY),
                };

                uint pixel = GetPixel(screen, x, y);
                if (pixel == BadPixel) { return null; }
                samples[(depth * AlongEdge.Length) + i] = pixel;

                if (i == 0) { agreed = pixel; }
                else if (pixel != agreed) { agreed = Mixed; }
            }
            profile[depth] = agreed;
        }
        raw = samples;
        return profile;
    }

    static void Judge(FrameworkElement root)
    {
        var detail = new System.Text.StringBuilder();
        foreach (var r in Readings)
        {
            detail.AppendLine(
                $"{r.Window,-22} backdrop={r.Backdrop} {(r.Active ? "active  " : "inactive")}");
            detail.AppendLine($"    top    {Describe(r.Top)}");
            detail.AppendLine($"    left   {Describe(r.Left)}");
            detail.AppendLine($"    right  {Describe(r.Right)}");
            detail.AppendLine($"    bottom {Describe(r.Bottom)}");

            if (r.ContentDepth is not null)
            {
                var parts = new string[AlongEdge.Length];
                for (int i = 0; i < AlongEdge.Length; i++)
                {
                    parts[i] = r.ContentDepth[i] < 0
                        ? $"{AlongEdge[i] * 100:0}%=none"
                        : $"{AlongEdge[i] * 100:0}%={r.ContentDepth[i]}px";
                }
                detail.AppendLine($"    content starts  {string.Join("  ", parts)}");
            }

            // "mixed" on the top edge is the case worth understanding, not hiding, so
            // spell out what each point along the edge actually read.
            if (r.Top is not null && r.TopRaw is not null && Array.IndexOf(r.Top, Mixed) >= 0)
            {
                for (int depth = 0; depth < r.Top.Length; depth++)
                {
                    if (r.Top[depth] != Mixed) { continue; }
                    var parts = new string[AlongEdge.Length];
                    for (int i = 0; i < AlongEdge.Length; i++)
                    {
                        parts[i] = $"{AlongEdge[i] * 100:0}%={Hex(r.TopRaw[(depth * AlongEdge.Length) + i]).Trim()}";
                    }
                    detail.AppendLine($"      top depth {depth}: {string.Join("  ", parts)}");
                }
            }
        }

        foreach (var s in Subjects)
        {
            detail.AppendLine($"{s.Title,-22} {s.Note}");
        }

        var lines = new List<string>();
        bool allMatch = true;
        bool anyJudged = false;

        foreach (var subject in Subjects)
        {
            if (!subject.IsEcitb) { continue; }

            foreach (var backdrop in Backdrops)
            {
                foreach (bool active in new[] { true, false })
                {
                    Reading? r = Readings.Find(x => x.Window == subject.Title
                        && x.Backdrop == backdrop.Name && x.Active == active);
                    if (r?.TopRaw is null || r.ContentDepth is null
                        || r.Left is null || r.Right is null || r.Bottom is null)
                    {
                        continue;
                    }

                    uint left = r.Left[0];
                    uint right = r.Right[0];
                    uint bottom = r.Bottom[0];

                    bool sidesAgree = Near(left, right) && Near(left, bottom);
                    string state = active ? "active" : "inactive";
                    if (!sidesAgree)
                    {
                        lines.Add($"{subject.Title} / {backdrop.Name} / {state}: the three "
                            + $"native sides do not agree with each other "
                            + $"(left {Hex(left)}, right {Hex(right)}, bottom {Hex(bottom)}), "
                            + "so there is no single border colour to compare the top against.");
                        allMatch = false;
                        continue;
                    }

                    // AppWindow owns its caption-button block. Do not mistake those pixels
                    // for the app-owned part of the custom title bar. At sample positions
                    // where app content starts at depth 0 or 1, however, ECITB must reserve
                    // exactly one frame row and that row must match the native side borders.
                    var samples = new List<string>();
                    bool readingMatches = true;
                    for (int i = 0; i < AlongEdge.Length; i++)
                    {
                        int contentDepth = r.ContentDepth[i];
                        if (contentDepth < 0 || contentDepth > 1) { continue; }

                        anyJudged = true;
                        uint top = r.TopRaw[i];
                        bool sampleMatches = contentDepth == 1
                            && Near(top, left) && Near(top, right) && Near(top, bottom);
                        readingMatches &= sampleMatches;
                        samples.Add($"{AlongEdge[i] * 100:0}% depth={contentDepth}px top={Hex(top).Trim()}");
                    }

                    if (samples.Count == 0) { continue; }

                    if (readingMatches)
                    {
                        lines.Add($"{subject.Title} / {backdrop.Name} / {state}: MATCH - "
                            + $"{string.Join(", ", samples)} vs sides "
                            + $"{Hex(left)}/{Hex(right)}/{Hex(bottom)}.");
                    }
                    else
                    {
                        lines.Add($"{subject.Title} / {backdrop.Name} / {state}: DIFFERS - "
                            + $"{string.Join(", ", samples)} vs sides "
                            + $"{Hex(left)}/{Hex(right)}/{Hex(bottom)}.");
                        allMatch = false;
                    }
                }
            }
        }

        // A translucent border tracks the backdrop. If the top edge is the same material
        // as the sides, it has to move by the same amount when the backdrop changes.
        // Matching on one backdrop could be luck; matching the shift cannot be.
        foreach (var subject in Subjects)
        {
            if (!subject.IsEcitb) { continue; }

            Reading? a = Readings.Find(x => x.Window == subject.Title
                && x.Backdrop == Backdrops[0].Name && x.Active);
            Reading? b = Readings.Find(x => x.Window == subject.Title
                && x.Backdrop == Backdrops[1].Name && x.Active);
            if (a?.Top is null || b?.Top is null || a.Left is null || b.Left is null) { continue; }

            lines.Add($"{subject.Title}: backdrop shift  top {Hex(a.Top[0])} -> {Hex(b.Top[0])}"
                + $"   left {Hex(a.Left[0])} -> {Hex(b.Left[0])}"
                + $"   ({(Shifts(a.Top[0], b.Top[0], a.Left[0], b.Left[0]) ? "same material" : "DIFFERENT material")})");
        }

        // Issue #8948 compares the two entry points to each other, so say plainly
        // whether they converge rather than leaving it to be read out of the table.
        Reading? viaWindow = Readings.Find(x => x.Window == WindowEcitbTitle
            && x.Backdrop == Backdrops[0].Name && x.Active);
        Reading? viaAppWindow = Readings.Find(x => x.Window == AppWindowEcitbTitle
            && x.Backdrop == Backdrops[0].Name && x.Active);
        if (viaWindow?.Top is not null && viaAppWindow?.Top is not null)
        {
            bool converge = Near(viaWindow.Top[0], viaAppWindow.Top[0]);
            lines.Add($"entry points: Window {Hex(viaWindow.Top[0])} vs AppWindow "
                + $"{Hex(viaAppWindow.Top[0])} - {(converge ? "converge" : "DO NOT CONVERGE")}.");
        }

        string headline = !anyJudged
            ? "inconclusive: no edge could be read. The windows are probably covered, or the desktop is locked."
            : allMatch
                ? "PASS - every app-owned ECITB top edge reserves one row that matches its native side borders, on both backdrops and in both focus states."
                : "DIFFERS - at least one app-owned ECITB top edge is missing its reserved row or does not match its native side borders. See the lines below.";

        Report(root, headline + "\n" + string.Join("\n", lines), anyJudged && allMatch,
            detail.ToString());
    }

    /// <summary>Two colours are the same border if every channel is within tolerance.</summary>
    static bool Near(uint a, uint b)
    {
        if (a == Mixed || b == Mixed || a == BadPixel || b == BadPixel) { return false; }
        for (int shift = 0; shift <= 16; shift += 8)
        {
            int lhs = (int)((a >> shift) & 0xFF);
            int rhs = (int)((b >> shift) & 0xFF);
            if (Math.Abs(lhs - rhs) > ChannelTolerance) { return false; }
        }
        return true;
    }

    /// <summary>
    /// Whether two edges responded to the backdrop change by the same amount. This is the
    /// strong form of the test: an opaque edge does not move at all, and two translucent
    /// edges of the same material move together.
    /// </summary>
    static bool Shifts(uint topA, uint topB, uint sideA, uint sideB)
    {
        if (topA == Mixed || topB == Mixed || sideA == Mixed || sideB == Mixed) { return false; }
        for (int shift = 0; shift <= 16; shift += 8)
        {
            int topDelta = (int)((topB >> shift) & 0xFF) - (int)((topA >> shift) & 0xFF);
            int sideDelta = (int)((sideB >> shift) & 0xFF) - (int)((sideA >> shift) & 0xFF);
            if (Math.Abs(topDelta - sideDelta) > ChannelTolerance * 2) { return false; }
        }
        return true;
    }

    static string Describe(uint[]? profile)
    {
        if (profile is null) { return "failed"; }
        var parts = new string[profile.Length];
        for (int i = 0; i < profile.Length; i++) { parts[i] = Hex(profile[i]); }
        return string.Join(" ", parts);
    }

    static string Hex(uint bgr) => bgr == Mixed
        ? "mixed  "
        : bgr == BadPixel
            ? "bad    "
            : $"#{bgr & 0xFF:X2}{(bgr >> 8) & 0xFF:X2}{(bgr >> 16) & 0xFF:X2}";

    static void Report(FrameworkElement root, string message, bool pass, string? detail = null)
    {
        Log(message);

        if (root.FindName("Result") is TextBlock result) { result.Text = message; }
        if (root.FindName("Detail") is TextBlock detailBlock && detail is not null)
        {
            detailBlock.Text = detail;
        }
        if (root.FindName("Verdict") is Border verdict)
        {
            verdict.Background = new SolidColorBrush(pass
                ? ColorHelper.FromArgb(255, 0x1B, 0x5E, 0x20)
                : ColorHelper.FromArgb(255, 0x7F, 0x1D, 0x1D));
        }

        WriteVerdictFile(message, detail);
    }

    static void WriteVerdictFile(string message, string? detail)
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(VerdictPath);
            if (dir is not null) { System.IO.Directory.CreateDirectory(dir); }

            var lines = new List<string>
            {
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  ECITB top edge vs native border",
                $"  OS       {Environment.OSVersion.Version}",
                $"  desktop  {InputDesktopName()}",
                $"  content  #{FillR:X2}{FillG:X2}{FillB:X2}",
                $"  profile  {EdgeDepth} pixels inward from each edge",
                string.Empty,
            };
            if (detail is not null) { lines.Add(detail); }
            lines.Add(message);
            lines.Add(string.Empty);

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
