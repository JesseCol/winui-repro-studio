namespace ReproStudio_Cli;

/// <summary>
/// Console output. Everything the tool prints goes through here so the format stays
/// consistent and so colour can be dropped in one place when output is redirected
/// (piped to a file, captured by CI, or read by an editor task runner).
/// </summary>
public static class Log
{
    /// <summary>Width of the label column in <see cref="Field"/>, chosen to fit "packaged".</summary>
    private const int LabelWidth = 9;

    private static readonly bool UseColour = !Console.IsOutputRedirected;

    /// <summary>A blank line, for separating sections.</summary>
    public static void Blank() => Console.WriteLine();

    /// <summary>The one-line banner at the top of a run.</summary>
    public static void Banner(string text) => Write(text, ConsoleColor.White);

    /// <summary>A section heading, e.g. <c>&gt; provision</c>.</summary>
    public static void Step(string text)
    {
        Console.WriteLine();
        Write("> " + text, ConsoleColor.Cyan);
    }

    /// <summary>An aligned label/value line, with an optional dimmed note after the value.</summary>
    public static void Field(string label, string value, string? note = null)
    {
        Console.Write("  " + label.PadRight(LabelWidth) + " ");
        Console.Write(value);
        if (note is { Length: > 0 })
        {
            WriteInline("  " + note, ConsoleColor.DarkGray);
        }

        Console.WriteLine();
    }

    /// <summary>A step in a longer operation, e.g. a provisioning stage.</summary>
    public static void Detail(string text) => Write("  . " + text, ConsoleColor.DarkGray);

    /// <summary>A successful outcome worth calling out.</summary>
    public static void Ok(string text) => Write("  " + text, ConsoleColor.Green);

    /// <summary>Something worked, but not the way it was asked for.</summary>
    public static void Warn(string text) => Write("  ! " + text, ConsoleColor.Yellow);

    /// <summary>A failure. Goes to stderr so it survives a pipe.</summary>
    public static void Error(string text)
    {
        if (UseColour)
        {
            Console.ForegroundColor = ConsoleColor.Red;
        }

        Console.Error.WriteLine("  x " + text);
        if (UseColour)
        {
            Console.ResetColor();
        }
    }

    /// <summary>A timestamped line in the watch loop.</summary>
    public static void Event(string text)
    {
        WriteInline("  " + DateTime.Now.ToString("HH:mm:ss") + "  ", ConsoleColor.DarkGray);
        Console.WriteLine(text);
    }

    /// <summary>Plain text with no decoration, used for help and quoted file content.</summary>
    public static void Raw(string text) => Console.WriteLine(text);

    private static void Write(string text, ConsoleColor colour)
    {
        WriteInline(text, colour);
        Console.WriteLine();
    }

    private static void WriteInline(string text, ConsoleColor colour)
    {
        if (!UseColour)
        {
            Console.Write(text);
            return;
        }

        Console.ForegroundColor = colour;
        Console.Write(text);
        Console.ResetColor();
    }
}
