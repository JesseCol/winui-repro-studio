using System;

namespace ReproStudio_Runner;

/// <summary>
/// Small helper surface that repro snippets can call. The runner points
/// <see cref="LogSink"/> at its log panel, and the snippet just calls
/// <c>Log("...")</c> (it is imported via <c>using static</c> in the compiler).
/// </summary>
public static class ReproApi
{
    public static Action<string>? LogSink { get; set; }

    public static void Log(string message) => LogSink?.Invoke(message ?? string.Empty);
}
