using System.Diagnostics;
using Microsoft.Win32;

namespace ReproStudio_Runner.Services;

internal static class CodeEditor
{
    public static void Open(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathFullyQualified(sourcePath))
            throw new InvalidOperationException("The host did not provide the original repro's absolute path. Run the repro again with a current host.");
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The original repro file is missing or inaccessible: " + sourcePath);
        string code = FindCode()
            ?? throw new FileNotFoundException("Stable Visual Studio Code was not found. Install VS Code (user/system installer), or put Code.exe on PATH. Other editors and Code Insiders are not used.");
        using Process? process = Process.Start(CreateStartInfo(code, sourcePath));
        if (process is null) throw new InvalidOperationException("VS Code could not be started.");
    }

    internal static ProcessStartInfo CreateStartInfo(string code, string sourcePath)
    {
        var start = new ProcessStartInfo(code) { UseShellExecute = false };
        start.ArgumentList.Add("--reuse-window");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(sourcePath);
        return start;
    }

    private static string? FindCode()
    {
        var candidates = new List<string?>
        {
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\App Paths\Code.exe", "", null) as string,
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\App Paths\Code.exe", "", null) as string,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft VS Code", "Code.exe"),
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "Code.exe")));
        return candidates.Select(path => path?.Trim('"')).FirstOrDefault(path =>
            path is not null && Path.IsPathFullyQualified(path)
            && Path.GetFileName(path).Equals("Code.exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(path));
    }
}
