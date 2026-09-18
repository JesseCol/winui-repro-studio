using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace ReproStudio_Runner.Services;

/// <summary>Outcome of compiling a snippet's C#.</summary>
public sealed class CompileResult
{
    public bool Success { get; init; }

    public Assembly? Assembly { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// Compiles a snippet's C# into an in-memory assembly using Roslyn. It references
/// the runner's own loaded assemblies, so the snippet always builds against the
/// exact WinUI version on screen. Each compile uses a fresh collectible load
/// context, and the previous one is unloaded to avoid leaking on every edit.
/// </summary>
public sealed class RoslynCompiler
{
    private const string Usings = """
        using System;
        using Microsoft.UI.Xaml;
        using Microsoft.UI.Xaml.Controls;
        using Microsoft.UI.Xaml.Controls.Primitives;
        using Microsoft.UI.Xaml.Media;
        using Microsoft.UI.Xaml.Shapes;
        using Microsoft.UI.Xaml.Input;
        using Microsoft.UI.Windowing;
        using Windows.Graphics;
        using static ReproStudio_Runner.ReproApi;

        """;

    private static readonly int UsingsLineCount = Usings.Count(c => c == '\n');

    /// <summary>
    /// Simple assembly name -> file path for everything the runner can resolve: the
    /// shared framework plus the assemblies shipped next to the exe.
    /// </summary>
    private static readonly Lazy<IReadOnlyDictionary<string, string>> ResolvableAssemblyPaths =
        new(BuildResolvableAssemblyPaths);

    /// <summary>
    /// <see cref="MetadataReference"/> is immutable and meant to be shared across
    /// compilations, so cache by path instead of re-reading every file on each edit.
    /// </summary>
    private static readonly ConcurrentDictionary<string, MetadataReference> ReferenceCache =
        new(StringComparer.OrdinalIgnoreCase);

    private AssemblyLoadContext? _previous;

    public CompileResult Compile(string csharp)
    {
        ArgumentNullException.ThrowIfNull(csharp);

        var parseOptions = CSharpParseOptions.Default;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(Usings + csharp, parseOptions);

        var compilation = CSharpCompilation.Create(
            "ReproSnippet_" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            BuildReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                platform: RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X86 => Platform.X86,
                    Architecture.X64 => Platform.X64,
                    Architecture.Arm64 => Platform.Arm64,
                    _ => throw new PlatformNotSupportedException("Unsupported Runner architecture."),
                }));

        Compilation output = compilation;
        try
        {
            IReadOnlyList<string> requests = Win32Generator.ReadRequests(csharp);
            if (requests.Count > 0)
            {
                output = Win32Generator.Generate(compilation, parseOptions, requests, out var diagnostics);
                // CsWin32 reports unknown APIs (and Roslyn reports generator crashes)
                // as warnings. Never emit a success-shaped, incomplete binding set.
                var failures = diagnostics.Where(d =>
                    d.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error).ToList();
                if (failures.Count > 0)
                {
                    return new CompileResult
                    {
                        Success = false,
                        Error = "CsWin32 generation failed:\n" + FormatDiagnostics(failures, tree),
                    };
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            return new CompileResult { Success = false, Error = "CsWin32 generation failed:\n" + ex };
        }

        using var ms = new MemoryStream();
        EmitResult emit = output.Emit(ms);
        if (!emit.Success)
        {
            return new CompileResult
            {
                Success = false,
                Error = FormatDiagnostics(emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error), tree),
            };
        }

        ms.Seek(0, SeekOrigin.Begin);
        var context = new AssemblyLoadContext("ReproSnippet", isCollectible: true);
        Assembly assembly = context.LoadFromStream(ms);

        _previous?.Unload();
        _previous = context;

        return new CompileResult { Success = true, Assembly = assembly };
    }

    /// <summary>
    /// Builds the reference set for a snippet compile.
    /// <para>
    /// The obvious source - <c>AppDomain.CurrentDomain.GetAssemblies()</c> - only sees
    /// assemblies the process has already loaded, and .NET loads lazily. Most WindowsAppSDK
    /// projections (Microsoft.Windows.Storage, AppNotifications, Storage.Pickers, AppLifecycle,
    /// AI.*, Widgets, ...) ship next to the runner but are never touched by the runner's own
    /// code, and plenty of the shared framework (System.Text.Json, System.Net.Http, ...) is
    /// never touched either. None of it would appear, so snippets using it fail with CS0234.
    /// </para>
    /// <para>
    /// So start from everything the host could resolve, then let already-loaded assemblies
    /// override by simple name: compiling against a different file copy of something already
    /// in memory would break type identity when the snippet runs.
    /// </para>
    /// </summary>
    private static IReadOnlyList<MetadataReference> BuildReferences()
    {
        var pathsByName = new Dictionary<string, string>(
            ResolvableAssemblyPaths.Value,
            StringComparer.OrdinalIgnoreCase);

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
            {
                continue;
            }

            string? name = assembly.GetName().Name;
            if (!string.IsNullOrEmpty(name))
            {
                pathsByName[name] = assembly.Location;
            }
        }

        return pathsByName.Values
            .Select(path => ReferenceCache.GetOrAdd(path, static p => MetadataReference.CreateFromFile(p)))
            .ToList();
    }

