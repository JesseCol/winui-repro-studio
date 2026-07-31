using System;
using System.IO;

namespace ReproStudio_Runner.Services;

/// <summary>
/// Minimal append-only log for diagnosing runner crashes. Writes next to the
/// request file's temp area so it is easy to find during development.
/// </summary>
public static class CrashLog
{
    private static readonly string LogPath = Path.Combine(
        Path.GetTempPath(), "winui-repro-app", "runner.log");

    public static void Log(string message)
    {
        try
        {
            string? dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging must never throw.
        }
    }
}
