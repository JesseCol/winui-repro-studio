using System.Reflection;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace ReproStudio.Shared;

/// <summary>Runtime-relevant NuGet groups for a prebuilt Runner, never MSBuild evaluation.</summary>
public sealed record RunnerAssetTarget(string Framework, string Rid)
{
    public NuGetFramework NuGetFramework => NuGetFramework.ParseFolder(Framework);

    public static RunnerAssetTarget Read(string directory)
    {
        using var config = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(directory, "ReproStudio.Runner.runtimeconfig.json")));
        using var deps = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(directory, "ReproStudio.Runner.deps.json")));
        string net = config.RootElement.GetProperty("runtimeOptions").GetProperty("tfm").GetString()!;
        string rid = deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!.Split('/').Last();
        if (rid is not ("win-x64" or "win-x86" or "win-arm64"))
            throw new InvalidOperationException("Unsupported base Runner RID: " + rid);
        Version windows = AssemblyName.GetAssemblyName(Path.Combine(directory, "Microsoft.Windows.SDK.NET.dll")).Version!;
        return new($"{net}-windows{windows.Major}.{windows.Minor}.{windows.Build}.0", rid);
    }
}

internal sealed record SdkPackage(string Id, NuGetVersion Version, string Directory,
    IReadOnlyList<PackageDependency> Dependencies)
{
    public string Key => Id + "/" + Version.ToNormalizedString();
}

internal sealed record SdkAsset(SdkPackage Package, string Path, string Destination, bool IsManaged);

