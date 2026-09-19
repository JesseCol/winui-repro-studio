using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;

namespace ReproStudio.Shared;

internal static class SdkPayload
{
    private static bool IsSdkRoot(string key)
    {
        string id = key.Split('/')[0];
        return id.Equals("Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("Microsoft.WindowsAppSDK.", StringComparison.OrdinalIgnoreCase)
            || id.Equals("Microsoft.Windows.AI.MachineLearning", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("Microsoft.Web.WebView2", StringComparison.OrdinalIgnoreCase);
    }

    internal static HashSet<string> SdkOwnedLibraries(JsonObject target)
    {
        var keys = target.Select(p => p.Key).ToDictionary(k => k.Split('/')[0], StringComparer.OrdinalIgnoreCase);
        HashSet<string> Walk(IEnumerable<string> roots, bool skipSdk)
        {
            var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<string>(roots);
            while (pending.TryDequeue(out string? key))
            {
                if ((skipSdk && IsSdkRoot(key)) || !reached.Add(key)) continue;
                if (target[key]?["dependencies"] is not JsonObject dependencies) continue;
                foreach (string id in dependencies.Select(p => p.Key))
                    if (keys.TryGetValue(id, out string? child)) pending.Enqueue(child);
            }
            return reached;
        }

        var owned = Walk(target.Select(p => p.Key).Where(IsSdkRoot), skipSdk: false);
        // Shared dependencies remain only when the app independently requires them.
        // Package prefixes identify roots, not ownership of transitive dependencies.
        owned.ExceptWith(Walk(target.Select(p => p.Key)
            .Where(k => k.StartsWith("ReproStudio.Runner/", StringComparison.OrdinalIgnoreCase)), skipSdk: true));
        return owned;
    }

    public static string BundledLabel(string baseDirectory)
    {
        var deps = JsonNode.Parse(File.ReadAllText(Path.Combine(baseDirectory, "ReproStudio.Runner.deps.json")))!;
        string? package = deps["libraries"]!.AsObject().Select(p => p.Key)
            .FirstOrDefault(k => k.StartsWith("Microsoft.WindowsAppSDK/", StringComparison.OrdinalIgnoreCase));
        return "Bundled API (base" + (package is null ? "" : "; WASDK " + package.Split('/')[1]) + ")";
    }

    public static IReadOnlyList<string> ApplyManaged(
        string baseDirectory, string staging, IReadOnlyList<SdkPackage> packages,
        IReadOnlyList<SdkAsset> assets, IReadOnlyList<SdkAsset> nativeAssets, bool useBase)
    {
        string depsPath = Path.Combine(staging, "ReproStudio.Runner.deps.json");
        JsonNode deps = JsonNode.Parse(File.ReadAllText(depsPath))!;
        JsonObject libraries = deps["libraries"]!.AsObject();
        JsonObject target = deps["targets"]![deps["runtimeTarget"]!["name"]!.GetValue<string>()]!.AsObject();
        HashSet<string> ownedLibraries = SdkOwnedLibraries(target);
        var ownedIds = ownedLibraries.Select(k => k.Split('/')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in target.Where(p => ownedLibraries.Contains(p.Key)).ToArray())
        {
            foreach (var file in (entry.Value?["runtime"] as JsonObject ?? []))
                owned.Add(Path.GetFileName(file.Key));
        }
        if (!useBase)
        {
            foreach (string name in owned)
            {
                string path = Path.Combine(staging, name);
                if (File.Exists(path)) File.Delete(path);
            }
            foreach (string key in ownedLibraries)
            {
                target.Remove(key);
                libraries.Remove(key);
            }
            foreach (var entry in target)
            {
                if (entry.Value?["dependencies"] is not JsonObject dependencies) continue;
                foreach (string id in dependencies.Select(p => p.Key).Where(ownedIds.Contains).ToArray())
                    dependencies.Remove(id);
            }
        }
        else
        {
            // Keep bundled API entries, but native dependency entries describe the
            // runtime selected below, not the native version from the base build.
            foreach (var entry in target.Where(p => ownedLibraries.Contains(p.Key)))
            {
                entry.Value?.AsObject().Remove("native");
                entry.Value?.AsObject().Remove("runtimeTargets");
            }
        }
        var managed = new Dictionary<string, SdkAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets.Where(a => a.IsManaged))
        {
            if (managed.TryGetValue(asset.Destination, out var other)
                && RunnerPairManifest.HashFile(Path.Combine(other.Package.Directory, other.Path))
                    != RunnerPairManifest.HashFile(Path.Combine(asset.Package.Directory, asset.Path)))
                throw new InvalidOperationException("SDK graph has conflicting assemblies: " + asset.Destination);
            managed[asset.Destination] = asset;
        }
        if (!useBase && !managed.ContainsKey("Microsoft.WinUI.dll"))
            throw new InvalidOperationException("Selected SDK/package has no compatible prebuilt Microsoft.WinUI.dll. Choose --sdk base or a package with managed API assets.");
        var checkedFiles = new List<string>();
        if (!useBase)
        {
            foreach (var asset in managed.Values)
            {
                string source = Path.Combine(asset.Package.Directory, asset.Path);
                string dest = Path.Combine(staging, asset.Destination);
                AssemblyName required = AssemblyName.GetAssemblyName(source);
                if (File.Exists(dest) && !owned.Contains(asset.Destination))
                {
                    // BCL, WinRT, SDK.NET and shared host support belong to the
                    // self-contained bundle. Never overwrite them with older packages.
                    AssemblyName actual = AssemblyName.GetAssemblyName(dest);
                    if (!ManagedCompatibility.CanSatisfy(actual, required))
                        throw new InvalidOperationException($"SDK needs {required.FullName}; bundled support is {actual.FullName}. Use a compatible SDK, a newer bundle, or --sdk base.");
                    checkedFiles.Add(asset.Destination);
                    continue;
                }
                File.Copy(source, dest, overwrite: true);
                checkedFiles.Add(asset.Destination);
                JsonObject entry = Entry(asset.Package.Key);
                var runtime = entry["runtime"] as JsonObject;
                if (runtime is null) entry["runtime"] = runtime = new();
                runtime[asset.Path.Replace('\\', '/')] = new JsonObject
                {
                    ["assemblyVersion"] = required.Version!.ToString(),
                    ["fileVersion"] = FileVersionInfo.GetVersionInfo(source).FileVersion,
                };
            }
            // deps.json is a runtime closure, not a restore graph. Every selected
            // library is connected to the app without pretending to run build targets.
            var dependencies = new JsonObject();
            foreach (string key in managed.Values.Select(a => a.Package.Key).Distinct().Where(k => target.ContainsKey(k)))
                dependencies[key.Split('/')[0]] = key.Split('/')[1];
            Entry("ReproStudio.SelectedSdk/1.0.0")["dependencies"] = dependencies;
            JsonObject app = target.First(p => p.Key.StartsWith("ReproStudio.Runner/", StringComparison.Ordinal)).Value!.AsObject();
            (app["dependencies"] ??= new JsonObject())["ReproStudio.SelectedSdk"] = "1.0.0";
        }
        else checkedFiles.AddRange(owned.Where(n => File.Exists(Path.Combine(staging, n))));

        foreach (var asset in nativeAssets.Where(a => !a.IsManaged && a.Destination.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(a.Destination) == a.Destination))
        {
            // Distinct names keep compile-against-X/run-on-Y native assets from
            // competing with managed libraries of the same NuGet package id.
            JsonObject entry = Entry("ReproStudio.Native." + asset.Package.Key);
            if (entry["native"] is not JsonObject native) entry["native"] = native = new();
            native[asset.Destination.Replace('\\', '/')] = new JsonObject
            {
                ["fileVersion"] = FileVersionInfo.GetVersionInfo(Path.Combine(asset.Package.Directory, asset.Path)).FileVersion,
            };
        }
        File.WriteAllText(depsPath, deps.ToJsonString(new() { WriteIndented = true }));
        ManagedCompatibility.Validate(staging, checkedFiles);
        return checkedFiles;

        JsonObject Entry(string key)
        {
            if (target[key] is JsonObject existing) return existing;
            var value = new JsonObject();
            target[key] = value;
            libraries[key] = new JsonObject { ["type"] = "project", ["serviceable"] = false, ["sha512"] = "" };
            return value;
        }
    }
}
