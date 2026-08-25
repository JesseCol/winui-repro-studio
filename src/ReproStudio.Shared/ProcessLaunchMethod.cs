using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReproStudio.Shared;

/// <summary>
/// Finds the optional block-bodied <c>static void OnProcessLaunch()</c> hook without
/// adding Roslyn to the host. Its fingerprint is a launch-time key: changing only this
/// method restarts the runner, while XAML and <c>Setup</c> edits stay live.
/// </summary>
public static class ProcessLaunchMethod
{
    public const string Name = "OnProcessLaunch";

    private static readonly Regex Declaration = new(
        @"\bstatic\s+void\s+OnProcessLaunch\s*\(\s*\)\s*\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns a stable fingerprint of the hook declaration and body, or an empty
    /// string when the file has no supported hook.
    /// </summary>
    public static string GetFingerprint(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        foreach (Match match in Declaration.Matches(source))
        {
            if (!IsCodePosition(source, match.Index))
            {
                continue;
            }

            int openBrace = source.IndexOf('{', match.Index, match.Length);
            int closeBrace = FindMatchingBrace(source, openBrace);
            string methodText = closeBrace >= 0
                ? source[match.Index..(closeBrace + 1)]
                : source[match.Index..];
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(methodText));
            return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
        }

        return string.Empty;
    }

    private static bool IsCodePosition(string source, int target)
    {
        int i = 0;
        while (i < target)
        {
            int start = i;
            if (TrySkipNonCode(source, ref i))
            {
                if (i > target)
                {
                    return false;
                }

                continue;
            }

            i = start + 1;
        }

        return true;
    }

    private static int FindMatchingBrace(string source, int openBrace)
    {
        int depth = 0;
        int i = openBrace;
        while (i < source.Length)
        {
            if (TrySkipNonCode(source, ref i))
            {
                continue;
            }

            switch (source[i])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }

            i++;
        }

        return -1;
    }

    private static bool TrySkipNonCode(string source, ref int i)
    {
        if (source[i] == '/' && i + 1 < source.Length)
        {
            if (source[i + 1] == '/')
            {
                int newline = source.IndexOf('\n', i + 2);
                i = newline < 0 ? source.Length : newline + 1;
                return true;
            }

            if (source[i + 1] == '*')
            {
                int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2;
                return true;
            }
        }

        if (source[i] == '\'')
        {
            i = SkipEscapedString(source, i, '\'');
            return true;
        }

        if (source[i] != '"')
        {
            return false;
        }

        int quoteCount = CountQuotes(source, i);
        if (quoteCount >= 3)
        {
            i = SkipRawString(source, i + quoteCount, quoteCount);
            return true;
        }

        bool verbatim = i > 0 && source[i - 1] == '@'
            || i > 1 && source[i - 2] == '@' && source[i - 1] == '$';
        i = verbatim
            ? SkipVerbatimString(source, i + 1)
            : SkipEscapedString(source, i, '"');
        return true;
    }

    private static int SkipEscapedString(string source, int openingQuote, char quote)
    {
        int i = openingQuote + 1;
        while (i < source.Length)
        {
            if (source[i] == '\\')
            {
                i += Math.Min(2, source.Length - i);
            }
            else if (source[i] == quote)
            {
                return i + 1;
            }
            else
            {
                i++;
            }
        }

        return source.Length;
    }

    private static int SkipVerbatimString(string source, int i)
    {
        while (i < source.Length)
        {
            if (source[i] != '"')
            {
                i++;
                continue;
            }

            if (i + 1 < source.Length && source[i + 1] == '"')
            {
                i += 2;
                continue;
            }

            return i + 1;
        }

        return source.Length;
    }

    private static int SkipRawString(string source, int i, int fence)
    {
        while (i < source.Length)
        {
            if (source[i] != '"')
            {
                i++;
                continue;
            }

            int run = CountQuotes(source, i);
            if (run >= fence)
            {
                return i + run;
            }

            i += run;
        }

        return source.Length;
    }

    private static int CountQuotes(string source, int start)
    {
        int count = 0;
        while (start + count < source.Length && source[start + count] == '"')
        {
            count++;
        }

        return count;
    }
}