internal sealed class SdkPackageGraph(
    NuGetFeed feed, RunnerAssetTarget target,
    Func<string, NuGetVersion, CancellationToken, Task<string>> ensurePackage)
{
    // These packages supply build tasks/generators, not the portable application's
    // runtime. WinRT/SDK.NET compatibility is checked against actual assembly refs.
    private static readonly HashSet<string> BuildOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.Windows.SDK.BuildTools", "Microsoft.Windows.SDK.BuildTools.MSIX",
        "Microsoft.Windows.CsWinRT", "Microsoft.Windows.SDK.NET.Ref",
    };

    public static IReadOnlyList<PackageDependency> Dependencies(string directory, NuGetFramework framework)
    {
        using var reader = new PackageFolderReader(directory);
        var groups = reader.GetPackageDependencies().ToArray();
        NuGetFramework? nearest = new FrameworkReducer().GetNearest(framework, groups.Select(g => g.TargetFramework));
        // WebView2 deliberately has empty native/UAP groups and provides its
        // modern .NET projection through lib_manual. Empty groups carry no edges.
        if (groups.Any(g => g.Packages.Any()) && nearest is null)
            throw new InvalidOperationException($"Package {reader.GetIdentity()} has no dependency group compatible with {framework.GetShortFolderName()}. Choose a compatible SDK or --sdk base.");
        return nearest is null ? [] : groups.First(g => g.TargetFramework.Equals(nearest)).Packages
            .Where(d => !BuildOnly.Contains(d.Id)).ToArray();
    }

    public async Task<IReadOnlyList<SdkPackage>> ResolveAsync(
        string? wasdk, WinUiOverride? winui, Func<WinUiOverride, string> extractLocal, CancellationToken ct)
    {
        var roots = new Dictionary<string, SdkPackage>(StringComparer.OrdinalIgnoreCase);
        async Task<SdkPackage> ReadAsync(string id, NuGetVersion version)
        {
            string directory = await ensurePackage(id, version, ct).ConfigureAwait(false);
            return new(id, version, directory, Dependencies(directory, target.NuGetFramework));
        }
        if (wasdk is not null)
        {
            var package = await ReadAsync("Microsoft.WindowsAppSDK", NuGetVersion.Parse(wasdk)).ConfigureAwait(false);
            roots.Add(package.Id, package);
        }
        if (winui is not null)
        {
            SdkPackage package;
            if (winui.LocalNupkgPath is not null)
            {
                string directory = extractLocal(winui);
                using var reader = new PackageFolderReader(directory);
                PackageIdentity identity = reader.GetIdentity();
                if (!identity.Id.Equals("Microsoft.WindowsAppSDK.WinUI", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A local WinUI package must identify itself as Microsoft.WindowsAppSDK.WinUI.");
                package = new(identity.Id, identity.Version, directory, Dependencies(directory, target.NuGetFramework));
            }
            else package = await ReadAsync("Microsoft.WindowsAppSDK.WinUI", NuGetVersion.Parse(winui.NuGetVersion!)).ConfigureAwait(false);
            roots[package.Id] = package;
        }
        return await ResolveClosureAsync(roots, ReadAsync,
            (id, token) => feed.ListVersionsAsync(id, includePrerelease: true, token), ct).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<SdkPackage>> ResolveClosureAsync(
        IReadOnlyDictionary<string, SdkPackage> roots,
        Func<string, NuGetVersion, Task<SdkPackage>> readPackage,
        Func<string, CancellationToken, Task<IReadOnlyList<NuGetVersion>>> listVersions,
        CancellationToken ct)
    {
        var selected = new Dictionary<string, SdkPackage>(roots, StringComparer.OrdinalIgnoreCase);
        var available = new Dictionary<string, IReadOnlyList<NuGetVersion>>(StringComparer.OrdinalIgnoreCase);
        for (int iteration = 0; iteration < 40; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            var constraints = new Dictionary<string, List<VersionRange>>(StringComparer.OrdinalIgnoreCase);
            var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<SdkPackage>(roots.Values);
            while (queue.TryDequeue(out var package))
            {
                if (!reached.Add(package.Id)) continue;
                foreach (var dependency in package.Dependencies)
                {
                    // An explicit WinUI override intentionally wins over a metapackage's
                    // WinUI dependency. Other constraints, including upper bounds, intersect.
                    if (roots.ContainsKey(dependency.Id)) continue;
                    if (!constraints.TryGetValue(dependency.Id, out var ranges))
                        constraints.Add(dependency.Id, ranges = []);
                    ranges.Add(dependency.VersionRange);
                    if (selected.TryGetValue(dependency.Id, out var child)) queue.Enqueue(child);
                }
            }
            var next = new Dictionary<string, SdkPackage>(roots, StringComparer.OrdinalIgnoreCase);
            var unsatisfied = new List<string>();
            foreach (var (id, ranges) in constraints.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
            {
                SdkPackage? package = null;
                if (selected.TryGetValue(id, out var previous) && ranges.All(r => r.Satisfies(previous.Version)))
                    package = previous;
                if (package is null)
                {
                    if (!available.TryGetValue(id, out var versions))
                    {
                        versions = await listVersions(id, ct).ConfigureAwait(false);
                        available.Add(id, versions);
                    }
                    bool prerelease = ranges.Any(r => r.MinVersion?.IsPrerelease == true || r.MaxVersion?.IsPrerelease == true);
                    NuGetVersion? version = versions.Where(v => prerelease || !v.IsPrerelease)
                        .Where(v => ranges.All(r => r.Satisfies(v))).OrderBy(v => v).FirstOrDefault();
                    if (version is null)
                    {
                        // A parent selected in this pass may replace the dependency
                        // range that conflicts. Recompute before declaring failure.
                        unsatisfied.Add($"No {id} version satisfies {string.Join(" AND ", ranges)}.");
                        if (selected.TryGetValue(id, out var pending)) next.Add(id, pending);
                        continue;
                    }
                    package = await readPackage(id, version).ConfigureAwait(false);
                }
                next.Add(id, package);
            }
            if (next.Count == selected.Count && next.All(p =>
                selected.TryGetValue(p.Key, out var old) && old.Version == p.Value.Version))
            {
                if (unsatisfied.Count > 0)
                    throw new InvalidOperationException(string.Join(" ", unsatisfied));
                return next.Values.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            selected = next;
        }
        throw new InvalidOperationException("The SDK dependency graph did not converge; no payload was selected.");
    }

    public static string? NearestFolder(string parent, NuGetFramework framework)
    {
        if (!Directory.Exists(parent)) return null;
        var folders = Directory.GetDirectories(parent).Select(p => (Path: p, Tfm: NuGetFramework.ParseFolder(System.IO.Path.GetFileName(p))))
            .Where(p => !p.Tfm.IsUnsupported).ToArray();
        NuGetFramework? nearest = new FrameworkReducer().GetNearest(framework, folders.Select(p => p.Tfm));
        return nearest is null ? null : folders.First(p => p.Tfm.Equals(nearest)).Path;
    }

    public static IReadOnlyList<string> RidFallbacks(string rid) => rid switch
    {
        "win-x64" or "win-x86" or "win-arm64" => [rid, "win10-" + rid[4..], "win", "any"],
        _ => throw new ArgumentException("Unsupported Runner RID: " + rid),
    };

    public static IReadOnlyList<SdkAsset> Assets(SdkPackage package, RunnerAssetTarget target,
        bool includeManaged = true, bool includeNative = true)
    {
        var assets = new Dictionary<string, SdkAsset>(StringComparer.OrdinalIgnoreCase);
        string root = package.Directory;
        void AddManaged(string? directory)
        {
            if (directory is null) return;
            foreach (string file in Directory.EnumerateFiles(directory, "*.dll"))
            {
                if (!ManagedCompatibility.TryIdentity(file, out _)) continue;
                string name = System.IO.Path.GetFileName(file);
                assets[name] = new(package, System.IO.Path.GetRelativePath(root, file), name, true);
            }
        }
        if (includeManaged)
        {
            AddManaged(NearestFolder(System.IO.Path.Combine(root, "lib"), target.NuGetFramework));
            foreach (string rid in RidFallbacks(target.Rid).Reverse())
                AddManaged(NearestFolder(System.IO.Path.Combine(root, "runtimes", rid, "lib"), target.NuGetFramework));
        }
        // WebView2's shipped C#/WinRT projection is selected by build/Common.targets,
        // not normal lib items. Consume that prebuilt projection, never WPF/WinForms.
        if (includeManaged && package.Id.Equals("Microsoft.Web.WebView2", StringComparison.OrdinalIgnoreCase))
        {
            string? directory = NearestFolder(System.IO.Path.Combine(root, "lib_manual"), target.NuGetFramework);
            string path = System.IO.Path.Combine(directory ?? "", "Microsoft.Web.WebView2.Core.Projection.dll");
            if (!File.Exists(path))
                throw new InvalidOperationException($"WebView2 {package.Version} has no compatible prebuilt WinRT projection.");
            assets.Clear();
            assets[System.IO.Path.GetFileName(path)] = new(package, System.IO.Path.GetRelativePath(root, path), System.IO.Path.GetFileName(path), true);
        }
        foreach (string group in includeNative ? new[] { "runtimes-framework", "runtimes" } : [])
        {
            foreach (string rid in RidFallbacks(target.Rid).Reverse())
            {
                AddNative(System.IO.Path.Combine(root, group, rid, "native"));
                if (package.Id.Equals("Microsoft.Web.WebView2", StringComparison.OrdinalIgnoreCase))
                    AddNative(System.IO.Path.Combine(root, group, rid, "native_uap"));
            }
        }
        return assets.Values.ToArray();

        void AddNative(string directory)
        {
            if (!Directory.Exists(directory)) return;
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                string destination = System.IO.Path.GetRelativePath(directory, file);
                if (destination.Equals("resources.pri", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A runtime package would shadow ReproStudio.Runner.pri with resources.pri.");
                if (System.IO.Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase)
                    && ManagedCompatibility.TryIdentity(file, out _))
                    throw new InvalidOperationException("Unexpected managed assembly in native package assets: " + file);
                assets[destination] = new(package, System.IO.Path.GetRelativePath(root, file), destination, false);
            }
        }
    }
}
