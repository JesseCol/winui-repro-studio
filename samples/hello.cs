// repro: Hello from a file
// theme: Default
// flow:  LeftToRight
// win32: DwmSetWindowAttribute

/*
REPROSTUDIO QUICK TOUR

Start here: change the heading in Xaml below, then save. The host watches this
file and refreshes the Runner without a project rebuild. Setup wires up events;
each save starts the snippet's state over. Keep the class and Xaml string.

The toolbar is yours, not part of the repro:
  Pin keeps the Runner on top and remembers your choice across launches.
  Open in VS Code opens this file.
  Runtime opens the native runtime and SDK/API picker. Apply writes the choices
  into this file's headers and restarts the Runner; Cancel changes nothing.

HEADER IDEAS
Copy settings ABOVE this block, with the existing // key: value lines.
Examples inside this block are just comments, not active settings.

Choose a native Windows App SDK runtime:
  // wasdk: 2.4.1-experimental

For SDK/API, match follows the native source (the default), base keeps the APIs
bundled with the tool, and a version chooses APIs independently:
  // sdk: match
  // sdk: base
  // sdk: 2.4.1-experimental

Choose ONE sdk line. A newer API can compile but fail on an older native runtime.
Without a wasdk/winui choice, the default is the newest stable runtime. Pin an
exact version for a repeatable repro. Press V in the console to list versions.
Instead of wasdk, choose WinUI in the dialog and Browse to a custom .nupkg.
With Match selected, a full package supplies its managed C# APIs and native bits.

Try loose native files, package identity (needs Developer Mode), or display
options. Theme accepts Light, Dark or Default; flow also accepts LeftToRight:
  // payload: D:\my-winui-build
  // packaged: yes
  // theme: Light
  // flow: RightToLeft

Replace an existing theme/flow line rather than adding a second one.
Display edits apply live; SDK/runtime, payload and packaged changes restart.
Keep ThemeResource brushes so your XAML works with both light and dark text.
Use --payload none for a stock comparison, ignoring any default payload folder.

SAVE THE PIXELS
From the repo: dotnet run -- samples\hello.cs --headless --no-watch --payload none
In a bundle, replace "dotnet run --" with ".\ReproStudio.exe".
This cloaks the Runner, saves ReproStudio.png in the working directory, then exits.
Add --screenshot captures\hello.png to choose the path. Omit --no-watch to capture
again on each save. Headless still needs a working graphical desktop.

Log(...) writes to the Runner's log panel and the log file named by the host.
For trouble, run with --doctor; --help lists options. Ctrl+C stops a watched run.
The win32 header generates the PInvoke binding used below. No DllImport needed!
The dark title-bar example uses a documented Windows 11 attribute; older Windows
versions skip it. Set darkMode to 0 below and save to return to a light title bar.
See samples\cswin32.cs for generated Win32 calls, samples\full-header.cs for more
headers, and docs\guide.md for the full tour. Only run repro files you trust.
*/

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

class Repro
{
    const string Xaml = """
        <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Padding="24" Spacing="12"
                    Background="{ThemeResource SolidBackgroundFillColorBaseBrush}">
            <TextBlock Text="Hello from a file!" Style="{StaticResource TitleTextBlockStyle}" />
            <TextBlock Text="Edit this heading in the file printed by the host, then save. This preview updates in place."
                       TextWrapping="Wrap" />
            <Button x:Name="HelloButton" AutomationProperties.AutomationId="HelloButton"
                    Content="Click me" />
            <TextBlock Text="The log below shows calls to Log(). The footer shows the WinUI version actually running."
                       TextWrapping="Wrap" />
        </StackPanel>
        """;

    // Setup can ask for the parsed root, the Window, or both.
    // Call Log("...") any time to write to the runner's log panel.
    static void Setup(FrameworkElement root, Window window)
    {
        window.Title = "Hello repro";
        Log("Hello! Click the button, or edit the heading and save the file.");

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            var hwnd = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(window));
            int darkMode = 1; // Win32 BOOL is four bytes, unlike C# bool.
            unsafe
            {
                var result = PInvoke.DwmSetWindowAttribute(hwnd,
                    DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE,
                    &darkMode, sizeof(int));
                Log(result.Value < 0
                    ? $"DwmSetWindowAttribute failed: 0x{result.Value:X8}"
                    : $"Native title-bar dark mode: {darkMode != 0}.");
            }
        }
        else
        {
            Log("Dark title-bar example skipped: this attribute requires Windows 11.");
        }

        if (root.FindName("HelloButton") is Button button)
        {
            button.Click += (s, e) =>
            {
                button.Content = "Clicked!";
                Log("Button clicked. Save an edit to start this snippet again.");
            };
        }
    }
}
