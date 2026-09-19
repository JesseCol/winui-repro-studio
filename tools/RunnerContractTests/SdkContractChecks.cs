using System.Reflection;
using System.Text.Json.Nodes;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.Versioning;
using ReproStudio.Shared;

namespace ReproStudio.Tests;

internal static class SdkContractChecks
{
    public static async Task RunAsync(string folder, Action<bool, string> assert)
    {
        assert(SdkSelection.Normalize(null) == "match" && SdkSelection.Normalize("MATCH") == "match", "null/default API matches native");
        assert(SdkSelection.Normalize("BASE") == "base", "explicit bundled API escape hatch");
        assert(SdkSelection.Normalize("2.4.1-experimental") == "2.4.1-experimental", "experimental SDK preserved");
        assert(SdkSelection.Normalize("2.4") == "2.4", "SDK accepts partial versions before feed resolution");
        bool rejected = false;
        try { SdkSelection.Normalize("2.4.1\n// wasdk: bad"); }
        catch (ArgumentException) { rejected = true; }
        assert(rejected, "SDK header injection rejected");

        var available = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int lookups = 0;
        Task<IReadOnlyList<string>> Lookup()
        {
            lookups++;
            return available.Task;
        }
        foreach (string choice in new[] { "match", "base", "2.4.1-experimental" })
            assert(await SdkSelection.ResolveAsync(choice, Lookup, CancellationToken.None) == choice,
                choice + " resolves without a version feed");
        assert(lookups == 0, "exact and special SDK choices never start a lookup");
        using (var cancellation = new CancellationTokenSource())
        {
            Task<string> resolving = SdkSelection.ResolveAsync("2.4", Lookup, cancellation.Token);
            cancellation.Cancel();
            bool cancelled = false;
            try { await resolving.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { cancelled = true; }
            assert(cancelled, "partial SDK resolution observes the apply cancellation deadline");
        }
        assert(!available.Task.IsCompleted, "cancelling one SDK waiter preserves the shared lookup");
        available.SetResult(["2.5.0", "2.4.2", "2.4.0"]);
        assert(await SdkSelection.ResolveAsync("2.4", Lookup, CancellationToken.None) == "2.4.2",
            "a subsequent SDK selection can reuse the completed lookup");

        const string body = "// repro: retained\r\nclass Repro {\r\n// sdk: body\r\n}\r\n";
        string text = "// wasdk: old\r\n// SDK: base\r\n// sdk: duplicate\r\n// winui: old\r\n" + body;
        string updated = RuntimeHeaderEdit.Rewrite(text, "wasdk", "2.2.0", "2.4.1-experimental");
        assert(updated == "// wasdk: 2.2.0\r\n// sdk: 2.4.1-experimental\r\n" + body, "SDK/native header update preserves all other characters");
        assert(RuntimeHeaderEdit.Rewrite(updated, "wasdk", "2.2.0", "match") == "// wasdk: 2.2.0\r\n" + body, "match removes all SDK headers");
        assert(RuntimeHeaderEdit.Rewrite(updated, "wasdk", "2.2.0", "base").Contains("// sdk: base\r\n"), "base selection serialized");
        var parsed = SnippetFileParser.Parse(updated);
        assert(parsed.Sdk == "2.4.1-experimental" && parsed.WasdkVersion == "2.2.0", "API/native parsed independently");
        assert(VersionResolver.Resolve("2.4", ["2.5.0", "2.4.2", "2.4.0"]) == "2.4.2", "SDK uses dotted version resolver");

        string package = Path.Combine(folder, "asset-package");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "Test.nuspec"), """
            <package><metadata><id>Test</id><version>1.0.0</version><authors>test</authors><description>test</description>
            <dependencies>
              <group targetFramework="net6.0-windows10.0.17763.0"><dependency id="Old" version="1.0.0"/></group>
              <group targetFramework="net8.0-windows10.0.17763.0"><dependency id="External.Runtime" version="[2.0.0,3.0.0)"/><dependency id="Microsoft.Windows.SDK.BuildTools" version="1.0.0"/></group>
            </dependencies></metadata></package>
            """);
        var framework = NuGetFramework.ParseFolder("net10.0-windows10.0.26100.0");
        var dependencies = SdkPackageGraph.Dependencies(package, framework);
        assert(dependencies.Count == 1 && dependencies[0].Id == "External.Runtime"
            && !dependencies[0].VersionRange.Satisfies(NuGet.Versioning.NuGetVersion.Parse("3.0.0")),
            "TFM dependency groups select nearest, retain external deps and upper bounds, omit build-only tools");
        string emptyGroups = Path.Combine(folder, "empty-groups");
        Directory.CreateDirectory(emptyGroups);
        File.WriteAllText(Path.Combine(emptyGroups, "Empty.nuspec"), """
            <package><metadata><id>Empty</id><version>1.0.0</version><authors>test</authors><description>test</description>
            <dependencies><group targetFramework="native"/><group targetFramework="UAP10.0"/></dependencies>
            </metadata></package>
            """);
        assert(SdkPackageGraph.Dependencies(emptyGroups, framework).Count == 0,
            "WebView2 empty native/UAP dependency groups do not require a .NET group");
        foreach (string tfm in new[] { "net6.0-windows10.0.17763.0", "net8.0-windows10.0.17763.0", "net11.0-windows10.0.26100.0" })
        {
            string lib = Path.Combine(package, "lib", tfm);
            Directory.CreateDirectory(lib);
            File.Copy(typeof(Snippet).Assembly.Location, Path.Combine(lib, "Example.Projection.dll"));
        }
        foreach (string rid in new[] { "win-x64", "win-x86", "win-arm64" })
        {
            string native = Path.Combine(package, "runtimes", rid, "native");
            Directory.CreateDirectory(native);
            File.WriteAllText(Path.Combine(native, "native.bin"), rid);
        }
        var model = new SdkPackage("Test", NuGet.Versioning.NuGetVersion.Parse("1.0.0"), package, dependencies);
        foreach (string rid in new[] { "win-x64", "win-x86", "win-arm64" })
        {
            var assets = SdkPackageGraph.Assets(model, new("net10.0-windows10.0.26100.0", rid));
            assert(assets.Single(a => a.IsManaged).Path.Contains("net8.0-windows10.0.17763.0"), rid + " chooses nearest compatible runtime lib, not future API");
            assert(assets.Single(a => !a.IsManaged).Path.Contains(rid), rid + " native assets are architecture-specific");
        }
        var webView = model with { Id = "Microsoft.Web.WebView2" };
        var nativeOnly = SdkPackageGraph.Assets(webView, new("net10.0-windows10.0.26100.0", "win-x64"), includeManaged: false);
        assert(nativeOnly.Count == 1 && !nativeOnly[0].IsManaged,
            "native-only graph never demands an unused managed WebView2 projection in base/cross mode");
        var managedOnly = SdkPackageGraph.Assets(model, new("net10.0-windows10.0.26100.0", "win-arm64"), includeNative: false);
        assert(managedOnly.Count == 1 && managedOnly[0].IsManaged,
            "independent SDK graph selects managed API assets without overlaying its native files");
        var newer = new AssemblyName("Example, Version=2.2.0.0, Culture=neutral, PublicKeyToken=null");
        var older = new AssemblyName("Example, Version=2.1.0.0, Culture=neutral, PublicKeyToken=null");
        assert(ManagedCompatibility.CanSatisfy(newer, older) && !ManagedCompatibility.CanSatisfy(older, newer),
            "support versions may unify upward but never downgrade Runner requirements");
        assert(!ManagedCompatibility.CanSatisfy(newer, new AssemblyName("Other, Version=2.1.0.0")),
            "assembly simple name remains part of compatibility");

