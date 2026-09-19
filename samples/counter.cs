// repro: Clicky counter
// theme: Dark
// flow:  LeftToRight

// Shows off C# driving the XAML: a button that bumps a number.
// (State resets on every save - each edit recompiles into a fresh assembly.)
class Repro
{
    const string Xaml = """
        <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Padding="24" Spacing="12"
                    Background="{ThemeResource SolidBackgroundFillColorBaseBrush}">
            <TextBlock Text="Click counter" Style="{StaticResource TitleTextBlockStyle}" />
            <TextBlock Text="Click to change C# state. Save an edit to reset it."
                       TextWrapping="Wrap" />
            <TextBlock x:Name="CountText" AutomationProperties.AutomationId="CountText"
                       Text="0" Style="{StaticResource DisplayTextBlockStyle}" />
            <Button x:Name="BumpButton" AutomationProperties.AutomationId="BumpButton"
                    AutomationProperties.Name="Increment the counter" Content="Increment" />
        </StackPanel>
        """;

    static int _count;

    static void Setup(FrameworkElement root, Window window)
    {
        window.Title = "Counter repro";
        Log("Counter ready. Each save compiles a fresh snippet and resets the count.");

        if (root.FindName("BumpButton") is Button bump
            && root.FindName("CountText") is TextBlock countText)
        {
            bump.Click += (s, e) =>
            {
                _count++;
                countText.Text = _count.ToString();
                Log($"Count is now {_count}.");
            };
        }
    }
}
