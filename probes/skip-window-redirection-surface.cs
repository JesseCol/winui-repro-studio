// repro: SkipWindowRedirectionSurface
// theme: Dark
//
// Question: does XamlChangeId 63530879 (SkipWindowRedirectionSurface) actually
//           clear WS_EX_NOREDIRECTIONBITMAP on the XAML window?
// Answer:   on stock WASDK 2.4.0, no. The change id is not recognised, and the
//           window comes up with exStyle 0x00000100, so the bit is clear. Not
//           yet run against a build that carries the change.
//
// Useful side effect of that reading: the interop path itself is fine on stock.
// It found Microsoft.UI.Xaml.Settings.XamlOptionalChanges, matched the statics
// IID and the vtable slot, and the native call returned success having simply
// declined the id. So if a candidate build fails this probe, suspect the change
// rather than the plumbing.
//
// How it tells: the DWM redirection surface is invisible by design, so this
// paints it bright green first. If the change works, no green is ever visible
// and WS_EX_NOREDIRECTIONBITMAP is set. WCA_REDIRECTION_BITMAP_FILL_COLOR (35)
// is not documented publicly.
//
// The change must be enabled before Application.Start, which is what the
// OnProcessLaunch hook is for. A build that has never heard of this change id
// reports "not enabled" rather than failing, so the probe still gives you a
// reading on a stock runner.
//
// Run against a candidate build with:
//   ReproStudio.exe probes\skip-window-redirection-surface.cs --winui <path.nupkg>

using System.Runtime.InteropServices;

class Repro
{
    const int GwlExStyle = -20;
    const int WcaRedirectionBitmapFillColor = 35;
    const long WsExNoRedirectionBitmap = 0x00200000;
    const uint BrightGreenColor = 0xFF00FF00;
    const int SkipWindowRedirectionSurface = 63530879;

    const string Xaml = """
        <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
              Padding="32">
            <StackPanel VerticalAlignment="Center" Spacing="12">
                <TextBlock Style="{ThemeResource TitleTextBlockStyle}"
                           Text="SkipWindowRedirectionSurface" />
                <TextBlock MaxWidth="520"
                           Text="The DWM redirection bitmap fill is bright green. No green should be visible while the XOC is enabled."
                           TextWrapping="Wrap" />
                <InfoBar x:Name="StatusBar"
                         IsClosable="False"
                         IsOpen="True"
                         Title="Checking window style..." />
            </StackPanel>
        </Grid>
        """;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetWindowCompositionAttribute(
        nint hwnd,
        ref WindowCompositionAttributeData data);

    static void OnProcessLaunch()
    {
        // XamlOptionalChanges must be enabled before Application.Start, so this
        // cannot move into Setup. A runner whose Microsoft.UI.Xaml.dll does not
        // know this change id throws here; swallow it so the window still comes
        // up and the style check below can report the "not enabled" case.
        try
        {
            EnableXamlOptionalChange(SkipWindowRedirectionSurface);
            Log($"Enabled XamlChangeId.SkipWindowRedirectionSurface ({SkipWindowRedirectionSurface}).");
        }
        catch (Exception ex)
        {
            Log(
                $"Could not enable XamlChangeId {SkipWindowRedirectionSurface}: "
                + $"{ex.GetType().FullName}, HRESULT 0x{ex.HResult:X8}. {ex.Message}");
        }
    }

    static void Setup(FrameworkElement root, Window window)
    {
        window.Title = "SkipWindowRedirectionSurface";
        if (root.FindName("StatusBar") is not InfoBar status)
        {
            return;
        }

        string step = "getting the window handle";
        try
        {
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

            step = "setting the redirection bitmap fill color";
            SetRedirectionBitmapFillColor(hwnd, BrightGreenColor);

            step = "reading the extended window style";
            long extendedStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            bool hasNoRedirectionBitmap =
                (extendedStyle & WsExNoRedirectionBitmap) != 0;

            status.Severity = hasNoRedirectionBitmap
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Error;
            status.Title = hasNoRedirectionBitmap
                ? "XOC enabled"
                : "XOC not enabled";
            status.Message =
                $"Extended window style: 0x{extendedStyle:X8}. "
                + $"WS_EX_NOREDIRECTIONBITMAP is "
                + (hasNoRedirectionBitmap ? "set." : "not set.")
                + " Redirection bitmap fill: opaque bright green (0xFF00FF00).";

            Log(
                $"Extended window style: 0x{extendedStyle:X8}; "
                + $"WS_EX_NOREDIRECTIONBITMAP={hasNoRedirectionBitmap}; "
                + "WCA_REDIRECTIONBITMAP_FILL_COLOR=0xFF00FF00.");
        }
        catch (Exception ex)
        {
            string message =
                $"{step} failed: {ex.GetType().FullName}, HRESULT 0x{ex.HResult:X8}. "
                + (string.IsNullOrEmpty(ex.Message) ? "(no message)" : ex.Message)
                + $"\n{ex.StackTrace}";
            status.Severity = InfoBarSeverity.Error;
            status.Title = "Setup failed";
            status.Message = message;
            Log(message);
        }
    }

    static void SetRedirectionBitmapFillColor(nint hwnd, uint color)
    {
        nint colorData = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(colorData, unchecked((int)color));
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaRedirectionBitmapFillColor,
                Data = colorData,
                SizeOfData = sizeof(uint),
            };

            if (!SetWindowCompositionAttribute(hwnd, ref data))
            {
                throw new InvalidOperationException(
                    $"SetWindowCompositionAttribute failed with Win32 error "
                    + $"{Marshal.GetLastWin32Error()}.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(colorData);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowCompositionAttributeData
    {
        public int Attribute;
        public nint Data;
        public int SizeOfData;
    }
}