        var dependencyGraph = new[]
        {
            Package("Root", "1.0.0", Dependency("A", "[1.0.0,)"), Dependency("B", "[1.0.0,)")),
            Package("A", "1.0.0", Dependency("C", "[1.0.0,2.0.0)")),
            Package("A", "2.0.0", Dependency("C", "[2.0.0,)")),
            Package("B", "1.0.0", Dependency("A", "[2.0.0,)"), Dependency("C", "[2.0.0,)")),
            Package("C", "1.0.0"),
            Package("C", "2.0.0"),
        }.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
        var roots = new Dictionary<string, SdkPackage> { ["Root"] = dependencyGraph["Root/1.0.0"] };
        Task<SdkPackage> ReadPackage(string id, NuGetVersion version) =>
            Task.FromResult(dependencyGraph[id + "/" + version.ToNormalizedString()]);
        Task<IReadOnlyList<NuGetVersion>> ListVersions(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NuGetVersion>>(dependencyGraph.Values
                .Where(p => p.Id == id).Select(p => p.Version).ToArray());
        var resolved = SdkPackageGraph.ResolveClosureAsync(roots, ReadPackage, ListVersions, default)
            .GetAwaiter().GetResult();
        assert(resolved.Single(p => p.Id == "A").Version == NuGetVersion.Parse("2.0.0")
            && resolved.Single(p => p.Id == "C").Version == NuGetVersion.Parse("2.0.0"),
            "replaced parent dependency bounds do not reject a satisfiable diamond");
        dependencyGraph["B/1.0.0"] = Package("B", "1.0.0", Dependency("C", "[3.0.0,)"));
        bool impossible = false;
        try { SdkPackageGraph.ResolveClosureAsync(roots, ReadPackage, ListVersions, default).GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { impossible = true; }
        assert(impossible, "stable unsatisfiable dependency bounds still fail");

        var baseGraph = JsonNode.Parse("""
            {
              "ReproStudio.Runner/1.0.0": { "dependencies": { "Microsoft.WindowsAppSDK": "2.2.0", "Host.Support": "1.0.0" } },
              "Microsoft.WindowsAppSDK/2.2.0": { "dependencies": { "Microsoft.Windows.AI.MachineLearning": "1.0.0" } },
              "Microsoft.Windows.AI.MachineLearning/1.0.0": { "dependencies": { "System.Numerics.Tensors": "9.0.0", "Shared.Support": "1.0.0" } },
              "System.Numerics.Tensors/9.0.0": {},
              "Host.Support/1.0.0": { "dependencies": { "Shared.Support": "1.0.0" } },
              "Shared.Support/1.0.0": { "dependencies": { "Common.Leaf": "1.0.0" } },
              "Common.Leaf/1.0.0": {}
            }
            """)!.AsObject();
        var sdkOwned = SdkPayload.SdkOwnedLibraries(baseGraph);
        assert(sdkOwned.Contains("System.Numerics.Tensors/9.0.0"),
            "SDK-only transitive libraries are owned even without an SDK package prefix");
        assert(!sdkOwned.Contains("Shared.Support/1.0.0") && !sdkOwned.Contains("Common.Leaf/1.0.0"),
            "independently needed host dependencies survive SDK replacement");

        string output = Path.Combine(folder, "pair");
        Directory.CreateDirectory(Path.Combine(output, "Assets"));
        string baseIcon = Path.Combine(output, "Assets", "AppIcon.ico");
        File.WriteAllText(baseIcon, "immutable base icon");
        string dll = Path.Combine(output, "file.dll");
        File.WriteAllText(dll, "one");
        var manifest = new RunnerPairManifest { Files = RunnerPairManifest.Inventory(output) };
        manifest.Save(output);
        assert(manifest.IsValid(output), "pair inventory verifies actual output bytes");
        string identity = Path.Combine(folder, "RunnerIdentity");
        Directory.CreateDirectory(Path.Combine(identity, "Assets"));
        File.WriteAllText(Path.Combine(output, "AppxManifest.xml"), "loose identity");
        foreach (string logo in new[] { "StoreLogo.png", "Square44x44Logo.png", "Square150x150Logo.png" })
        {
            File.WriteAllText(Path.Combine(identity, "Assets", logo), "identity asset");
            File.WriteAllText(Path.Combine(output, "Assets", logo), "identity asset");
        }
        assert(manifest.IsValid(output), "package registration does not invalidate a running SDK/runtime pair");
        PackagedRunnerLauncher.UnstageManifest(output, identity);
        assert(File.Exists(baseIcon) && !File.Exists(Path.Combine(output, "Assets", "StoreLogo.png"))
            && !File.Exists(Path.Combine(output, "AppxManifest.xml")), "identity cleanup removes only staged files, not base assets");
        assert(manifest.IsValid(output), "unregistering package identity preserves SDK/runtime pair validity");
        string leasePath = Path.Combine(folder, "registration.lock");
        using (PackagedRunnerLauncher.OpenRegistrationLease(leasePath))
        {
            bool conflict = false;
            try { using var competing = PackagedRunnerLauncher.OpenRegistrationLease(leasePath); }
            catch (InvalidOperationException) { conflict = true; }
            assert(conflict, "packaged registration has one owner even for the same pair");
        }
        using (var nextLease = PackagedRunnerLauncher.OpenRegistrationLease(leasePath))
            assert(nextLease.CanWrite, "packaged registration lease can be reacquired after release");
        File.WriteAllText(Path.Combine(output, "resources.pri"), "wrong resource index");
        assert(!manifest.IsValid(output), "unexpected resources.pri still invalidates a cached pair");
        File.Delete(Path.Combine(output, "resources.pri"));
        DateTime stamp = File.GetLastWriteTimeUtc(dll);
        File.WriteAllText(dll, "two");
        File.SetLastWriteTimeUtc(dll, stamp);
        assert(!manifest.IsValid(output), "same-size same-timestamp content changes invalidate pair");
        assert(RunnerPairManifest.ContentIdentity(manifest.Files) != RunnerPairManifest.ContentIdentity(RunnerPairManifest.Inventory(output)),
            "base identity reflects actual support content");
        File.WriteAllText(dll, "one");
        File.WriteAllText(Path.Combine(output, "unexpected.dll"), "stale API");
        assert(!manifest.IsValid(output), "unexpected stale managed files invalidate cache");
        File.Delete(Path.Combine(output, "unexpected.dll"));
        manifest.Schema = 0;
        assert(!manifest.IsValid(output), "old native-only schema cannot count as a selected SDK pair");

        string request = Path.Combine(folder, "sdk-request.json");
        SnippetIo.WriteAtomic(request, new Snippet
        {
            Sdk = "2.4.1-experimental",
            Pair = new() { Key = "pair-id", ApiLabel = "WASDK 2.4.1-experimental", NativeLabel = "WASDK 2.2.0", RuntimeIdentifier = "win-arm64" },
        });
        var copy = SnippetIo.TryRead(request)!;
        assert(copy.Sdk == "2.4.1-experimental" && copy.Pair?.RuntimeIdentifier == "win-arm64" && copy.Pair.Key == "pair-id",
            "API/native/RID pair metadata round trips through IPC");
        File.WriteAllText(request, "{\"wasdkVersion\":\"2.2.0\"}");
        assert(SnippetIo.TryRead(request) is { Sdk: null, Pair: null }, "legacy nullable request fields remain readable");
        using (var held = new FileStream(request, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            SnippetIo.WriteAtomic(request, new Snippet { Sdk = "base" });
            assert(SnippetIo.TryRead(request)?.Sdk == "base", "IPC replacement is atomic with an open delete-sharing reader");
        }
        Task blockedWrite;
        using (var transientReader = new FileStream(request, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            blockedWrite = Task.Run(() => SnippetIo.WriteAtomic(request, new Snippet { Sdk = "match" }));
            await Task.Delay(50);
        }
        await blockedWrite.WaitAsync(TimeSpan.FromSeconds(5));
        assert(SnippetIo.TryRead(request)?.Sdk == "match", "IPC replacement retries a transient reader without delete sharing");
        assert(SnippetIo.IsRetryableReplaceError(1175), "ReplaceFile unable-to-remove failures retain both paths and can retry");
        assert(!SnippetIo.IsRetryableReplaceError(1176) && !SnippetIo.IsRetryableReplaceError(1177),
            "ReplaceFile failures that can change paths are not blindly retried");
        using (var stop = new CancellationTokenSource())
        {
            Task reader = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested) _ = SnippetIo.TryRead(request);
            });
            try
            {
                for (int i = 0; i < 50; i++) SnippetIo.WriteAtomic(request, new Snippet { Sdk = "base" });
            }
            finally { stop.Cancel(); reader.GetAwaiter().GetResult(); }
        }
        assert(SnippetIo.TryRead(request)?.Sdk == "base", "atomic pair requests survive concurrent IPC polling");

        static PackageDependency Dependency(string id, string range) => new(id, VersionRange.Parse(range));
        static SdkPackage Package(string id, string version, params PackageDependency[] dependencies) =>
            new(id, NuGetVersion.Parse(version), "", dependencies);
    }
}
