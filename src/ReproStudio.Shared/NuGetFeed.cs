using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace ReproStudio.Shared;

/// <summary>
/// Feed access, done by the real NuGet client rather than by hand. Everything that talks
/// to a package source goes through here: which sources exist, what versions a package
/// has, downloading one, and reading what it says it depends on.
///
/// Sources come from nuget.config the same way the dotnet CLI finds them, so pointing
/// ReproStudio at an internal feed (a WinUI PR feed, say) is a config change and not a
/// code change. A nuget.config sitting next to the repro file is picked up too, which
/// lets a repro travel with the feed it needs.
/// </summary>
public sealed class NuGetFeed : IDisposable
{
    private const string PublicSource = "https://api.nuget.org/v3/index.json";

    private readonly SourceCacheContext _cacheContext = new();
    private readonly ILogger _logger = NullLogger.Instance;
    private readonly IReadOnlyList<SourceRepository> _repositories;

    /// <param name="settingsRoot">
    /// Directory to start the nuget.config search from, usually the folder holding the
    /// repro file. Null uses only the machine and user level config, which is what the
    /// dotnet CLI does outside a project.
    /// </param>
    public NuGetFeed(string? settingsRoot = null)
    {
        ISettings settings = LoadSettings(settingsRoot);
        var provider = new PackageSourceProvider(settings);

        List<PackageSource> sources = provider.LoadPackageSources()
            .Where(s => s.IsEnabled)
            .ToList();

        if (sources.Count == 0)
        {
            // A machine can genuinely have every source disabled. Provisioning is the whole
            // point of the tool, so fall back to the public feed rather than failing.
            sources.Add(new PackageSource(PublicSource));
        }

        Sources = sources.Select(s => s.Name ?? s.Source).ToList();
        _repositories = sources.Select(Repository.Factory.GetCoreV3).ToList();
    }

    /// <summary>Display names of the sources in use, for logs and error messages.</summary>
    public IReadOnlyList<string> Sources { get; }

    private static ISettings LoadSettings(string? root)
    {
        try
        {
            return Settings.LoadDefaultSettings(root);
        }
        catch (NuGetConfigurationException)
        {
            // A malformed nuget.config somewhere up the tree should not stop the tool
            // dead. Carry on with no configured sources; the caller falls back to public.
            return NullSettings.Instance;
        }
    }

    /// <summary>
    /// Turns a version range into a version that actually exists, the way NuGet does it:
    /// the lowest available version that satisfies the range, not the range's floor.
    ///
    /// This matters more than it sounds. WinUI 2.3.0 asks for Foundation &gt;= 2.3.1, and
    /// Foundation 2.3.1 was never published - 2.3.5 is the first one that satisfies it.
    /// Treating the floor as a version to fetch fails on packages that resolve perfectly
    /// well under a normal restore.
    ///
    /// Prerelease versions are considered only when the range itself is prerelease, which
    /// is also NuGet's rule: an experimental floor opts you into experimental builds.
    /// Returns null when nothing satisfies the range.
    /// </summary>
    public async Task<NuGetVersion?> ResolveVersionAsync(
        string id,
        VersionRange range,
        CancellationToken ct)
    {
        bool wantsPrerelease = range.MinVersion?.IsPrerelease == true
            || range.MaxVersion?.IsPrerelease == true;

        IReadOnlyList<NuGetVersion> available =
            await ListVersionsAsync(id, wantsPrerelease, ct).ConfigureAwait(false);

        // FindBestMatch is the resolver's own rule, so we inherit it rather than guess.
        return range.FindBestMatch(available);
    }

    /// <summary>
    /// Every version of a package across all sources, newest first. Prerelease versions
    /// are left out unless asked for.
    /// </summary>
    public async Task<IReadOnlyList<NuGetVersion>> ListVersionsAsync(
        string id,
        bool includePrerelease,
        CancellationToken ct)
    {
        var all = new HashSet<NuGetVersion>();
        Exception? lastFailure = null;

        foreach (SourceRepository repo in _repositories)
        {
            try
            {
                FindPackageByIdResource finder = await repo
                    .GetResourceAsync<FindPackageByIdResource>(ct).ConfigureAwait(false);
                IEnumerable<NuGetVersion> versions = await finder
                    .GetAllVersionsAsync(id, _cacheContext, _logger, ct).ConfigureAwait(false);
                all.UnionWith(versions);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreachable source should not hide the versions another one has.
                lastFailure = ex;
            }
        }

        if (all.Count == 0 && lastFailure is not null)
        {
            throw lastFailure;
        }

        return all
            .Where(v => includePrerelease || !v.IsPrerelease)
            .OrderByDescending(v => v)
            .ToList();
    }

    /// <summary>
    /// Downloads a package into <paramref name="destination"/>, trying each source in
    /// turn. Returns false when no source has it, which is a normal answer and not an
    /// error - the caller knows which id and version it asked for and can say so.
    /// </summary>
    public async Task<bool> TryDownloadAsync(
        string id,
        NuGetVersion version,
        Stream destination,
        CancellationToken ct)
    {
        foreach (SourceRepository repo in _repositories)
        {
            try
            {
                FindPackageByIdResource finder = await repo
                    .GetResourceAsync<FindPackageByIdResource>(ct).ConfigureAwait(false);
                if (await finder.CopyNupkgToStreamAsync(id, version, destination, _cacheContext, _logger, ct)
                        .ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unreachable or unauthorized source: try the next one.
            }
        }

        return false;
    }

    /// <summary>
    /// What a package declares it depends on, filtered to ids starting with
    /// <paramref name="idPrefix"/>. Dependency groups are unioned rather than matched to a
    /// target framework: the Windows App SDK components declare the same component
    /// versions in every group, and a union cannot miss one because we guessed the wrong
    /// framework. Where two groups disagree, the higher floor wins.
    /// </summary>
    public async Task<IReadOnlyList<PackageDependency>> GetDependenciesAsync(
        string id,
        NuGetVersion version,
        string idPrefix,
        CancellationToken ct)
    {
        foreach (SourceRepository repo in _repositories)
        {
            try
            {
                FindPackageByIdResource finder = await repo
                    .GetResourceAsync<FindPackageByIdResource>(ct).ConfigureAwait(false);
                FindPackageByIdDependencyInfo? info = await finder
                    .GetDependencyInfoAsync(id, version, _cacheContext, _logger, ct).ConfigureAwait(false);
                if (info is not null)
                {
                    return FilterDependencies(
                        info.DependencyGroups.SelectMany(g => g.Packages),
                        idPrefix);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }

        return Array.Empty<PackageDependency>();
    }

    /// <summary>
    /// Keeps one entry per id, the one with the highest floor. That matches how NuGet
    /// settles a diamond: a bare version is a minimum, so the package that asks for the
    /// most is the one that has to be satisfied.
    /// </summary>
    public static IReadOnlyList<PackageDependency> FilterDependencies(
        IEnumerable<PackageDependency> dependencies,
        string idPrefix)
    {
        var best = new Dictionary<string, (PackageDependency Dep, NuGetVersion Floor)>(StringComparer.OrdinalIgnoreCase);
        foreach (PackageDependency dep in dependencies)
        {
            if (!dep.Id.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            NuGetVersion? floor = dep.VersionRange?.MinVersion;
            if (floor is null)
            {
                continue;
            }

            if (!best.TryGetValue(dep.Id, out var existing) || floor > existing.Floor)
            {
                best[dep.Id] = (dep, floor);
            }
        }

        return best.Values
            .Select(v => v.Dep)
            .OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Dispose() => _cacheContext.Dispose();
}
