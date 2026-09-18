// repro: Hello from a file
// theme: Default
// flow:  LeftToRight

// A single-file repro. The CLI watches this file; every save refreshes the
// Runner. XAML lives in the Xaml raw-string; your logic goes in Setup.
// No wasdk header means newest stable. Add "// wasdk: 2.2" above to choose one.
// Press V in the watching console, or use ReproStudio --list, to see versions.
// Keep the background theme-aware: Dark-theme text and button styles use white.
class Repro
{
    const string Xaml = """
        <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Padding="24" Spacing="12"
                    Background="{ThemeResource SolidBackgroundFillColorBaseBrush}">
            <TextBlock Text="Hello from a file!" FontSize="28" />
            <Button x:Name="HelloButton" AutomationProperties.AutomationId="HelloButton"
                    Content="Click me" />
        </StackPanel>
        """;

    // Setup can ask for the parsed root, the Window, or both.
    // Call Log("...") any time to write to the runner's log panel.
    static void Setup(FrameworkElement root, Window window)
    {
        window.Title = "Hello repro";
        window.ExtendsContentIntoTitleBar = true;
        Log("Loaded from hello.cs.");

        if (root.FindName("HelloButton") is Button button)
        {
            button.Click += (s, e) =>
            {
                button.Content = "Clicked!";
                Log("Button clicked.");
            };
        }
    }
}
