// repro: P/Invoke from a repro file
// wasdk: 2.3.1

// A repro can declare its own using directives. The runner prepends the common
// WinUI ones, so duplicates like the System line below are fine - C# treats a
// repeated using as a warning, not an error.
using System;
using System.Runtime.InteropServices;
using WinRT.Interop;

class Repro
{
    const string Xaml = """
        <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Padding="24" Spacing="12">
            <TextBlock Text="Win32 interop" FontSize="28" />
            <TextBlock x:Name="Result" FontSize="16" TextWrapping="Wrap" />
        </StackPanel>
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int LeftWidth;
        public int RightWidth;
        public int TopHeight;
        public int BottomHeight;
    }


    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    static void Setup(FrameworkElement root, Window window)
    {
        // The one WinUI-specific step: turn the Window into an HWND.
        IntPtr hwnd = WindowNative.GetWindowHandle(window);

        int m = 0;

        // -1 on all four sides is the "sheet of glass" extend: the frame covers
        // the whole client area.
        var margins = new Margins
        {
            LeftWidth = m,
            RightWidth = m,
            TopHeight = m,
            BottomHeight = m,
        };

        window.ExtendsContentIntoTitleBar = true;

        int hr = DwmExtendFrameIntoClientArea(hwnd, ref margins);

        const int GwlExStyle = -20;
        int exStyle = GetWindowLong(hwnd, GwlExStyle);

        string text = $"hwnd 0x{hwnd:X}, DwmExtendFrameIntoClientArea -> 0x{hr:X8}, ex-style 0x{exStyle:X8}";
        Log(text);
        if (root.FindName("Result") is TextBlock result)
        {
            result.Text = text;
        }

        // Note: the call succeeds, but you will not see glass. The runner paints
        // its own opaque stage over the client area, and DWM only shows through
        // pixels that are transparent.
    }
}
