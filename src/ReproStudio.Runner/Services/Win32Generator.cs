using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace ReproStudio_Runner.Services;

/// <summary>Supplies CsWin32 with in-memory inputs and app-local SDK metadata.</summary>
internal static class Win32Generator
{
    private static readonly Regex ApiName = new(
        @"\A[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*\z",
        RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> ReadRequests(string source)
    {
        var requests = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(source);
        int lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // Match Shared.SnippetFileParser's leading // block rule. Stop at the
            // first non-comment line, not at arbitrary matches inside C#/XAML strings.
            if (!trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                break;
            }

            string content = trimmed[2..].Trim();
            int colon = content.IndexOf(':');
            if (colon <= 0 || !content[..colon].Trim().Equals("win32", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Unlike scalar headers, repeated win32 headers append requests.
            // Keep case (Win32 names are case-sensitive), remove exact duplicates.
            foreach (string entry in content[(colon + 1)..].Split(','))
            {
                string name = entry.Trim();
                if (!ApiName.IsMatch(name))
                {
                    throw new FormatException(
                        $"({lineNumber},1): CsWin32 header: expected an API, type or constant name; got \"{name}\".");
                }

                if (seen.Add(name))
                {
                    requests.Add(name);
                }
            }
        }

        return requests;
    }

    // Keep generator loading outside the ordinary snippet path, including JIT inlining.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Compilation Generate(
        CSharpCompilation compilation,
        CSharpParseOptions parseOptions,
        IReadOnlyList<string> requests,
        out ImmutableArray<Diagnostic> diagnostics)
    {
        string metadataPath = Path.Combine(AppContext.BaseDirectory, "CsWin32", "Windows.Win32.winmd");
        if (!File.Exists(metadataPath))
        {
            throw new FileNotFoundException("The Runner bundle is missing CsWin32's Windows.Win32.winmd.", metadataPath);
        }

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new Microsoft.Windows.CsWin32.SourceGenerator() },
            additionalTexts: new AdditionalText[]
            {
                new InMemoryText("NativeMethods.txt", string.Join('\n', requests)),
                // Use JIT runtime marshalling, not LibraryImport/COM generator chaining.
                new InMemoryText("NativeMethods.json",
                    """{"allowMarshaling":true,"comInterop":{"useComSourceGenerators":false}}"""),
            },
            parseOptions: parseOptions,
            optionsProvider: new OptionsProvider(metadataPath));

        driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation generated, out diagnostics);
        return generated;
    }

    private sealed class InMemoryText(string path, string content) : AdditionalText
    {
        private readonly SourceText _text = SourceText.From(content, Encoding.UTF8);

        public override string Path => path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }

    private sealed class OptionsProvider(string metadataPath) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(metadataPath);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Options.Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Options.Empty;
    }

    private sealed class Options(string? metadataPath) : AnalyzerConfigOptions
    {
        public static Options Empty { get; } = new(null);

        public override bool TryGetValue(string key, out string value)
        {
            if (metadataPath is not null &&
                key.Equals("build_property.CsWin32InputMetadataPaths", StringComparison.OrdinalIgnoreCase))
            {
                value = metadataPath;
                return true;
            }

            value = string.Empty;
            return false;
        }
    }
}
