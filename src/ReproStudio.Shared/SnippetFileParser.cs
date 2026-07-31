using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ReproStudio.Shared;

/// <summary>
/// The parsed pieces of a single-file repro (a <c>.cs</c> file an external editor
/// owns). Launch-time fields (<see cref="WasdkVersion"/>, <see cref="WinUiToken"/>)
/// decide which runner exe to run; the rest are live and just get written to the
/// request file.
/// </summary>
public sealed class ParsedSnippetFile
{
    /// <summary>Optional friendly name from the header (<c>// repro:</c>).</summary>
    public string? Title { get; init; }

    /// <summary>Windows App SDK version from the header (<c>// wasdk:</c>), or null.</summary>
    public string? WasdkVersion { get; init; }

    /// <summary>
    /// WinUI override from the header (<c>// winui:</c>): a version, a path to a
    /// local <c>.nupkg</c>, or null when the header says "default" / is missing.
    /// </summary>
    public string? WinUiToken { get; init; }

    /// <summary>
    /// Launch-time: whether to give the runner package identity (<c>// packaged:</c>).
    /// Null when the header does not say, so a command-line default can win.
    /// </summary>
    public bool? Packaged { get; init; }

    /// <summary>Launch-time process DPI for the runner (<c>// dpi:</c>), or null.</summary>
    public int? Dpi { get; init; }

    /// <summary>Live theme: Default | Light | Dark.</summary>
    public string Theme { get; init; } = "Default";

    /// <summary>Live flow direction: LeftToRight | RightToLeft.</summary>
    public string FlowDirection { get; init; } = "LeftToRight";

    /// <summary>Live stage background from <c>// background:</c>, e.g. "#202020".</summary>
    public string? Background { get; init; }

    /// <summary>Live: keep the runner window above other windows (<c>// topmost:</c>).</summary>
    public bool Topmost { get; init; }

    /// <summary>The XAML pulled from the file's <c>string Xaml</c> literal.</summary>
    public string Xaml { get; init; } = string.Empty;

    /// <summary>The whole file, handed to the runner as C# exactly as today.</summary>
    public string CSharp { get; init; } = string.Empty;

    /// <summary>True when a <c>string Xaml</c> literal was found (even if empty).</summary>
    public bool HasXaml { get; init; }
}

