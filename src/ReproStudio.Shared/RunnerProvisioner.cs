using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace ReproStudio.Shared;

public sealed class ProvisionProgress
{
    public required string Message { get; init; }
}

/// <summary>Assembles a portable API/native pair from prebuilt package assets, without a target SDK.</summary>
public sealed class RunnerProvisioner : IDisposable
{
    private const string MetapackageId = "Microsoft.WindowsAppSDK";
    private const string WinUiComponentId = MetapackageId + ".WinUI";
    private readonly NuGetFeed _feed;
    private readonly string _nupkgCache;
    private readonly string _versionsRoot;
    private readonly string _localCache;
    private readonly SemaphoreSlim _provisionGate = new(1, 1);

    public RunnerProvisioner(string cacheRoot, string? settingsRoot = null)
    {
        _feed = new NuGetFeed(settingsRoot);
        _nupkgCache = Path.Combine(cacheRoot, "nupkgs");
        _versionsRoot = Path.Combine(cacheRoot, "versions");
        _localCache = Path.Combine(cacheRoot, "local-winui");
    }

    public IReadOnlyList<string> Sources => _feed.Sources;
    public void Dispose() { _feed.Dispose(); _provisionGate.Dispose(); }
    public Task<IReadOnlyList<string>> ListWasdkVersionsAsync(bool includePrerelease = false, CancellationToken ct = default) =>
        ListPackageVersionsAsync(MetapackageId, includePrerelease, ct);
    public Task<IReadOnlyList<string>> ListWinUiVersionsAsync(bool includePrerelease = false, CancellationToken ct = default) =>
        ListPackageVersionsAsync(WinUiComponentId, includePrerelease, ct);
    private async Task<IReadOnlyList<string>> ListPackageVersionsAsync(string id, bool prerelease, CancellationToken ct) =>
        (await _feed.ListVersionsAsync(id, prerelease, ct).ConfigureAwait(false)).Select(v => v.ToNormalizedString()).ToArray();

    public string GetVersionFolder(string version) => Path.Combine(_versionsRoot, version);
    public void ClearProvisionedRunners()
    {
        DeleteWithRetry(_versionsRoot);
        DeleteWithRetry(_localCache);
    }

