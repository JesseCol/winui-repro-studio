using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;
using System.IO.Compression;

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
public sealed class RunnerProvisioner
{
    private const string FlatContainer = "https://api.nuget.org/v3-flatcontainer/";
    private const string MetapackageId = "Microsoft.WindowsAppSDK";
    private const string WinUiComponentId = "Microsoft.WindowsAppSDK.WinUI";
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

    private readonly HttpClient _http;
    private readonly string _nupkgCache;
    private readonly string _versionsRoot;
    private readonly string _localCache;

    public RunnerProvisioner(HttpClient http, string cacheRoot)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentNullException.ThrowIfNull(cacheRoot);
        _nupkgCache = Path.Combine(cacheRoot, "nupkgs");
        _versionsRoot = Path.Combine(cacheRoot, "versions");
        _localCache = Path.Combine(cacheRoot, "local-winui");
    }

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
        string url = FlatContainer + id.ToLowerInvariant() + "/index.json";
        await using Stream stream = await _http.GetStreamAsync(url, ct);
        FlatIndex? index = await JsonSerializer.DeserializeAsync<FlatIndex>(stream, cancellationToken: ct);

        // A '-' marks a SemVer prerelease label (e.g. "1.8.0-experimental1").
        var versions = (index?.Versions ?? new List<string>())
            .Where(v => includePrerelease || !v.Contains('-', StringComparison.Ordinal))
            .Select(NuGetVersion.Parse)
            .OrderByDescending(v => v)
            .Select(v => v.Original)
            .ToList();
        return versions;
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
    /// Makes sure a runner for the version is on disk and returns the exe path.
    /// Copies the base runner, then overlays the version's WASDK runtime files. An
    /// optional <paramref name="winui"/> override swaps just the WinUI component for
    /// a different NuGet version or a local .nupkg, leaving the rest of WASDK alone.
    /// An optional <paramref name="payload"/> copies loose local files over the lot,
    /// which is the quickest way to test a private build of any runtime binary.
    /// </summary>
    public async Task<string> EnsureRunnerAsync(
        string version,
        string baseRunnerDir,
        WinUiOverride? winui = null,
        RunnerPayload? payload = null,
        IProgress<ProvisionProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(baseRunnerDir);

        string folderName = winui is null ? version : $"{version}__{winui.CacheKey}";
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

        Report(progress, $"Preparing runner for WASDK {version}...");
        CopyDirectory(baseRunnerDir, staging);

        Report(progress, $"Resolving components for {version}...");
        IReadOnlyList<(string Id, string Version)> components = await ResolveComponentsAsync(version, ct);

        if (components.Count == 0)
        {
            // Older layout (WASDK 1.7 and earlier): the metapackage has no component
            // dependencies and carries the runtime itself. The native framework payload
            // is zipped inside a .msix under tools\MSIX; the loose native sits alongside.
            // We detect this by the absence of components, not a version number, so the
            // 1.7 -> 1.8 cutover (and any future one) is handled automatically.
            Report(progress, $"Extracting Windows App SDK {version} runtime...");
            string metaDir = await EnsurePackageAsync(MetapackageId, version, ct);
            CopyComponentNativeFiles(metaDir, staging);
            ExtractFrameworkMsix(metaDir, staging);
        }
        else
        {
            // Newer layout (WASDK 1.8+): the runtime lives in component sub-packages
            // (Foundation, WinUI, Runtime, ...), each with loose self-contained files.
            foreach ((string id, string componentVersion) in components)
            {
                // When overriding WinUI, skip the metapackage's WinUI so the override wins.
                if (winui is not null && id.Equals(WinUiComponentId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Report(progress, $"Fetching {id} {componentVersion}...");
                string pkgDir = await EnsurePackageAsync(id, componentVersion, ct);
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

    private async Task<IReadOnlyList<(string Id, string Version)>> ResolveComponentsAsync(
        string version,
        CancellationToken ct)
    {
        string pkgDir = await EnsurePackageAsync(MetapackageId, version, ct);
        string nuspec = Path.Combine(pkgDir, MetapackageId.ToLowerInvariant() + ".nuspec");
        XDocument doc = XDocument.Load(nuspec);

        var components = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement dep in doc.Descendants().Where(e => e.Name.LocalName == "dependency"))
        {
            string? id = dep.Attribute("id")?.Value;
            string? depVersion = dep.Attribute("version")?.Value;
            if (id is null || depVersion is null)
            {
                continue;
            }

            if (!id.StartsWith(MetapackageId + ".", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(id))
            {
                components.Add((id, depVersion.Trim('[', ']', '(', ')')));
            }
        }

        return components;
    }

    private async Task<string> EnsurePackageAsync(string id, string version, CancellationToken ct)
    {
        string lowerId = id.ToLowerInvariant();
        string dir = Path.Combine(_nupkgCache, lowerId, version);
        if (Directory.Exists(dir))
        {
            return dir;
        }

        string url = $"{FlatContainer}{lowerId}/{version}/{lowerId}.{version}.nupkg";
        byte[] bytes = await _http.GetByteArrayAsync(url, ct);

        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        string temp = dir + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var ms = new MemoryStream(bytes))
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
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

    private sealed class FlatIndex
    {
        [System.Text.Json.Serialization.JsonPropertyName("versions")]
        public List<string> Versions { get; set; } = new();
    }
}