/// <summary>
/// Turns a single-file repro (<c>.cs</c>) into its parts. The file carries a small
/// <c>// key: value</c> header at the top and puts the markup in a
/// <c>const string Xaml = """..."""</c> literal, so the whole file stays valid C#.
/// Parsing is plain text - no Roslyn - to keep the host light.
/// </summary>
public static class SnippetFileParser
{
    private static readonly Regex XamlDeclaration =
        new(@"\bstring\s+Xaml\s*=\s*", RegexOptions.Compiled);

    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "repro", "title", "wasdk", "winui", "packaged", "dpi",
        "theme", "flow", "background", "topmost",
    };

    /// <summary>Parses the file text into its header + XAML + C# parts.</summary>
    public static ParsedSnippetFile Parse(string fileText)
    {
        ArgumentNullException.ThrowIfNull(fileText);

        Dictionary<string, string> header = ReadHeader(fileText);
        bool hasXaml = TryExtractXaml(fileText, out string xaml);

        string? title = Get(header, "repro") ?? Get(header, "title");
        string? winuiToken = NormalizeWinUi(Get(header, "winui"));

        return new ParsedSnippetFile
        {
            Title = title,
            WasdkVersion = Get(header, "wasdk"),
            WinUiToken = winuiToken,
            Packaged = ParseBool(Get(header, "packaged")),
            Dpi = ParseDpi(Get(header, "dpi")),
            Theme = NormalizeTheme(Get(header, "theme")),
            FlowDirection = NormalizeFlow(Get(header, "flow")),
            Background = Get(header, "background"),
            Topmost = ParseBool(Get(header, "topmost")) ?? false,
            Xaml = xaml,
            CSharp = fileText,
            HasXaml = hasXaml,
        };
    }

    /// <summary>
    /// Reads the leading <c>// key: value</c> block. Blank lines are allowed inside
    /// it; the first line of real code ends the header. Only known keys are kept,
    /// and the first value for a key wins.
    /// </summary>
    private static Dictionary<string, string> ReadHeader(string fileText)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(fileText);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                break;
            }

            string content = trimmed[2..].Trim();
            int colon = content.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            string key = content[..colon].Trim();
            string value = content[(colon + 1)..].Trim();
            if (KnownKeys.Contains(key) && !map.ContainsKey(key))
            {
                map[key] = value;
            }
        }

        return map;
    }

    private static string? Get(Dictionary<string, string> header, string key) =>
        header.TryGetValue(key, out string? value) && value.Length > 0 ? value : null;

    private static string NormalizeTheme(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "light" => "Light",
        "dark" => "Dark",
        _ => "Default",
    };

    /// <summary>
    /// Reads a yes/no header value. Returns null when the key is absent, so a caller can
    /// tell "the file did not say" from "the file said no" and let a command-line flag win.
    /// </summary>
    private static bool? ParseBool(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "on" or "1" => true,
        "false" or "no" or "off" or "0" => false,
        _ => null,
    };

    /// <summary>Reads a DPI value, ignoring anything outside the range the runner accepts.</summary>
    private static int? ParseDpi(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int dpi)
        && dpi is >= 100 and <= 400
            ? dpi
            : null;

    private static string NormalizeFlow(string? value)
    {
        string v = value?.Trim() ?? string.Empty;
        return string.Equals(v, "RightToLeft", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "rtl", StringComparison.OrdinalIgnoreCase)
            ? "RightToLeft"
            : "LeftToRight";
    }

    private static string? NormalizeWinUi(string? value) =>
        value is null || string.Equals(value, "default", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;

    /// <summary>
    /// Finds the <c>string Xaml</c> literal and returns its decoded value. Handles
    /// raw strings (<c>"""..."""</c>), verbatim strings (<c>@"..."</c>), and regular
    /// strings (<c>"..."</c>). Returns false when no such literal is present.
    /// </summary>
    private static bool TryExtractXaml(string text, out string xaml)
    {
        xaml = string.Empty;

        Match match = XamlDeclaration.Match(text);
        if (!match.Success)
        {
            return false;
        }

        int i = match.Index + match.Length;
        if (i >= text.Length)
        {
            return false;
        }

        if (text[i] == '@' && i + 1 < text.Length && text[i + 1] == '"')
        {
            xaml = ReadVerbatim(text, i + 2);
            return true;
        }

        if (text[i] != '"')
        {
            return false;
        }

        int quotes = CountQuotes(text, i);
        xaml = quotes >= 3
            ? ReadRaw(text, i, quotes)
            : ReadRegular(text, i + 1);
        return true;
    }

    private static int CountQuotes(string text, int start)
    {
        int count = 0;
        while (start + count < text.Length && text[start + count] == '"')
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Reads a raw string literal body (content between two runs of
    /// <paramref name="fence"/> quotes) and removes the common indentation, matching
    /// how C# processes multi-line raw strings.
    /// </summary>
    private static string ReadRaw(string text, int openStart, int fence)
    {
        int contentStart = openStart + fence;
        int j = contentStart;
        while (j < text.Length)
        {
            if (text[j] == '"')
            {
                int run = CountQuotes(text, j);
                if (run >= fence)
                {
                    return Dedent(text[contentStart..j]);
                }

                j += run;
            }
            else
            {
                j++;
            }
        }

        return Dedent(text[contentStart..]);
    }

    private static string Dedent(string raw)
    {
        raw = raw.Replace("\r\n", "\n", StringComparison.Ordinal)
                 .Replace('\r', '\n');
        if (!raw.Contains('\n', StringComparison.Ordinal))
        {
            return raw;
        }

        var lines = new List<string>(raw.Split('\n'));

        // The opening fence is followed by a newline, so the first line is blank.
        if (lines.Count > 0 && lines[0].Trim().Length == 0)
        {
            lines.RemoveAt(0);
        }

        // The last line is the whitespace before the closing fence; its width is the
        // common indent C# strips from every line.
        string indent = lines.Count > 0 ? lines[^1] : string.Empty;
        if (lines.Count > 0 && lines[^1].Trim().Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        for (int k = 0; k < lines.Count; k++)
        {
            lines[k] = indent.Length > 0 && lines[k].StartsWith(indent, StringComparison.Ordinal)
                ? lines[k][indent.Length..]
                : lines[k].TrimStart();
        }

        return string.Join('\n', lines);
    }

    private static string ReadVerbatim(string text, int start)
    {
        var sb = new StringBuilder();
        int j = start;
        while (j < text.Length)
        {
            char c = text[j];
            if (c == '"')
            {
                if (j + 1 < text.Length && text[j + 1] == '"')
                {
                    sb.Append('"');
                    j += 2;
                    continue;
                }

                break;
            }

            sb.Append(c);
            j++;
        }

        return sb.ToString();
    }

    private static string ReadRegular(string text, int start)
    {
        var sb = new StringBuilder();
        int j = start;
        while (j < text.Length)
        {
            char c = text[j];
            if (c == '\\' && j + 1 < text.Length)
            {
                char next = text[j + 1];
                sb.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '0' => '\0',
                    _ => next,
                });
                j += 2;
                continue;
            }

            if (c == '"')
            {
                break;
            }

            sb.Append(c);
            j++;
        }

        return sb.ToString();
    }
}
