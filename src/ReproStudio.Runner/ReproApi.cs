using System;
using ReproStudio_Runner.Services;

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

    /// <summary>
    /// Enables a numeric XAML optional change before XAML initialization. Call this
    /// from the CLI-only <c>OnProcessLaunch</c> hook.
    /// </summary>
    public static void EnableXamlOptionalChange(int changeId) =>
        XamlOptionalChangesInterop.EnableChange(changeId);
}
