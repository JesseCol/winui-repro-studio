using System.Text.Json;

namespace ReproStudio.Shared;

/// <summary>
/// Reads and writes <see cref="Snippet"/> files as JSON. Writes go to a temp file
/// then rename, which is atomic on the same volume, so a reader never sees a
/// half-written file.
/// </summary>
public static class SnippetIo
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static void WriteAtomic(string path, Snippet snippet)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(snippet);

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        string json = JsonSerializer.Serialize(snippet, Options);
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Tries to read a snippet. Returns null if the file is missing, locked,
    /// mid-write, or malformed - the caller is expected to retry.
    /// </summary>
    public static Snippet? TryRead(string path)
    {
        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Snippet>(json, Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
