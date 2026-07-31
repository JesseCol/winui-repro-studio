using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace ReproStudio_Host.Services;

/// <summary>
/// Runs the (normally unpackaged) preview runner <em>with package identity</em> on demand,
/// so a repro can be tested packaged as well as unpackaged.
/// <para>
/// The runner is self-contained and code-only, so it resolves WinUI's own resources through
/// <c>ms-appx:///</c>. That only works when the whole runner folder is the package, so we use
/// a loose-layout package (via the <c>winapp</c> CLI's <c>run</c> command) rather than a sparse
/// one - a sparse package would leave <c>ms-appx:///Microsoft.UI.Xaml/...</c> unresolvable and
/// crash the runner on startup. Registration is done once per version (it stages a package
/// layout, which is slow); each launch then activates the app by its AUMID, which is fast.
/// </para>
/// </summary>
public sealed class PackagedRunnerLauncher
{
    private readonly string _workingDir;
    private readonly string _appxLayoutDir;
    private readonly string _manifestSourceDir;

    private string? _registeredFolder;
    private string? _aumid;

    /// <summary>
    /// Creates a launcher. <paramref name="cacheRoot"/> is the app's writable cache root;
    /// the winapp working directory and the shared package layout live underneath it (a
    /// packaged host's real working directory may be read-only).
    /// </summary>
    public PackagedRunnerLauncher(string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheRoot);
        _workingDir = Path.Combine(cacheRoot, "runner-identity");
        _appxLayoutDir = Path.Combine(cacheRoot, "runner-appx");
        _manifestSourceDir = Path.Combine(AppContext.BaseDirectory, "RunnerIdentity");
    }

    /// <summary>Outcome of registering a version folder as the packaged runner.</summary>
    public readonly record struct RegisterResult(bool Success, string Aumid, string Message);

    /// <summary>True while a packaged runner is registered (so callers know to unregister).</summary>
    public bool IsRegistered => _registeredFolder is not null;

    /// <summary>
    /// Makes sure the runner in <paramref name="versionFolder"/> is registered as a loose-layout
    /// package and returns its AUMID. Re-registering the same folder is skipped (it is expensive),
    /// so a relaunch only pays for AUMID activation. Registering a different folder replaces the
    /// previous package (there is only ever one packaged runner).
    /// </summary>
    public async Task<RegisterResult> EnsureRegisteredAsync(string versionFolder)
    {
        ArgumentException.ThrowIfNullOrEmpty(versionFolder);

        if (_aumid is not null && string.Equals(_registeredFolder, versionFolder, StringComparison.OrdinalIgnoreCase))
        {
            return new RegisterResult(true, _aumid, string.Empty);
        }

        // winapp indexes the input folder in place (writing a resources.pri) and needs the manifest +
        // assets copied there. Those byproducts would break a later *unpackaged* launch from the same
        // folder, so we restore the folder afterwards. The registered package runs from its own staged
        // copy under _appxLayoutDir, so cleaning the source folder does not affect it.
        bool hadResourcesPri = File.Exists(Path.Combine(versionFolder, "resources.pri"));
        try
        {
            string manifestPath = StageManifest(versionFolder);

            (int exitCode, string output) = await RunWinappAsync(
                "run",
                versionFolder,
                "--manifest",
                manifestPath,
                "--output-appx-directory",
                _appxLayoutDir,
                "--no-launch",
                "--json").ConfigureAwait(false);

            if (exitCode != 0)
            {
                return new RegisterResult(false, string.Empty, LastMeaningfulLine(output));
            }

            string? aumid = ParseAumid(output);
            if (aumid is null)
            {
                return new RegisterResult(false, string.Empty, "winapp did not report an AUMID.");
            }

            _registeredFolder = versionFolder;
            _aumid = aumid;
            return new RegisterResult(true, aumid, string.Empty);
        }
#pragma warning disable CA1031 // Surface a staging/registration failure to the caller.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new RegisterResult(false, string.Empty, ex.Message);
        }
        finally
        {
            RestoreVersionFolder(versionFolder, hadResourcesPri);
        }
    }

    /// <summary>
    /// Activates the packaged runner by AUMID with the given command-line arguments and returns
    /// the launched process id, or null if activation failed.
    /// </summary>
    public int? Activate(string aumid, string arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(aumid);
        try
        {
            var manager = (IApplicationActivationManager)new ApplicationActivationManager();
            manager.ActivateApplication(aumid, arguments, ActivateOptions.None, out uint processId);
            return (int)processId;
        }
#pragma warning disable CA1031 // Surface an activation failure by returning null.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>
    /// Removes the packaged runner registration (so the runner launches unpackaged again) and
    /// deletes its staged layout. A no-op if we never registered one this session.
    /// </summary>
    public async Task UnregisterAsync()
    {
        if (_registeredFolder is null)
        {
            return;
        }

        string manifestPath = Path.Combine(_manifestSourceDir, "Package.appxmanifest");
        await RunWinappAsync("unregister", "--manifest", manifestPath, "--force").ConfigureAwait(false);

        _registeredFolder = null;
        _aumid = null;

        try
        {
            if (Directory.Exists(_appxLayoutDir))
            {
                Directory.Delete(_appxLayoutDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // The just-unregistered package may still be releasing file handles; leave it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above - a stale layout folder is harmless.
        }
    }

    /// <summary>Copies the manifest and its assets into the version folder (the package root).</summary>
    private string StageManifest(string versionFolder)
    {
        string manifestDest = Path.Combine(versionFolder, "Package.appxmanifest");
        File.Copy(Path.Combine(_manifestSourceDir, "Package.appxmanifest"), manifestDest, overwrite: true);

        string assetsSource = Path.Combine(_manifestSourceDir, "Assets");
        string assetsDest = Path.Combine(versionFolder, "Assets");
        Directory.CreateDirectory(assetsDest);
        foreach (string file in Directory.GetFiles(assetsSource))
        {
            File.Copy(file, Path.Combine(assetsDest, Path.GetFileName(file)), overwrite: true);
        }

        return manifestDest;
    }

    /// <summary>
    /// Removes the packaging byproducts from the source folder (the copied manifest and assets, and
    /// winapp's generated resources.pri) so an unpackaged launch from that same folder still works.
    /// </summary>
    private static void RestoreVersionFolder(string versionFolder, bool hadResourcesPri)
    {
        TryDeleteFile(Path.Combine(versionFolder, "Package.appxmanifest"));
        TryDeleteDirectory(Path.Combine(versionFolder, "Assets"));
        if (!hadResourcesPri)
        {
            TryDeleteFile(Path.Combine(versionFolder, "resources.pri"));
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover packaging file is tolerable; don't fail the launch over it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Same as TryDeleteFile: a leftover Assets folder is harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task<(int ExitCode, string Output)> RunWinappAsync(params string[] arguments)
    {
        Directory.CreateDirectory(_workingDir);

        var startInfo = new ProcessStartInfo
        {
            FileName = "winapp",
            WorkingDirectory = _workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return (-1, "Could not start the winapp CLI.");
            }

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            string output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
            return (process.ExitCode, output);
        }
#pragma warning disable CA1031 // Surface any winapp failure (e.g. not installed) to the caller.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return (-1, ex.Message);
        }
    }

    private static string? ParseAumid(string output)
    {
        int start = output.IndexOf('{');
        int end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(output[start..(end + 1)]);
            return doc.RootElement.TryGetProperty("AUMID", out JsonElement aumid)
                ? aumid.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The final non-blank line of winapp output is its outcome (success or error).</summary>
    private static string LastMeaningfulLine(string output)
    {
        string[] lines = output.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.Length > 0)
            {
                return line;
            }
        }

        return string.Empty;
    }

    private enum ActivateOptions
    {
        None = 0,
    }

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        IntPtr ActivateApplication(
            [In] string appUserModelId,
            [In] string? arguments,
            [In] ActivateOptions options,
            [Out] out uint processId);
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager
    {
    }
}
