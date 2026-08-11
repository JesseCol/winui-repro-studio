using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NuGet.Versioning;
using ReproStudio.Shared;

namespace ReproStudio_Cli;

/// <summary>
/// The environment report behind <c>--doctor</c>.
/// <para>
/// This tool is used to chase Windows App SDK bugs, often on a machine nobody set up
/// for development. When the runner will not start, the cause is usually environmental:
/// the OS is too old, the bundle was extracted wrong, NuGet is blocked, or Developer
/// Mode is off. Doctor answers those before anyone starts reading stack traces.
/// </para>
/// <para>
/// Findings are collected rather than just printed, so the report can end with a plain
/// verdict. Someone staring at a broken machine wants "you are missing X", not thirty
/// lines to interpret.
/// </para>
/// </summary>
internal static class Doctor
{
    /// <summary>The oldest Windows build the runner supports (Windows 10 1809, "RS5").</summary>
    private const int MinimumBuild = 17763;

    /// <summary>
    /// Disk to keep free per provisioned runner. Each version is a full copy of the
    /// base plus that version's native DLLs, so this adds up fast on a small test VM.
    /// Measured folders run 156-260 MB; this is deliberately above that so the check
    /// leaves room for the download cache and the staging folder.
    /// </summary>
    private const long BytesPerVersion = 350L * 1024 * 1024;

    /// <summary>How long to wait on the NuGet probe before calling it unreachable.</summary>
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(20);

    public static async Task<int> ReportAsync(AppLayout layout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var findings = new Findings();

        Log.Step("windows");
        ReportWindows(findings);

        Log.Step("runner");
        ReportRunner(layout, findings);

        Log.Step("storage");
        ReportStorage(layout, findings);

        Log.Step("network");
        await ReportNetworkAsync(findings, ct).ConfigureAwait(false);

        Log.Step("packaged mode");
        ReportDeveloperMode(findings);

        Log.Step("logs");
        ReportLogs();

        return Verdict(findings);
    }