    public async Task<string> EnsureRunnerAsync(
        string? version, string baseRunnerDir, WinUiOverride? winui = null,
        RunnerPayload? payload = null, IProgress<ProvisionProgress>? progress = null,
        CancellationToken ct = default, string? sdk = null)
    {
        sdk = SdkSelection.Normalize(sdk);
        if (version is null && winui is null) throw new ArgumentException("Choose a native WASDK version or WinUI package.");
        await _provisionGate.WaitAsync(ct).ConfigureAwait(false);
        string? staging = null;
        try
        {
            var target = RunnerAssetTarget.Read(baseRunnerDir);
            // Only on provisioning/reselection, not UI polling or ordinary C# saves.
            // Hash actual content, including host support and resources, not timestamps.
            string baseIdentity = RunnerPairManifest.ContentIdentity(RunnerPairManifest.Inventory(baseRunnerDir));
            string localIdentity = winui?.LocalNupkgPath is { } local ? RunnerPairManifest.HashFile(local) : "";
            string payloadIdentity = payload is null ? "" : RunnerPairManifest.ContentIdentity(
                payload.RelativePaths.ToDictionary(p => p, p => RunnerPairManifest.HashFile(Path.Combine(payload.Directory, p))));
            string selectionKey = RunnerPairManifest.HashText(
                $"pair:{RunnerPairManifest.CurrentSchema}|{version}|{winui?.NuGetVersion}|{localIdentity}|{sdk}|{payloadIdentity}|{baseIdentity}|{target.Framework}|{target.Rid}");
            string prefix = "pair-v" + RunnerPairManifest.CurrentSchema + "-" + selectionKey[..24] + "-";
            Directory.CreateDirectory(_versionsRoot);
            foreach (string directory in Directory.EnumerateDirectories(_versionsRoot, prefix + "*")
                .Where(d => !Path.GetFileName(d).Contains(".staging", StringComparison.Ordinal)))
            {
                RunnerPairManifest? cached = RunnerPairManifest.Read(directory);
                if (cached?.SelectionKey == selectionKey && cached.BaseIdentity == baseIdentity && cached.IsValid(directory))
                {
                    Report(progress, "Verified cached " + cached.Pair.Describe());
                    return Path.Combine(directory, "ReproStudio.Runner.exe");
                }
            }
            Report(progress, $"Resolving API {sdk}; native {version ?? "WinUI package"}; {target.Framework}/{target.Rid}...");
            var resolver = new SdkPackageGraph(_feed, target, EnsurePackageAsync);
            // Shape-only private native packages remain usable with --sdk base or an
            // explicit SDK, but cannot truthfully supply matched managed APIs.
            bool hasWinUiMetadata = winui?.LocalNupkgPath is null
                || Directory.EnumerateFiles(ExtractLocalNupkg(winui.LocalNupkgPath), "*.nuspec").Any();
            if (!hasWinUiMetadata && (sdk == "match" || version is null))
                throw new InvalidOperationException("This local WinUI package has no SDK/dependency metadata. Specify a native --wasdk version and --sdk base or an explicit SDK; matched API selection is unavailable.");
            IReadOnlyList<SdkPackage> nativeGraph = await resolver.ResolveAsync(
                version, hasWinUiMetadata ? winui : null, w => ExtractLocalNupkg(w.LocalNupkgPath!), ct).ConfigureAwait(false);
            IReadOnlyList<SdkPackage> sdkGraph = sdk switch
            {
                "base" => [],
                "match" => nativeGraph,
                _ => await resolver.ResolveAsync(sdk, null, w => ExtractLocalNupkg(w.LocalNupkgPath!), ct).ConfigureAwait(false),
            };
            var nativeAssets = nativeGraph.SelectMany(p => SdkPackageGraph.Assets(p, target, includeManaged: false)).ToList();
            var sdkAssets = sdkGraph.SelectMany(p => SdkPackageGraph.Assets(p, target, includeNative: false)).ToArray();
            staging = Path.Combine(_versionsRoot, prefix + Guid.NewGuid().ToString("N") + ".staging");
            CopyDirectory(baseRunnerDir, staging);
            // Only the old monolithic metapackage needs framework extraction.
            // Split Runtime packages also contain deployment MSIXs; those are not
            // self-contained asset groups and must not overwrite component payloads.
            foreach (var package in nativeGraph.Where(p => p.Id.Equals(MetapackageId, StringComparison.OrdinalIgnoreCase)
                && !nativeGraph.Any(c => c.Id.Equals(WinUiComponentId, StringComparison.OrdinalIgnoreCase))))
                ExtractFrameworkMsix(package.Directory, staging, target.Rid);
            foreach (var asset in nativeAssets)
                CopyAsset(asset, staging);
            if (!hasWinUiMetadata)
            {
                // The shape-only native payload has no graph; hash every resulting file
                // below and retain the original archive content identity in the pair key.
                string directory = ExtractLocalNupkg(winui!.LocalNupkgPath!);
                foreach (string rid in SdkPackageGraph.RidFallbacks(target.Rid).Reverse())
                    foreach (string root in new[] { "runtimes-framework", "runtimes" })
                    {
                        string source = Path.Combine(directory, root, rid, "native");
                        if (Directory.Exists(source))
                        {
                            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                            {
                                string name = Path.GetFileName(file);
                                if ((name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && ManagedCompatibility.TryIdentity(file, out _))
                                    || name.StartsWith("ReproStudio.Runner.", StringComparison.OrdinalIgnoreCase)
                                    || name.Equals("resources.pri", StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidOperationException("A native-only WinUI package cannot replace managed SDK/Runner assets: " + file);
                            }
                            CopyDirectory(source, staging);
                        }
                    }
            }
            var managed = SdkPayload.ApplyManaged(baseRunnerDir, staging, sdkGraph, sdkAssets, nativeAssets, sdk == "base");
            if (payload is not null)
            {
                foreach (string relative in payload.RelativePaths)
                {
                    string source = Path.Combine(payload.Directory, relative);
                    string name = Path.GetFileName(relative);
                    if ((name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && ManagedCompatibility.TryIdentity(source, out _))
                        || name.StartsWith("ReproStudio.Runner.", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("resources.pri", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A loose native payload cannot replace managed SDK/Runner assets: " + relative + ". Select managed APIs with --sdk.");
                }
                payload.ApplyTo(staging);
            }
            if (!File.Exists(Path.Combine(staging, "ReproStudio.Runner.pri"))
                || File.Exists(Path.Combine(staging, "resources.pri")))
                throw new InvalidOperationException("Invalid Runner resource indexes: retain ReproStudio.Runner.pri and omit resources.pri.");
            ManagedCompatibility.Validate(staging, managed);
            string winuiPath = Path.Combine(staging, "Microsoft.WinUI.dll");
            string nativeLabel = (version is null ? "" : "WASDK " + version)
                + (winui is null ? "" : (version is null ? "" : " + ") + "WinUI " + (winui.LocalNupkgPath is null ? winui.NuGetVersion : Path.GetFileName(winui.LocalNupkgPath)))
                + (payload is null ? "" : " + native payload " + payloadIdentity[..12]);
            string apiLabel = sdk == "base" ? SdkPayload.BundledLabel(baseRunnerDir)
                : sdk != "match" ? "WASDK " + sdk
                : (version is null ? "" : "WASDK " + version)
                    + (winui is null ? "" : (version is null ? "" : " + ") + "WinUI package " +
                        sdkGraph.Single(p => p.Id.Equals(WinUiComponentId, StringComparison.OrdinalIgnoreCase)).Version.ToNormalizedString());
            var manifest = new RunnerPairManifest
            {
                BaseIdentity = baseIdentity,
                SelectionKey = selectionKey,
                Pair = new()
                {
                    Sdk = sdk, ApiLabel = apiLabel, NativeLabel = nativeLabel, NativeWasdkVersion = version,
                    RuntimeIdentifier = target.Rid, TargetFramework = target.Framework,
                    WinUiAssemblyIdentity = AssemblyName.GetAssemblyName(winuiPath).FullName,
                    WinUiFileVersion = FileVersionInfo.GetVersionInfo(winuiPath).FileVersion ?? "",
                    WinUiSha256 = RunnerPairManifest.HashFile(winuiPath),
                },
                Packages = nativeGraph.Select(p => "native: " + p.Key).Concat(sdkGraph.Select(p => "api: " + p.Key)).ToArray(),
                Assets = nativeAssets.Concat(sdkAssets).Select(a => Path.Combine(a.Package.Directory, a.Path))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p, RunnerPairManifest.HashFile),
                Files = RunnerPairManifest.Inventory(staging),
            };
            // Graph versions, TFM/RID asset choices, resources, support and output deps
            // all contribute. Old native-only entries are never reused or migrated.
            manifest.Pair.Key = RunnerPairManifest.HashText(selectionKey + "\n" + string.Join("\n", manifest.Packages)
                + "\n" + RunnerPairManifest.ContentIdentity(manifest.Files));
            string dest = Path.Combine(_versionsRoot, prefix + manifest.Pair.Key[..24]);
            manifest.Save(staging);
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(dest))
            {
                // Another host may have published and launched this identical pair
                // while we were assembling it. Never delete its verified payload.
                RunnerPairManifest? existing = RunnerPairManifest.Read(dest);
                if (existing?.Pair.Key != manifest.Pair.Key || !existing.IsValid(dest))
                    DeleteWithRetry(dest);
            }
            MoveWithRetry(staging, dest);
            staging = null;
            if (RunnerPairManifest.Read(dest)?.Pair.Key != manifest.Pair.Key)
                throw new InvalidOperationException("A concurrent provision wrote a different SDK/runtime pair. Retry the selection.");
            Report(progress, "Prepared " + manifest.Pair.Describe() + "; managed WinUI " + manifest.Pair.WinUiFileVersion);
            return Path.Combine(dest, "ReproStudio.Runner.exe");
        }
        catch (Exception ex) when (ex is PackagingException or BadImageFormatException)
        {
            throw new InvalidOperationException("Unsupported SDK/package assets: " + ex.Message
                + ". Choose a compatible package or --sdk base.", ex);
        }
        finally
        {
            if (staging is not null && Directory.Exists(staging)) DeleteWithRetry(staging);
            _provisionGate.Release();
        }
    }

    private static void CopyAsset(SdkAsset asset, string dest)
    {
        string target = Path.Combine(dest, asset.Destination);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(asset.Package.Directory, asset.Path), target, overwrite: true);
    }

    private string ExtractLocalNupkg(string path)
    {
        string directory = Path.Combine(_localCache, WinUiOverride.HashFile(path));
        if (Directory.Exists(directory)) return directory;
        Directory.CreateDirectory(_localCache);
        string temp = directory + ".tmp-" + Guid.NewGuid().ToString("N");
        try { ZipFile.ExtractToDirectory(path, temp); MoveWithRetry(temp, directory); }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
        return directory;
    }

    private async Task<string> EnsurePackageAsync(string id, NuGetVersion version, CancellationToken ct)
    {
        string directory = Path.Combine(_nupkgCache, id.ToLowerInvariant(), version.ToNormalizedString());
        if (Directory.Exists(directory)) return directory;
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        string temp = directory + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using var buffer = new MemoryStream();
            if (!await _feed.TryDownloadAsync(id, version, buffer, ct).ConfigureAwait(false))
                throw new InvalidOperationException($"{id} {version} was not found on any package source. Tried: {string.Join(", ", Sources)}. Check the repro's nuget.config.");
            buffer.Position = 0;
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Read)) archive.ExtractToDirectory(temp);
            MoveWithRetry(temp, directory);
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
        return directory;
    }

