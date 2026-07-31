using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace ReproStudio.Shared;

/// <summary>
/// Runs the (normally unpackaged) preview runner <em>with package identity</em> on demand,
/// so a repro can be tested packaged as well as unpackaged.
/// <para>
/// The runner is self-contained and code-only, so it resolves WinUI's own resources through
/// <c>ms-appx:///</c>. That only works when the whole runner folder is the package, so we
/// register the version folder itself as a loose-layout package rather than a sparse one - a
/// sparse package would leave <c>ms-appx:///Microsoft.UI.Xaml/...</c> unresolvable and crash
/// the runner on startup. Registration is done once per version; each launch then activates
/// the app by its AUMID, which is fast.
/// </para>
/// <para>
/// Registration goes through <see cref="PackageManager"/> in-process rather than the
/// <c>winapp</c> CLI, because <c>winapp</c> only exists on a machine with the Windows SDK
/// installed and this tool has to run from an xcopy'd folder. The runner's manifest uses only
/// literal strings and unqualified asset names, so no <c>resources.pri</c> - and therefore no
/// <c>makepri</c> - is needed either.
/// </para>
/// <para>
/// This needs Developer Mode (or sideloading) turned on, which is the one real prerequisite
/// packaged mode carries. When it is off, registration fails and the caller falls back to an
/// unpackaged launch with the reason in the status bar.
/// </para>
/// </summary>
public sealed class PackagedRunnerLauncher
{
    // These three must match src\ReproStudio.Host\RunnerIdentity\Package.appxmanifest.
    private const string PackageName = "ReproStudio.Runner";
    private const string PackagePublisher = "CN=AppPublisher";
    private const string ApplicationId = "ReproStudioRunner";

    /// <summary>The name a loose-layout package's manifest must have to be registered.</summary>
    private const string DeployedManifestName = "AppxManifest.xml";

    /// <summary>
    /// "To install this application you need either a Windows developer license or a
    /// sideloading-enabled system." Worth translating, because it is by far the most likely
    /// failure on a freshly imaged test machine.
    /// </summary>
    private const int ErrorDeploymentBlockedByPolicy = unchecked((int)0x80073CFF);

    private readonly string _manifestSourceDir;
    private readonly PackageManager _packageManager = new();

    private string? _registeredFolder;
    private string? _aumid;

    public PackagedRunnerLauncher() =>
        _manifestSourceDir = IdentitySourceDir;

    /// <summary>
    /// Where the identity manifest and its assets ship: a <c>RunnerIdentity</c> folder next
    /// to the running host. Public so a host can check it is there before offering packaged
    /// mode, instead of finding out at registration time.
    /// </summary>
    public static string IdentitySourceDir => Path.Combine(AppContext.BaseDirectory, "RunnerIdentity");

    /// <summary>True when the identity manifest is present, so packaged mode is possible.</summary>
    public static bool HasIdentityAssets => File.Exists(Path.Combine(IdentitySourceDir, "Package.appxmanifest"));

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

        try
        {
            // The version folder IS the package root, so the manifest and its assets have to live
            // there. Both are inert for a plain CreateProcess, so leaving them in place while
            // registered does not affect an unpackaged launch from the same folder.
            string manifestPath = StageManifest(versionFolder);

            DeploymentResult result = await _packageManager
                .RegisterPackageAsync(new Uri(manifestPath), null, DeploymentOptions.DevelopmentMode)
                .AsTask()
                .ConfigureAwait(false);

            if (result.ExtendedErrorCode is not null)
            {
                return new RegisterResult(false, string.Empty, Explain(result.ExtendedErrorCode));
            }

            string? familyName = FindPackage()?.Id.FamilyName;
            if (familyName is null)
            {
                return new RegisterResult(false, string.Empty, "The package registered but could not be found.");
            }

            _registeredFolder = versionFolder;
            _aumid = familyName + "!" + ApplicationId;
            return new RegisterResult(true, _aumid, string.Empty);
        }
#pragma warning disable CA1031 // Surface a staging/registration failure to the caller.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new RegisterResult(false, string.Empty, Explain(ex));
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
    /// deletes the manifest and assets we staged into the version folder. A no-op if we never
    /// registered one this session.
    /// </summary>
    public async Task UnregisterAsync()
    {
        if (_registeredFolder is null)
        {
            return;
        }

        string folder = _registeredFolder;
        _registeredFolder = null;
        _aumid = null;

        try
        {
            Package? package = FindPackage();
            if (package is not null)
            {
                await _packageManager
                    .RemovePackageAsync(package.Id.FullName, RemovalOptions.None)
                    .AsTask()
                    .ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // A stale dev registration is harmless and gets replaced next run.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        UnstageManifest(folder);
    }

    /// <summary>
    /// Copies the manifest and its assets into the version folder (the package root) and returns
    /// the staged manifest path. It is written as <c>AppxManifest.xml</c> because that is the name
    /// a loose-layout registration expects; the source is a <c>.appxmanifest</c> only by project
    /// convention, and its contents need no transformation (no <c>$targetnametoken$</c>, no
    /// <c>ms-resource:</c> indirection).
    /// </summary>
    private string StageManifest(string versionFolder)
    {
        string manifestDest = Path.Combine(versionFolder, DeployedManifestName);
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

    /// <summary>Removes what <see cref="StageManifest"/> put in the version folder.</summary>
    private static void UnstageManifest(string versionFolder)
    {
        TryDeleteFile(Path.Combine(versionFolder, DeployedManifestName));
        TryDeleteDirectory(Path.Combine(versionFolder, "Assets"));
    }

    private Package? FindPackage() =>
        _packageManager.FindPackagesForUser(string.Empty, PackageName, PackagePublisher).FirstOrDefault();

    /// <summary>
    /// Turns a deployment failure into something actionable. Developer Mode being off is the
    /// common case on a freshly imaged test machine, and its raw HRESULT says nothing useful.
    /// </summary>
    private static string Explain(Exception ex) =>
        ex.HResult == ErrorDeploymentBlockedByPolicy
            ? "Developer Mode is off. Turn it on in Settings > System > For developers, or uncheck Packaged."
            : ex.Message;

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
