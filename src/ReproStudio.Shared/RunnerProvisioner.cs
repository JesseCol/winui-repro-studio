using System.IO.Compression;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace ReproStudio.Shared;

/// <summary>
/// Reports a step of provisioning so the UI can show progress.
/// </summary>
public sealed class ProvisionProgress
{
    public required string Message { get; init; }
}

/// <summary>
/// Gets a self-contained runner ready for a chosen Windows App SDK version without
/// building per version. It starts from a prebuilt code-only base runner, then
/// copies that version's loose runtime files (downloaded from NuGet and unzipped)
/// over a copy of the base. Switching versions is just files on disk - no MSBuild.
/// </summary>
public sealed class RunnerProvisioner : IDisposable
{
    private const string MetapackageId = "Microsoft.WindowsAppSDK";
    private const string ComponentPrefix = MetapackageId + ".";
    private const string WinUiComponentId = MetapackageId + ".WinUI";
    private const string RuntimeIdentifier = "win-x64";

    /// <summary>
    /// File names inside an MSIX that are packaging metadata, not payload. Everything
    /// else in the archive (the DLLs, PRIs, winmds, resource folders) is copied out.
    /// </summary>
    private static readonly HashSet<string> MsixPackagingFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppxManifest.xml",
        "AppxBlockMap.xml",
        "AppxSignature.p7x",
        "[Content_Types].xml",
    };

    private readonly NuGetFeed _feed;
    private readonly string _nupkgCache;
    private readonly string _versionsRoot;
    private readonly string _localCache;

    /// <param name="cacheRoot">Where downloaded packages and provisioned runners live.</param>
    /// <param name="settingsRoot">
    /// Directory to start the nuget.config search from, usually the folder holding the
    /// repro file. Lets a repro sit next to a nuget.config naming the feed it needs.
    /// </param>
    public RunnerProvisioner(string cacheRoot, string? settingsRoot = null)
    {
        ArgumentNullException.ThrowIfNull(cacheRoot);
        _feed = new NuGetFeed(settingsRoot);
        _nupkgCache = Path.Combine(cacheRoot, "nupkgs");
        _versionsRoot = Path.Combine(cacheRoot, "versions");
        _localCache = Path.Combine(cacheRoot, "local-winui");
    }

    /// <summary>Package sources in use, for logs and diagnostics.</summary>
    public IReadOnlyList<string> Sources => _feed.Sources;

    public void Dispose() => _feed.Dispose();

    /// <summary>
    /// Windows App SDK versions from NuGet, newest first. Prerelease versions
    /// (e.g. experimental/preview builds) are skipped unless <paramref name="includePrerelease"/>
    /// is true.
    /// </summary>
    public Task<IReadOnlyList<string>> ListWasdkVersionsAsync(
        bool includePrerelease = false,
        CancellationToken ct = default) =>
        ListPackageVersionsAsync(MetapackageId, includePrerelease, ct);

    /// <summary>
    /// WinUI component versions from NuGet, newest first. Prerelease versions are
    /// skipped unless <paramref name="includePrerelease"/> is true.
    /// </summary>
    public Task<IReadOnlyList<string>> ListWinUiVersionsAsync(
        bool includePrerelease = false,
        CancellationToken ct = default) =>
        ListPackageVersionsAsync(WinUiComponentId, includePrerelease, ct);

    private async Task<IReadOnlyList<string>> ListPackageVersionsAsync(
        string id,
        bool includePrerelease,
        CancellationToken ct)
    {
        IReadOnlyList<NuGetVersion> versions =
            await _feed.ListVersionsAsync(id, includePrerelease, ct).ConfigureAwait(false);
        return versions.Select(v => v.ToNormalizedString()).ToList();
    }

    /// <summary>Path to the request file the host writes for the runner to watch.</summary>
    public string GetVersionFolder(string version) => Path.Combine(_versionsRoot, version);

    /// <summary>
    /// Clears the provisioned per-version runners and any local WinUI overrides so
    /// they are rebuilt fresh on next use. Downloaded NuGet packages are kept, so
    /// re-provisioning is fast (no re-download). Stop any running runner first, or the
    /// delete will retry against its locked files.
    /// </summary>
    public void ClearProvisionedRunners()
    {
        DeleteWithRetry(_versionsRoot);
        DeleteWithRetry(_localCache);
    }

    private static void DeleteWithRetry(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                // A just-stopped runner may still be releasing file handles.
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException) when (attempt < 10)
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>
    /// Makes sure a runner is on disk and returns the exe path. Copies the base runner,
    /// then overlays runtime files.
    ///
    /// Give <paramref name="version"/> for a stock Windows App SDK version. Give
    /// <paramref name="winui"/> to swap the WinUI component for a different NuGet version
    /// or a local .nupkg. Give both and the Windows App SDK version supplies the
    /// components WinUI does not need (AI, ML, Widgets, DWrite) while the WinUI package
    /// wins on anything it does declare. Give only <paramref name="winui"/> and the
    /// package's own declared dependencies decide the whole stack.
    ///
    /// An optional <paramref name="payload"/> copies loose local files over the lot,
    /// which is the quickest way to test a private build of any runtime binary.
    /// </summary>
    public async Task<string> EnsureRunnerAsync(
        string? version,
        string baseRunnerDir,
        WinUiOverride? winui = null,
        RunnerPayload? payload = null,
        IProgress<ProvisionProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseRunnerDir);
        if (version is null && winui is null)
        {
            throw new ArgumentException(
                "Give a Windows App SDK version, a WinUI override, or both.",
                nameof(version));
        }

        string folderName = (version, winui) switch
        {
            (null, not null) => winui.CacheKey,
            (not null, null) => version,
            _ => $"{version}__{winui!.CacheKey}",
        };
        if (payload is not null)
        {
            // A separate folder, so runs without a payload keep using untouched stock
            // bits. The name is fixed rather than per-fingerprint: dropping in a new
            // build should replace the previous one, not leave a 350 MB folder behind
            // for every iteration.
            folderName += "+payload";
        }

        string dest = Path.Combine(_versionsRoot, folderName);
        string exe = Path.Combine(dest, "ReproStudio.Runner.exe");
        if (File.Exists(exe))
        {
            if (!RunnerPayload.Matches(dest, payload))
            {
                // Overlaid files cannot be un-overlaid in place - restoring a stock DLL
                // would mean knowing what it used to be - so a changed payload rebuilds
                // the folder. Downloads are cached, so this is local copying only.
                Report(progress, "Payload changed, re-provisioning...");
                DeleteWithRetry(dest);
            }
            else if (IsBaseRunnerNewer(baseRunnerDir, dest))
            {
                // The runner was rebuilt after this folder was provisioned. Without this,
                // a runner fix never reaches versions provisioned earlier - they keep
                // serving the old binary forever, and the bug looks like it came back.
                Report(progress, $"Base runner changed, re-provisioning {version}...");
                DeleteWithRetry(dest);
            }
            else
            {
                return exe;
            }
        }

        string staging = dest + ".staging";
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Report(progress, version is null
            ? "Preparing runner..."
            : $"Preparing runner for WASDK {version}...");
        CopyDirectory(baseRunnerDir, staging);

        IReadOnlyList<(string Id, NuGetVersion Version)> components;
        if (version is null)
        {
            // WinUI-driven. The package declares the stack it expects, so fetch exactly that
            // closure. The base runner's own Windows App SDK payload stays underneath and
            // supplies the parts WinUI does not declare (DWrite, AI, ML, Widgets), which a
            // XAML repro does not touch. What gets overwritten is the whole XAML stack.
            Report(progress, "Resolving components from the WinUI package...");
            components = await ResolveWinUiClosureAsync(winui!, ct).ConfigureAwait(false);
        }
        else
        {
            Report(progress, $"Resolving components for {version}...");
            components = await ResolveComponentsAsync(version, ct).ConfigureAwait(false);

            if (winui is not null && components.Count > 0)
            {
                components = await RaiseFloorsForWinUiAsync(components, winui, progress, ct).ConfigureAwait(false);
            }
        }

        if (components.Count == 0)
        {
            // Older layout (WASDK 1.7 and earlier): the metapackage has no component
            // dependencies and carries the runtime itself. The native framework payload
            // is zipped inside a .msix under tools\MSIX; the loose native sits alongside.
            // We detect this by the absence of components, not a version number, so the
            // 1.7 -> 1.8 cutover (and any future one) is handled automatically.
            Report(progress, $"Extracting Windows App SDK {version} runtime...");
            string metaDir = await EnsurePackageAsync(MetapackageId, version!, ct).ConfigureAwait(false);
            CopyComponentNativeFiles(metaDir, staging);
            ExtractFrameworkMsix(metaDir, staging);
        }
        else
        {
            // Newer layout (WASDK 1.8+): the runtime lives in component sub-packages
            // (Foundation, WinUI, Runtime, ...), each with loose self-contained files.
            foreach ((string id, NuGetVersion componentVersion) in components)
            {
                // When overriding WinUI, skip the resolved WinUI so the override wins.
                if (winui is not null && id.Equals(WinUiComponentId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Report(progress, $"Fetching {id} {componentVersion.ToNormalizedString()}...");
                string pkgDir = await EnsurePackageAsync(id, componentVersion, ct).ConfigureAwait(false);
                CopyComponentNativeFiles(pkgDir, staging);
            }
        }

        if (winui is not null)
        {
            await ApplyWinUiOverrideAsync(winui, staging, progress, ct);
        }

        if (payload is not null)
        {
            // Last, so a dropped file beats both the stock component and any WinUI override.
            Report(progress, $"Applying {payload.RelativePaths.Count} file(s) from {payload.Directory}...");
            payload.ApplyTo(staging);
        }

        // Atomic-ish swap: stage fully, then move into place.
        MoveWithRetry(staging, dest);
        return exe;
    }

    /// <summary>
    /// True when the base runner has been rebuilt since this version folder was
    /// provisioned. File copies preserve last-write time, so a mismatch on the runner
    /// assembly means the base moved on and this folder is serving a stale binary.
    /// </summary>
    private static bool IsBaseRunnerNewer(string baseRunnerDir, string dest)
    {
        const string marker = "ReproStudio.Runner.dll";
        string baseDll = Path.Combine(baseRunnerDir, marker);
        string copiedDll = Path.Combine(dest, marker);

        if (!File.Exists(baseDll))
        {
            // No base to compare against, so keep whatever is already provisioned.
            return false;
        }

        return !File.Exists(copiedDll)
            || File.GetLastWriteTimeUtc(baseDll) != File.GetLastWriteTimeUtc(copiedDll);
    }

    /// <summary>
    /// The full component set a WinUI override needs, from its own declared dependencies.
    /// </summary>
    private async Task<IReadOnlyList<(string Id, NuGetVersion Version)>> ResolveWinUiClosureAsync(
        WinUiOverride winui,
        CancellationToken ct)
    {
        var pkg = await ReadWinUiPackageAsync(winui, ct).ConfigureAwait(false);
        if (pkg is null)
        {
            throw new InvalidOperationException(
                $"{Describe(winui)} declares no Windows App SDK dependencies, so it cannot decide "
                + "the stack on its own. Pass a Windows App SDK version as well, or build the "
                + "package with the WinUI repo's 'build.cmd /version <version>', which stamps the "
                + "Base, Foundation and InteractiveExperiences versions it was built against.");
        }

        return await ResolveClosureAsync(pkg.Value.Id, pkg.Value.Version, pkg.Value.Dependencies, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Raises any Windows App SDK component below what the WinUI override asks for, so a
    /// WinUI build lands on the stack it was compiled against. The Windows App SDK version
    /// still supplies everything WinUI does not declare (AI, ML, Widgets, DWrite).
    ///
    /// This is the check that was missing when a WinUI build compiled against Foundation
    /// 3.0.0 was dropped onto Windows App SDK 2.3.1, which provides 2.3.5. Nothing
    /// complained, and the mismatch surfaced days later as an unexplained E_NOINTERFACE.
    /// </summary>
    private async Task<IReadOnlyList<(string Id, NuGetVersion Version)>> RaiseFloorsForWinUiAsync(
        IReadOnlyList<(string Id, NuGetVersion Version)> components,
        WinUiOverride winui,
        IProgress<ProvisionProgress>? progress,
        CancellationToken ct)
    {
        var pkg = await ReadWinUiPackageAsync(winui, ct).ConfigureAwait(false);
        if (pkg is null)
        {
            // Nothing declared, so nothing to check. Shape-only packages land here.
            return components;
        }

        IReadOnlyList<(string Id, NuGetVersion Version)> closure =
            await ResolveClosureAsync(pkg.Value.Id, pkg.Value.Version, pkg.Value.Dependencies, ct)
                .ConfigureAwait(false);

        var merged = components.ToDictionary(c => c.Id, c => c.Version, StringComparer.OrdinalIgnoreCase);
        foreach ((string id, NuGetVersion required) in closure)
        {
            if (id.Equals(WinUiComponentId, StringComparison.OrdinalIgnoreCase))
            {
                // The override supplies WinUI itself; its own version is not a constraint.
                continue;
            }

            if (!merged.TryGetValue(id, out NuGetVersion? provided))
            {
                Report(progress, $"{Describe(winui)} needs {id} {required.ToNormalizedString()}, adding it.");
                merged[id] = required;
            }
            else if (required > provided)
            {
                Report(
                    progress,
                    $"{Describe(winui)} needs {id} {required.ToNormalizedString()}, "
                    + $"but this Windows App SDK provides {provided.ToNormalizedString()}. Raising it.");
                merged[id] = required;
            }
        }

        return merged
            .Select(kv => (Id: kv.Key, Version: kv.Value))
            .OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Describe(WinUiOverride winui) =>
        winui.LocalNupkgPath is not null
            ? Path.GetFileName(winui.LocalNupkgPath)
            : "WinUI " + winui.NuGetVersion;

    private async Task ApplyWinUiOverrideAsync(
        WinUiOverride winui,
        string staging,
        IProgress<ProvisionProgress>? progress,
        CancellationToken ct)
    {
        string pkgDir;
        if (winui.LocalNupkgPath is not null)
        {
            Report(progress, $"Applying local WinUI package {Path.GetFileName(winui.LocalNupkgPath)}...");
            pkgDir = ExtractLocalNupkg(winui.LocalNupkgPath);
        }
        else
        {
            Report(progress, $"Fetching WinUI {winui.NuGetVersion}...");
            pkgDir = await EnsurePackageAsync(WinUiComponentId, winui.NuGetVersion!, ct);
        }

        CopyComponentNativeFiles(pkgDir, staging);
    }

    private string ExtractLocalNupkg(string nupkgPath)
    {
        string dir = Path.Combine(_localCache, WinUiOverride.HashFile(nupkgPath));
        if (Directory.Exists(dir))
        {
            return dir;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        string temp = dir + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            ZipFile.ExtractToDirectory(nupkgPath, temp);
            MoveWithRetry(temp, dir);
        }
        catch
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }

            throw;
        }

        return dir;
    }

    /// <summary>
    /// The components a Windows App SDK metapackage version is made of. Empty for the
    /// older layout (1.7 and earlier), where the metapackage carries the runtime itself.
    /// </summary>
    private async Task<IReadOnlyList<(string Id, NuGetVersion Version)>> ResolveComponentsAsync(
        string version,
        CancellationToken ct)
    {
        if (!NuGetVersion.TryParse(version, out NuGetVersion? parsed))
        {
            throw new InvalidOperationException($"'{version}' is not a valid Windows App SDK version.");
        }

        IReadOnlyList<PackageDependency> deps =
            await _feed.GetDependenciesAsync(MetapackageId, parsed, ComponentPrefix, ct).ConfigureAwait(false);

        var components = new List<(string Id, NuGetVersion Version)>(deps.Count);
        foreach (PackageDependency dep in deps)
        {
            NuGetVersion? resolved = await _feed
                .ResolveVersionAsync(dep.Id, dep.VersionRange, ct).ConfigureAwait(false);
            if (resolved is null)
            {
                throw new InvalidOperationException(
                    $"No version of {dep.Id} satisfies {dep.VersionRange.PrettyPrint()}, "
                    + $"which Windows App SDK {version} asks for. Tried: "
                    + string.Join(", ", _feed.Sources) + ".");
            }

            components.Add((dep.Id, resolved));
        }

        return components;
    }

    /// <summary>
    /// Walks a package's Windows App SDK dependencies transitively and returns everything
    /// needed to run it, the root included.
    ///
    /// This is what makes a WinUI package able to stand on its own. WinUI declares Base,
    /// Foundation and InteractiveExperiences; Foundation declares Base and
    /// InteractiveExperiences; and Base declares nothing. The closure is those four, and
    /// it is complete - Runtime and Base carry no binaries at all, and nothing in the XAML
    /// stack references the one binary DWrite contributes.
    ///
    /// A bare NuGet version is a floor, not a pin, so when two packages ask for different
    /// versions of the same component the higher ask wins. That is also why this cannot be
    /// an equality check: a healthy Windows App SDK 2.3.1 ships Foundation 2.3.5 while its
    /// own WinUI only asks for 2.3.1.
    ///
    /// A floor is also not necessarily a version anyone published. WinUI 2.3.0 asks for
    /// Foundation &gt;= 2.3.1, and there is no Foundation 2.3.1 - the first one that
    /// satisfies it is 2.3.5. Every range therefore goes through the resolver rather than
    /// being read as a version to fetch.
    /// </summary>
    private async Task<IReadOnlyList<(string Id, NuGetVersion Version)>> ResolveClosureAsync(
        string rootId,
        NuGetVersion rootVersion,
        IReadOnlyList<PackageDependency> rootDependencies,
        CancellationToken ct)
    {
        // The root is pinned: the user picked that exact build, so nothing may move it.
        var resolved = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase)
        {
            [rootId] = rootVersion,
        };

        var floors = new Dictionary<string, VersionRange>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        foreach (PackageDependency dep in rootDependencies)
        {
            if (dep.Id.Equals(rootId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            floors[dep.Id] = dep.VersionRange;
            queue.Enqueue(dep.Id);
        }

        while (queue.Count > 0)
        {
            string id = queue.Dequeue();
            VersionRange range = floors[id];

            NuGetVersion? version = await _feed.ResolveVersionAsync(id, range, ct).ConfigureAwait(false);
            if (version is null)
            {
                throw new InvalidOperationException(
                    $"No version of {id} satisfies {range.PrettyPrint()}. Tried: "
                    + string.Join(", ", _feed.Sources)
                    + ". Experimental component builds usually live on an internal feed - "
                    + "add it to a nuget.config next to the repro file.");
            }

            if (resolved.TryGetValue(id, out NuGetVersion? already) && already == version)
            {
                // A raised floor that lands on the same version teaches us nothing new.
                continue;
            }

            resolved[id] = version;

            IReadOnlyList<PackageDependency> deps = await _feed
                .GetDependenciesAsync(id, version, ComponentPrefix, ct).ConfigureAwait(false);

            foreach (PackageDependency dep in deps)
            {
                if (dep.Id.Equals(rootId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Re-queue on a raised floor as well as on a first sighting: a higher
                // version can pull in dependencies the lower one never had.
                if (!floors.TryGetValue(dep.Id, out VersionRange? known)
                    || IsHigherFloor(dep.VersionRange, known))
                {
                    floors[dep.Id] = dep.VersionRange;
                    queue.Enqueue(dep.Id);
                }
            }
        }

        return resolved
            .Select(kv => (Id: kv.Key, Version: kv.Value))
            .OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsHigherFloor(VersionRange candidate, VersionRange known) =>
        candidate.MinVersion is { } a && (known.MinVersion is not { } b || a > b);

    /// <summary>
    /// What a WinUI override says it needs. A package built by the WinUI repo's
    /// <c>build.cmd /version</c> declares its real Base, Foundation and
    /// InteractiveExperiences versions, so it fully describes the stack it expects.
    /// Returns null when the package declares nothing, which is the case for the
    /// shape-only packages <c>tools\pack-local-winui.ps1</c> produces.
    /// </summary>
    private async Task<(string Id, NuGetVersion Version, IReadOnlyList<PackageDependency> Dependencies)?>
        ReadWinUiPackageAsync(WinUiOverride winui, CancellationToken ct)
    {
        if (winui.LocalNupkgPath is null)
        {
            if (!NuGetVersion.TryParse(winui.NuGetVersion!, out NuGetVersion? parsed))
            {
                return null;
            }

            IReadOnlyList<PackageDependency> feedDeps = await _feed
                .GetDependenciesAsync(WinUiComponentId, parsed, ComponentPrefix, ct).ConfigureAwait(false);
            return feedDeps.Count == 0 ? null : (WinUiComponentId, parsed, feedDeps);
        }

        using var reader = new PackageArchiveReader(winui.LocalNupkgPath);

        PackageIdentity identity;
        IReadOnlyList<PackageDependency> deps;
        try
        {
            identity = reader.GetIdentity();
            deps = NuGetFeed.FilterDependencies(
                reader.GetPackageDependencies().SelectMany(g => g.Packages),
                ComponentPrefix);
        }
        catch (PackagingException)
        {
            // No nuspec at all. Shape-only packages land here, and "declares nothing"
            // is the answer the callers want - they have a much better message for it
            // than NuGet's raw "missing the required nuspec file".
            return null;
        }

        return deps.Count == 0 ? null : (identity.Id, identity.Version, deps);
    }

    private async Task<string> EnsurePackageAsync(string id, string version, CancellationToken ct)
    {
        if (!NuGetVersion.TryParse(version, out NuGetVersion? parsed))
        {
            throw new InvalidOperationException($"'{version}' is not a valid NuGet version for {id}.");
        }

        return await EnsurePackageAsync(id, parsed, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads and unzips a package into the cache, returning its folder. The folder
    /// name uses the normalized version so a single package cannot land twice under two
    /// spellings of the same version.
    /// </summary>
    private async Task<string> EnsurePackageAsync(string id, NuGetVersion version, CancellationToken ct)
    {
        string dir = Path.Combine(_nupkgCache, id.ToLowerInvariant(), version.ToNormalizedString());
        if (Directory.Exists(dir))
        {
            return dir;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        string temp = dir + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var buffer = new MemoryStream())
            {
                if (!await _feed.TryDownloadAsync(id, version, buffer, ct).ConfigureAwait(false))
                {
                    // Loud on purpose. Silently carrying on with a different version is how a
                    // WinUI build compiled against Foundation 3.0.0 ended up running on 2.3.5,
                    // which failed much later as an unexplained E_NOINTERFACE.
                    throw new InvalidOperationException(
                        $"{id} {version.ToNormalizedString()} was not found on any package source. "
                        + $"Tried: {string.Join(", ", _feed.Sources)}. "
                        + "Experimental component builds usually live on an internal feed - "
                        + "add it to a nuget.config next to the repro file.");
                }

                buffer.Position = 0;
                using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
                archive.ExtractToDirectory(temp);
            }

            MoveWithRetry(temp, dir);
        }
        catch
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }

            throw;
        }

        return dir;
    }

    /// <summary>
    /// Moves a freshly extracted directory into place, retrying briefly. Antivirus
    /// often scans (and momentarily locks) just-written files, which can make the
    /// move fail with an access-denied IOException on the first try.
    /// </summary>
    private static void MoveWithRetry(string source, string dest)
    {
        const int attempts = 10;
        for (int attempt = 1; ; attempt++)
        {
            // Another provisioning run may have finished this package first.
            if (Directory.Exists(dest))
            {
                Directory.Delete(source, recursive: true);
                return;
            }

            try
            {
                Directory.Move(source, dest);
                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException) when (attempt < attempts)
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>
    /// Overlays a package's native self-contained runtime (the versioned WinUI and
    /// WindowsAppRuntime DLLs, PRIs, and localized resources) into the runner folder.
    /// Only native files are copied - the managed WinUI projection assemblies stay at
    /// the base runner's build version so they keep matching the runner's compiled-in
    /// assembly references (mixing in a different version's managed assemblies fails
    /// to load). WinRT ABI stability lets those fixed managed projections drive the
    /// swapped-in native DLLs of a different WASDK version.
    /// </summary>
    private static void CopyComponentNativeFiles(string pkgDir, string dest)
    {
        CopyTreeIfExists(Path.Combine(pkgDir, "runtimes-framework", RuntimeIdentifier, "native"), dest);
        CopyTreeIfExists(Path.Combine(pkgDir, "runtimes", RuntimeIdentifier, "native"), dest);
    }

    /// <summary>
    /// Extracts the self-contained native runtime from the metapackage's framework
    /// MSIX (WASDK 1.7 and earlier). The framework package for our RID lives under
    /// tools\MSIX\win10-&lt;arch&gt;; its payload is the real versioned WinUI and
    /// WindowsAppRuntime DLLs. The DDLM/Main/Singleton packages next to it are
    /// packaged-deployment infrastructure a self-contained app does not need.
    /// </summary>
    private static void ExtractFrameworkMsix(string pkgDir, string dest)
    {
        string arch = RuntimeIdentifier["win-".Length..];
        string msixDir = Path.Combine(pkgDir, "tools", "MSIX", "win10-" + arch);
        if (!Directory.Exists(msixDir))
        {
            return;
        }

        string? framework = Directory.GetFiles(msixDir, "*.msix").FirstOrDefault(IsFrameworkMsix);
        if (framework is not null)
        {
            ExtractMsixPayload(framework, dest);
        }
    }

    private static bool IsFrameworkMsix(string path)
    {
        string name = Path.GetFileName(path);
        return !name.Contains(".DDLM.", StringComparison.OrdinalIgnoreCase)
            && !name.Contains(".Main.", StringComparison.OrdinalIgnoreCase)
            && !name.Contains(".Singleton.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Unzips an MSIX into the runner folder, keeping the payload (DLLs, PRIs,
    /// winmds, localized resource folders) and dropping the MSIX packaging metadata.
    /// </summary>
    private static void ExtractMsixPayload(string msixPath, string dest)
    {
        using ZipArchive archive = ZipFile.OpenRead(msixPath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            // Skip directory entries and MSIX packaging metadata.
            if (entry.FullName.EndsWith('/') || IsMsixPackagingEntry(entry.FullName))
            {
                continue;
            }

            string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            string target = Path.Combine(dest, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static bool IsMsixPackagingEntry(string entryName) =>
        MsixPackagingFiles.Contains(entryName)
        || entryName.StartsWith("AppxMetadata/", StringComparison.OrdinalIgnoreCase);

    private static void CopyTreeIfExists(string source, string dest)
    {
        if (Directory.Exists(source))
        {
            CopyDirectory(source, dest);
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }

    private static void Report(IProgress<ProvisionProgress>? progress, string message) =>
        progress?.Report(new ProvisionProgress { Message = message });
}