    /// <summary>
    /// Everything the .NET host is willing to load for this process, by simple assembly name.
    /// <para>
    /// TRUSTED_PLATFORM_ASSEMBLIES is the list hostpolicy built from the app's deps.json plus
    /// its shared frameworks, so it covers app-local and framework assemblies alike, app-local
    /// first. The base-directory scan on top catches anything sitting next to the exe that the
    /// deps.json missed - for instance native DLLs overlaid by the host's version provisioner.
    /// </para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildResolvableAssemblyPaths()
    {
        var pathsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        foreach (string path in (trusted ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            // App-local entries come first, so first one wins.
            pathsByName.TryAdd(Path.GetFileNameWithoutExtension(path), path);
        }

        foreach (KeyValuePair<string, string> local in ScanManagedAssemblies(AppContext.BaseDirectory))
        {
            pathsByName[local.Key] = local.Value;
        }

        return pathsByName;
    }

    /// <summary>
    /// Maps simple assembly name -> path for the managed assemblies in a folder. The
    /// runner's folder is full of native DLLs too (WinUI, MRM, WindowsAppRuntime); those
    /// have no assembly identity and are skipped.
    /// </summary>
    private static Dictionary<string, string> ScanManagedAssemblies(string directory)
    {
        var pathsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(directory, "*.dll"))
        {
            try
            {
                string? name = AssemblyName.GetAssemblyName(path).Name;
                if (!string.IsNullOrEmpty(name))
                {
                    pathsByName[name] = path;
                }
            }
            catch (BadImageFormatException)
            {
                // Native DLL.
            }
            catch (FileLoadException)
            {
                // Locked or unreadable.
            }
        }

        return pathsByName;
    }

    private static string FormatDiagnostics(IEnumerable<Diagnostic> diagnostics, SyntaxTree snippetTree)
    {
        var sb = new StringBuilder();
        foreach (Diagnostic diagnostic in diagnostics)
        {
            if (diagnostic.Location != Location.None)
            {
                FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
                bool isSnippet = diagnostic.Location.SourceTree == snippetTree;
                int line = Math.Max(1, span.StartLinePosition.Line + 1 - (isSnippet ? UsingsLineCount : 0));
                int column = span.StartLinePosition.Character + 1;
                // Generated files and NativeMethods.txt have their own coordinates.
                sb.Append($"{(isSnippet ? string.Empty : span.Path)}({line},{column}): ");
            }

            sb.AppendLine($"{diagnostic.Severity} {diagnostic.Id}: {diagnostic.GetMessage()}");
        }

        return sb.ToString().Trim();
    }
}