    private static void ExtractFrameworkMsix(string package, string dest, string rid)
    {
        string directory = Path.Combine(package, "tools", "MSIX", "win10-" + rid[4..]);
        if (!Directory.Exists(directory)) return;
        string? framework = Directory.EnumerateFiles(directory, "*.msix").FirstOrDefault(p =>
            !p.Contains(".DDLM.", StringComparison.OrdinalIgnoreCase)
            && !p.Contains(".Main.", StringComparison.OrdinalIgnoreCase)
            && !p.Contains(".Singleton.", StringComparison.OrdinalIgnoreCase));
        if (framework is null) throw new InvalidOperationException("No framework MSIX for " + rid + " in " + package);
        using var archive = ZipFile.OpenRead(framework);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.StartsWith("AppxMetadata/", StringComparison.OrdinalIgnoreCase)
                || entry.FullName is "AppxManifest.xml" or "AppxBlockMap.xml" or "AppxSignature.p7x" or "[Content_Types].xml"
                // The framework's package index must not shadow the app's PRI.
                || entry.FullName.Equals("resources.pri", StringComparison.OrdinalIgnoreCase))
                continue;
            string target = Path.GetFullPath(Path.Combine(dest, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(Path.GetFullPath(dest) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe framework MSIX entry: " + entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (string directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(dest, Path.GetFileName(directory)));
    }

    private static void DeleteWithRetry(string directory)
    {
        if (!Directory.Exists(directory)) return;
        for (int attempt = 1; ; attempt++)
        {
            try { Directory.Delete(directory, recursive: true); return; }
            catch (IOException) when (attempt < 10) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) when (attempt < 10) { Thread.Sleep(200); }
        }
    }

    private static void MoveWithRetry(string source, string dest)
    {
        for (int attempt = 1; ; attempt++)
        {
            if (Directory.Exists(dest)) { Directory.Delete(source, recursive: true); return; }
            try { Directory.Move(source, dest); return; }
            catch (IOException) when (attempt < 10) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) when (attempt < 10) { Thread.Sleep(200); }
        }
    }
    private static void Report(IProgress<ProvisionProgress>? progress, string message) =>
        progress?.Report(new ProvisionProgress { Message = message });
}
