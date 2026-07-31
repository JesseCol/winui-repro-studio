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
                    Padding="24" Spacing="12" Width="260">
            <TextBlock Text="Counter" FontSize="48" />
            <TextBlock x:Name="CountText" Text="0" FontSize="48" />
            <Button x:Name="BumpButton" Content="Bump it" HorizontalAlignment="Stretch" />
        </StackPanel>
        """;

    static int _count;

    static void Setup(FrameworkElement root, Window window)
    {
        window.Title = "Counter repro -- this is the new title";
        Log("this is a test");
        Log("This is another line....");

        if (root.FindName("BumpButton") is Button bump
            && root.FindName("CountText") is TextBlock countText)
        {
            bump.Content = "Bump it!  This is live";

            bump.Click += (s, e) =>
            {
                _count++;
                countText.Text = _count.ToString();
                Log($"Count is now {_count}.");
            };
        }
    }
}
