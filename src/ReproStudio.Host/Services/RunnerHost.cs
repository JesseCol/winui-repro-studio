using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ReproStudio.Shared;

namespace ReproStudio_Host.Services;

/// <summary>
/// Owns the request file and the runner process. The host writes snippets to the
/// request file; the runner (a separate window, a specific WASDK version) watches
/// it and renders live. Switching versions just launches a different runner exe
/// against the same request file.
/// <para>
/// The runner normally runs unpackaged (a plain <c>Process.Start</c>). When packaged
/// mode is requested it is instead registered as a loose-layout package and activated
/// by AUMID (see <see cref="PackagedRunnerLauncher"/>), so the same repro can be tested
/// with package identity.
/// </para>
/// </summary>
public sealed class RunnerHost : IDisposable
{
    private readonly string _requestPath;
    private readonly PackagedRunnerLauncher _packagedLauncher;
    private Process? _process;

    public RunnerHost(PackagedRunnerLauncher packagedLauncher)
    {
        _packagedLauncher = packagedLauncher ?? throw new ArgumentNullException(nameof(packagedLauncher));

        string dir = Path.Combine(
            Path.GetTempPath(),
            "winui-repro-app",
            "runner-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _requestPath = Path.Combine(dir, "request.json");
    }

    /// <summary>Whether the runner started, and a short note describing the identity mode.</summary>
    public readonly record struct LaunchResult(bool Launched, string ModeNote);

    public int? ProcessId => _process is { HasExited: false } ? _process.Id : null;

    public void WriteRequest(Snippet snippet) => SnippetIo.WriteAtomic(_requestPath, snippet);

    /// <summary>
    /// Launches the runner at <paramref name="exePath"/> watching our request file, replacing any
    /// previously running runner. When <paramref name="packaged"/> is true the runner is registered
    /// as a loose-layout package and activated by AUMID (falling back to an unpackaged launch, with a
    /// note, if that fails); otherwise it is launched unpackaged. Optional screen bounds (physical
    /// pixels) tell the runner where to place its window.
    /// </summary>
    public async Task<LaunchResult> LaunchAsync(
        string exePath,
        (int X, int Y, int Width, int Height)? bounds,
        bool packaged)
    {
        ArgumentNullException.ThrowIfNull(exePath);

        KillProcess();
        if (!File.Exists(exePath))
        {
            return new LaunchResult(false, string.Empty);
        }

        List<string> args = BuildArgs(bounds);

        if (packaged)
        {
            (bool launched, string? failure) = await TryLaunchPackagedAsync(exePath, args).ConfigureAwait(false);
            if (launched)
            {
                return new LaunchResult(true, " (packaged)");
            }

            // Fall back to an unpackaged launch so the tool stays usable, noting why.
            bool fell = LaunchUnpackaged(exePath, args);
            return new LaunchResult(fell, $" (packaged failed: {failure}; running unpackaged)");
        }

        // Drop any packaged registration so this truly runs unpackaged (no-op if none).
        await _packagedLauncher.UnregisterAsync().ConfigureAwait(false);
        bool started = LaunchUnpackaged(exePath, args);
        return new LaunchResult(started, " (unpackaged)");
    }

    /// <summary>Removes any packaged-runner registration. Used when the provisioned runners are cleared.</summary>
    public Task UnregisterPackagedAsync() => _packagedLauncher.UnregisterAsync();

    public void Dispose()
    {
        KillProcess();

        if (_packagedLauncher.IsRegistered)
        {
            try
            {
                _packagedLauncher.UnregisterAsync().Wait(TimeSpan.FromSeconds(5));
            }
#pragma warning disable CA1031 // Best-effort cleanup on shutdown; never throw from Dispose.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Leave the dev package registered; it is harmless and reconciled next run.
            }
        }
    }

    /// <summary>Stops the current runner process (if any), releasing its file locks.</summary>
    public void Stop() => KillProcess();

    private async Task<(bool Launched, string? Failure)> TryLaunchPackagedAsync(string exePath, List<string> args)
    {
        string versionFolder = Path.GetDirectoryName(exePath)!;

        PackagedRunnerLauncher.RegisterResult registration =
            await _packagedLauncher.EnsureRegisteredAsync(versionFolder).ConfigureAwait(false);
        if (!registration.Success)
        {
            return (false, registration.Message);
        }

        int? pid = _packagedLauncher.Activate(registration.Aumid, QuoteArgs(args));
        if (pid is null)
        {
            return (false, "AUMID activation failed");
        }

        _process = TryGetProcess(pid.Value);
        return _process is null ? (false, "activated process was not found") : (true, null);
    }

    private bool LaunchUnpackaged(string exePath, List<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
        };
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        _process = Process.Start(startInfo);
        return _process is not null;
    }

    private List<string> BuildArgs((int X, int Y, int Width, int Height)? bounds)
    {
        var args = new List<string> { "--request", _requestPath };
        if (bounds is (int x, int y, int width, int height))
        {
            args.Add("--bounds");
            args.Add(x.ToString(CultureInfo.InvariantCulture));
            args.Add(y.ToString(CultureInfo.InvariantCulture));
            args.Add(width.ToString(CultureInfo.InvariantCulture));
            args.Add(height.ToString(CultureInfo.InvariantCulture));
        }

        return args;
    }

    /// <summary>Joins args into a single command line for AUMID activation, quoting where needed.</summary>
    private static string QuoteArgs(IEnumerable<string> args) =>
        string.Join(' ', args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a));

    private static Process? TryGetProcess(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // The activated process already exited.
            return null;
        }
    }

    private void KillProcess()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }

        _process?.Dispose();
        _process = null;
    }
}
