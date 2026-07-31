// repro:      Every header key
// wasdk:      1.7
// winui:      default
// packaged:   no
// theme:      Dark
// flow:       LeftToRight
// dpi:        100
// background: #1F1F2E
// topmost:    no

// Every supported header key, with a note on each. Copy this file as a starting
// point for a new repro and delete the lines you don't need - every key is
// optional, and order doesn't matter.
//
//   repro       Friendly name. Shows up in the runner's title bar.
//   wasdk       WASDK version. Partial is fine: "1.7" picks the newest 1.7.x.
//               Write a full version (three dots) to pin it and skip the
//               network lookup entirely.
//   winui       Override just the WinUI component. A version, a path to a local
//               .nupkg (relative paths resolve next to this file), or "default".
//   packaged    yes/no. Registers the runner as a loose-layout package so it
//               runs with real package identity. Needs Developer Mode.
//   theme       Default | Light | Dark
//   flow        LeftToRight | RightToLeft
//   dpi         100 to 400. Scale factor the runner launches at.
//   background  Stage colour behind your XAML. Any XAML colour string.
//   topmost     yes/no. Keeps the runner above other windows.
//
// theme, flow, background and topmost apply live on save. wasdk, winui,
// packaged and dpi relaunch the runner, so they take a couple of seconds.

class Repro
{
    const string Xaml = """
        <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Padding="24" Spacing="12" Width="360">
            <TextBlock Text="Every header key" FontSize="28" />
            <TextBlock x:Name="Details" TextWrapping="Wrap" Opacity="0.8" />
            <Button x:Name="WhatAmIRunning" Content="What am I running?"
                    HorizontalAlignment="Stretch" />
        </StackPanel>
        """;

    static void Setup(FrameworkElement root, Window window)
    {
        window.Title = "Every header key";

        if (root.FindName("Details") is TextBlock details)
        {
            details.Text = $"Theme is {root.ActualTheme}, flow is {root.FlowDirection}.";
        }

        if (root.FindName("WhatAmIRunning") is Button button)
        {
            button.Click += (s, e) =>
            {
                // The runner's footer already shows the loaded Microsoft.ui.xaml.dll
                // version. This just proves your C# is running against it.
                var xaml = typeof(Button).Assembly.GetName();
                Log($"Managed projection: {xaml.Name} {xaml.Version}");
                Log("Native version is in the footer - that's the one that changes.");
            };
        }
    }
}
