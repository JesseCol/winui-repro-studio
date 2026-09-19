// repro:      Launch and display options
// wasdk:      2.2
// winui:      default
// payload:    none
// packaged:   no
// theme:      Dark
// flow:       LeftToRight
// dpi:        100
// background: #1F1F2E

// Launch and display headers, with a note on each. Copy this file as a starting
// point for a new repro and delete the lines you don't need - every key is
// optional, and order doesn't matter. For the win32 header, see cswin32.cs.
//
//   repro       Friendly name. Shows up in the runner's title bar.
//   wasdk       WASDK version. Partial is fine: "2.2" picks the newest 2.2.x.
//               Press V in the console, or use ReproStudio --list, to see versions.
//               Write an exact version (for example 2.2.0) to pin it and skip
//               the version-list lookup.
//   winui       Override just the WinUI component. A version, a path to a local
//               .nupkg (relative paths resolve next to this file), or "default".
//   payload     Folder of loose files to copy over the runner, after the WASDK
//               version is laid down. Drop a private Microsoft.ui.xaml.dll in
//               and it wins over the stock one. Relative paths resolve next to
//               this file. "none" ignores the default payload\ folder.
//   packaged    yes/no. Registers the runner as a loose-layout package so it
//               runs with real package identity. Needs Developer Mode.
//   theme       Default | Light | Dark
//   flow        LeftToRight | RightToLeft
//   dpi         100 to 400. Scale factor the runner launches at.
//   background  Stage colour behind your XAML. Any XAML colour string.
//
// Pin is a Runner preference, not a header. The toolbar remembers it across runs.
// theme, flow and background apply live on save. wasdk, winui, payload,
// packaged and dpi relaunch the runner, so they take a couple of seconds.

class Repro
{
    const string Xaml = """
        <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Padding="24" Spacing="12">
            <TextBlock Text="Launch and display options" Style="{StaticResource TitleTextBlockStyle}" />
            <TextBlock Text="Change a header and save. Display options update here; runtime options restart the Runner."
                       TextWrapping="Wrap" />
            <TextBlock x:Name="Details" TextWrapping="Wrap" />
            <Button x:Name="WhatAmIRunning" AutomationProperties.AutomationId="WhatAmIRunning"
                    Content="What am I running?" />
        </StackPanel>
        """;

    static void Setup(FrameworkElement root, Window window)
    {
        window.Title = "Launch and display options";

        if (root.FindName("Details") is TextBlock details)
        {
            // The host applies theme and flow when it attaches the repro to the stage.
            root.Loaded += (_, _) =>
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
