using System;
using System.IO;

namespace ReproStudio_Host;

/// <summary>
/// Minimal append-only log for diagnosing host crashes. Writes to the same temp
/// area the runner uses, so both logs sit side by side during development.
/// </summary>
public static class HostLog
{
    private static readonly string LogPath = Path.Combine(
        Path.GetTempPath(), "winui-repro-app", "host.log");

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
