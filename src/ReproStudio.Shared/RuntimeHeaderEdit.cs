using System.Text;
using NuGet.Versioning;

namespace ReproStudio.Shared;

/// <summary>
/// A byte-for-byte source snapshot. Provision without holding an editor lock; commit
/// only if the original bytes still match, under a short exclusive file handle.
/// </summary>
public sealed class RuntimeHeaderEdit
{
    private readonly byte[] _original;
    private readonly Encoding _encoding;
    private readonly int _preambleLength;

    private RuntimeHeaderEdit(byte[] original)
    {
        _original = original;
        (_encoding, _preambleLength) = DetectEncoding(original);
        Text = _encoding.GetString(original, _preambleLength, original.Length - _preambleLength);
    }

    public string Text { get; }

    public static RuntimeHeaderEdit Read(string path)
    {
        // Preflight writable access as well as encoding before provisioning.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return new RuntimeHeaderEdit(memory.ToArray());
    }

    public static RuntimeHeaderEdit Read(string path, string expectedSourceText)
    {
        ArgumentNullException.ThrowIfNull(expectedSourceText);
        RuntimeHeaderEdit edit = Read(path);
        if (!string.Equals(edit.Text, expectedSourceText, StringComparison.Ordinal))
            throw new IOException("The repro changed since this preview was sent. No headers were saved. Reopen Runtime and retry.");
        return edit;
    }

    public static void ValidateChoice(string kind, string value)
    {
        if (kind is not ("wasdk" or "winui") || string.IsNullOrWhiteSpace(value)
            || value != value.Trim() || value.Any(char.IsControl))
        {
            throw new ArgumentException("Choose either a Windows App SDK version or a WinUI version/package.");
        }

        if (kind == "winui" && value.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            if (!Path.IsPathFullyQualified(value))
                throw new ArgumentException("Browse to an absolute .nupkg path.");
        }
        else if (!NuGetVersion.TryParse(value, out _))
        {
            throw new ArgumentException("Select a valid published package version.");
        }
    }

    public static string Rewrite(string text, string kind, string value, string? sdk = null)
    {
        ValidateChoice(kind, value);
        sdk = SdkSelection.Normalize(sdk);
        string newline = "\n";
        int firstNewline = text.IndexOfAny(['\r', '\n']);
        if (firstNewline >= 0)
            newline = text[firstNewline] == '\r'
                ? (firstNewline + 1 < text.Length && text[firstNewline + 1] == '\n' ? "\r\n" : "\r")
                : "\n";

        var result = new StringBuilder("// " + kind + ": " + value + newline);
        if (sdk != "match") result.Append("// sdk: ").Append(sdk).Append(newline);
        bool inHeader = true;
        for (int start = 0; start < text.Length;)
        {
            int end = start;
            while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
            int next = end;
            if (next < text.Length && text[next++] == '\r' && next < text.Length && text[next] == '\n') next++;
            string trimmed = text[start..end].Trim();
            bool remove = false;
            if (inHeader && trimmed.Length > 0)
            {
                if (!trimmed.StartsWith("//", StringComparison.Ordinal)) inHeader = false;
                else
                {
                    string comment = trimmed[2..].Trim();
                    int colon = comment.IndexOf(':');
                    string key = colon < 0 ? string.Empty : comment[..colon].Trim();
                    remove = key.Equals("wasdk", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("winui", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("sdk", StringComparison.OrdinalIgnoreCase);
                }
            }
            if (!remove) result.Append(text, start, next - start);
            start = next;
        }
        return result.ToString();
    }

    public string Commit(string path, string kind, string value, string? sdk = null)
    {
        string text = Rewrite(Text, kind, value, sdk);
        byte[] body = _encoding.GetBytes(text);
        byte[] replacement = new byte[_preambleLength + body.Length];
        _original.AsSpan(0, _preambleLength).CopyTo(replacement);
        body.CopyTo(replacement, _preambleLength);

        // FileShare.None excludes both in-place writes and editor replace/rename saves.
        // A read-compare followed by File.Move would leave a race that loses editor data.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        if (!memory.ToArray().AsSpan().SequenceEqual(_original))
            throw new IOException("The repro changed while preparing the runtime. No headers were saved. Reopen Runtime and retry.");

        if (replacement.AsSpan().SequenceEqual(_original)) return text;
        try
        {
            Write(replacement);
        }
        catch (IOException)
        {
            // Still exclusive: a best-effort rollback cannot overwrite another editor.
            Write(_original);
            throw;
        }
        return text;

        void Write(byte[] bytes)
        {
            stream.Position = 0;
            stream.Write(bytes);
            stream.SetLength(bytes.Length);
            stream.Flush(flushToDisk: true);
        }
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) return (new UTF32Encoding(false, true, true), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) return (new UTF32Encoding(true, true, true), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return (new UnicodeEncoding(false, true, true), 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return (new UnicodeEncoding(true, true, true), 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return (new UTF8Encoding(true, true), 3);
        // Refuse undecodable legacy bytes rather than silently substituting/re-encoding them.
        return (new UTF8Encoding(false, true), 0);
    }
}