    private static void ReportWindows(Findings findings)
    {
        Version os = Environment.OSVersion.Version;
        int build = os.Build;
        string ubr = ReadRegistryValue(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR") is string revision
            ? "." + revision
            : string.Empty;

        string? display = ReadRegistryValue(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion")
            ?? ReadRegistryValue(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ReleaseId");

        Log.Field("build", $"10.0.{build}{ubr}", display is null ? null : "(" + display + ")");
        Log.Field("os arch", RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
        Log.Field("this app", RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());

        if (build < MinimumBuild)
        {
            findings.Fail($"Windows build {build} is below the {MinimumBuild} floor (Windows 10 1809). "
                + "The Windows App SDK will not load here, and nothing in this tool can work around that.");
        }
        else
        {
            Log.Ok($"meets the {MinimumBuild} floor");
        }

        if (RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture)
        {
            findings.Warn("This is a "
                + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
                + " build on a "
                + RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()
                + " machine, so it runs emulated. Use the matching bundle if a repro looks off.");
        }

        // A window has to have somewhere to appear. Running under a service account or a
        // non-interactive session lets the host start but leaves the runner invisible.
        if (!Environment.UserInteractive)
        {
            findings.Fail("This is not an interactive session, so the runner has no desktop to "
                + "put a window on. Run it from a normal desktop or RDP session.");
        }
    }

    private static void ReportRunner(AppLayout layout, Findings findings)
    {
        Log.Field("mode", layout.IsPortable ? "portable (xcopy bundle)" : "dev (base runner from cache)");
        Log.Field("app dir", AppContext.BaseDirectory);
        Log.Field("base", layout.BaseRunnerDir);

        if (!layout.HasBaseRunner)
        {
            findings.Fail(layout.DescribeMissingBaseRunner());
            return;
        }

        string dir = layout.BaseRunnerDir;

        if (!File.Exists(Path.Combine(dir, "ReproStudio.Runner.exe")))
        {
            findings.Fail("runner-base exists but has no ReproStudio.Runner.exe in it. "
                + "The bundle is incomplete - re-extract the zip.");
            return;
        }

        // The runner has to carry its own .NET, or a machine with no runtime installed
        // fails at launch with a confusing "framework not found" dialog.
        if (File.Exists(Path.Combine(dir, "coreclr.dll")))
        {
            Log.Ok("carries its own .NET");
        }
        else
        {
            findings.Fail("The base runner is not self-contained for .NET, so this machine needs "
                + ".NET 10 installed. Rebuild the bundle with pack.ps1.");
        }

        // Same story for WASDK: the whole version-overlay design depends on the native
        // DLLs sitting next to the exe rather than coming from an installed framework.
        if (File.Exists(Path.Combine(dir, "Microsoft.ui.xaml.dll")))
        {
            Log.Ok("carries its own Windows App SDK");
        }
        else
        {
            findings.Fail("The base runner has no Microsoft.ui.xaml.dll next to it, so it is "
                + "framework-dependent and expects an installed Windows App SDK. "
                + "Rebuild the bundle with pack.ps1.");
        }

        ReportResourceIndex(dir, findings);
    }

    /// <summary>
    /// Checks the resource index, which has broken this tool twice in two different ways.
    /// WinUI loads framework theme resources through a PRI file; if the right one is missing
    /// or the wrong one shadows it, every launch dies with "Cannot locate resource from
    /// 'ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml'" and nothing in the message
    /// points at the cause.
    /// </summary>
    private static void ReportResourceIndex(string dir, Findings findings)
    {
        string strayPri = Path.Combine(dir, "resources.pri");
        bool hasStray = File.Exists(strayPri);
        bool hasAppPri = File.Exists(Path.Combine(dir, "ReproStudio.Runner.pri"));

        if (hasStray)
        {
            findings.Fail("There is a stray 'resources.pri' in the base runner. It shadows "
                + "ReproStudio.Runner.pri, so the runner will crash on startup unable to find "
                + "themeresources.xaml. Delete it: " + strayPri);
        }

        if (!hasAppPri)
        {
            findings.Fail("ReproStudio.Runner.pri is missing from the base runner, so the runner "
                + "will crash on startup unable to find themeresources.xaml. That happens when the "
                + "runner is published instead of built - rebuild the bundle with pack.ps1.");
        }
        else if (!hasStray)
        {
            Log.Ok("resource index looks right");
        }
    }

    private static void ReportStorage(AppLayout layout, Findings findings)
    {
        Log.Field("cache", layout.CacheRoot,
            Environment.GetEnvironmentVariable(AppLayout.CacheRootVariable) is { Length: > 0 }
                ? "(from " + AppLayout.CacheRootVariable + ")"
                : null);

        string nupkgs = Path.Combine(layout.CacheRoot, "nupkgs");
        string versions = Path.Combine(layout.CacheRoot, "versions");

        Log.Field("downloads", Directory.Exists(nupkgs) ? Describe(nupkgs) : "none yet");

        // DirectoryInfo.Name is non-nullable, unlike Path.GetFileName.
        string[] provisioned = Directory.Exists(versions)
            ? new DirectoryInfo(versions).GetDirectories()
                .Select(d => d.Name)
                .Where(n => !n.EndsWith(".staging", StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        Log.Field("runners", provisioned.Length == 0 ? "none provisioned yet" : Describe(versions));
        foreach (string name in provisioned)
        {
            Log.Detail(name);
        }

        CheckWritable(layout.CacheRoot, findings);
        CheckFreeSpace(layout.CacheRoot, findings);
    }

    /// <summary>
    /// The cache is the only place this tool writes. A locked-down or redirected
    /// %LOCALAPPDATA% turns into an access-denied error deep inside provisioning, so it is
    /// worth proving up front that a file can actually be created.
    /// </summary>
    private static void CheckWritable(string cacheRoot, Findings findings)
    {
        try
        {
            Directory.CreateDirectory(cacheRoot);
            string probe = Path.Combine(cacheRoot, ".write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            Log.Ok("cache is writable");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            findings.Fail("Cannot write to the cache at " + cacheRoot + " (" + ex.Message.TrimEnd('.') + "). "
                + "Set " + AppLayout.CacheRootVariable + " to a folder you can write to.");
        }
    }

    private static void CheckFreeSpace(string cacheRoot, Findings findings)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(cacheRoot));
            if (root is not { Length: > 0 })
            {
                return;
            }

            long free = new DriveInfo(root).AvailableFreeSpace;
            Log.Field("free", Bytes(free), "keep about " + Bytes(BytesPerVersion) + " free per version");

            if (free < BytesPerVersion)
            {
                findings.Fail("Not enough free disk space to provision a runner. Free up space, or "
                    + "point " + AppLayout.CacheRootVariable + " at a bigger drive.");
            }
            else if (free < BytesPerVersion * 2)
            {
                findings.Warn("Only enough space for about one more runner. Each Windows App SDK "
                    + "version you try needs its own copy.");
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Field("free", "unknown");
        }
    }

    /// <summary>
    /// Windows App SDK versions come from NuGet on demand, so a blocked or proxied network
    /// stops you trying a new version. It is only a warning: versions already on disk still
    /// work, and a repro that pins a full version never needs the network at all.
    /// </summary>
    /// <summary>
    /// Checks NuGet the way provisioning does: through the configured sources, asking a
    /// real question. A hardcoded probe of nuget.org proves nothing on a machine whose
    /// nuget.config replaces the public feed, which is common inside Microsoft.
    /// </summary>
    private static async Task ReportNetworkAsync(Findings findings, CancellationToken ct)
    {
        using var feed = new NuGetFeed();

        foreach (string source in feed.Sources)
        {
            Log.Field("source", source);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(NetworkTimeout);

            IReadOnlyList<NuGetVersion> versions = await feed
                .ListVersionsAsync("Microsoft.WindowsAppSDK", includePrerelease: false, timeout.Token)
                .ConfigureAwait(false);

            if (versions.Count > 0)
            {
                Log.Ok($"NuGet answered, {versions.Count} Windows App SDK versions available");
                return;
            }

            findings.Warn("No Windows App SDK versions came back from any source. Versions already "
                + "provisioned still work; new ones cannot be downloaded. Check that a source "
                + "carrying Microsoft.WindowsAppSDK is enabled.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            findings.Warn("Could not reach NuGet (" + ex.Message.TrimEnd('.') + "). Versions already "
                + "provisioned still work, and a repro that pins a full version is fine offline, but "
                + "new versions cannot be downloaded.");
        }
    }

    private static void ReportDeveloperMode(Findings findings)
    {
        // Registering a loose-layout package is a sideload, which Windows only allows when
        // Developer Mode is on. Without it, packaged mode fails with 0x80073CFF.
        bool devMode = ReadRegistryValue(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock",
            "AllowDevelopmentWithoutDevLicense") == "1";

        Log.Field("identity", PackagedRunnerLauncher.HasIdentityAssets ? "present" : "MISSING");
        Log.Field("dev mode", devMode ? "on" : "off");

        // Only needed for '// packaged: yes', so neither of these is a hard failure.
        if (!PackagedRunnerLauncher.HasIdentityAssets)
        {
            findings.Warn("No RunnerIdentity\\Package.appxmanifest next to this exe (looked in "
                + PackagedRunnerLauncher.IdentitySourceDir + "), so '// packaged: yes' cannot work. "
                + "Unpackaged repros are unaffected.");
        }

        if (!devMode)
        {
            findings.Warn("Developer Mode is off, so '// packaged: yes' falls back to an unpackaged "
                + "launch. Turn it on at Settings > Privacy & security > For developers. "
                + "Unpackaged repros are unaffected.");
        }
        else if (PackagedRunnerLauncher.HasIdentityAssets)
        {
            Log.Ok("packaged mode should work");
        }
    }

    private static void ReportLogs()
    {
        string dir = Path.Combine(Path.GetTempPath(), "winui-repro-app");
        Log.Field("runner", Describe(Path.Combine(dir, "runner.log")));
        Log.Field("host", Describe(Path.Combine(dir, "host.log")));
    }

    /// <summary>
    /// The bottom line. Exit code is non-zero on a hard failure so a script can gate on it.
    /// </summary>
    private static int Verdict(Findings findings)
    {
        Log.Step("verdict");

        if (findings.Problems.Count == 0 && findings.Warnings.Count == 0)
        {
            Log.Ok("Ready to run. Try: ReproStudio.exe samples\\hello.cs");
            return 0;
        }

        foreach (string problem in findings.Problems)
        {
            Log.Error(problem);
        }

        foreach (string warning in findings.Warnings)
        {
            Log.Warn(warning);
        }

        Log.Blank();

        if (findings.Problems.Count == 0)
        {
            Log.Ok("Good enough to run, with the caveats above.");
            return 0;
        }

        Log.Error(findings.Problems.Count == 1
            ? "1 problem will stop the runner from working."
            : findings.Problems.Count + " problems will stop the runner from working.");
        return 1;
    }

    /// <summary>File size and age, or "none" when it has never been written.</summary>
    private static string Describe(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var files = new DirectoryInfo(path).GetFiles("*", SearchOption.AllDirectories);
                return $"{files.Length} files, {Bytes(files.Sum(f => f.Length))}";
            }

            if (!File.Exists(path))
            {
                return "none";
            }

            var info = new FileInfo(path);
            return $"{Bytes(info.Length)}, last written {info.LastWriteTime:yyyy-MM-dd HH:mm}";
        }
        catch (IOException)
        {
            return "unreadable";
        }
        catch (UnauthorizedAccessException)
        {
            return "access denied";
        }
    }

    private static string Bytes(long bytes)
    {
        double mb = bytes / 1024d / 1024d;
        return mb >= 1024
            ? (mb / 1024).ToString("0.#", CultureInfo.InvariantCulture) + " GB"
            : mb.ToString("0.#", CultureInfo.InvariantCulture) + " MB";
    }

    private static string? ReadRegistryValue(string subKey, string name)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(subKey);
            return key?.GetValue(name)?.ToString();
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the report found. A problem stops the runner working; a warning only limits what
    /// you can do (packaged mode, downloading a version you do not already have).
    /// </summary>
    private sealed class Findings
    {
        public List<string> Problems { get; } = [];

        public List<string> Warnings { get; } = [];

        public void Fail(string message) => Problems.Add(message);

        public void Warn(string message) => Warnings.Add(message);
    }
}
