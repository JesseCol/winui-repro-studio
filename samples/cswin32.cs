// repro: Generated Win32 bindings with CsWin32
// wasdk: 2.2.0
// win32: GetWindowRect, GetDpiForWindow

using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

class Repro
{
    const string Xaml = """
        <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Padding="24" Spacing="12"
                    Background="{ThemeResource ApplicationPageBackgroundThemeBrush}">
            <TextBlock Text="Generated Win32 bindings" Style="{StaticResource TitleTextBlockStyle}" />
            <TextBlock Text="Move or resize the window, then refresh. The rectangle uses screen coordinates."
                       TextWrapping="Wrap" />
            <TextBlock x:Name="Result" IsTextSelectionEnabled="True" TextWrapping="Wrap" />
            <Button x:Name="Refresh" AutomationProperties.AutomationId="CsWin32Refresh"
                    AutomationProperties.Name="Refresh window measurements" Content="Refresh" />
        </StackPanel>
        """;

    static void Setup(FrameworkElement root, Window window)
    {
        var result = (TextBlock)root.FindName("Result");
        IntPtr handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var hwnd = new HWND(handle);

        void Refresh()
        {
            string rectangle;
            if (PInvoke.GetWindowRect(hwnd, out RECT rect))
            {
                rectangle = $"Rectangle: ({rect.left}, {rect.top})-({rect.right}, {rect.bottom})\n"
                    + $"Size: {rect.right - rect.left} x {rect.bottom - rect.top} pixels";
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                rectangle = $"GetWindowRect failed: {error} ({new Win32Exception(error).Message})";
            }

            uint dpi = PInvoke.GetDpiForWindow(hwnd);
            // GetDpiForWindow returns zero for an invalid HWND; it does not set last error.
            string dpiText = dpi == 0
                ? "GetDpiForWindow failed: returned 0 (invalid window handle)."
                : $"DPI: {dpi} ({dpi / 96.0:P0} scaling)";
            result.Text = $"HWND: 0x{handle:X}\n{rectangle}\n{dpiText}";
            Log(result.Text);
        }

        ((Button)root.FindName("Refresh")).Click += (_, _) => Refresh();
        Refresh();
    }
}
