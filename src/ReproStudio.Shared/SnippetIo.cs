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
        => WriteAtomicCore(path, snippet);

    internal static void WriteAtomicCore<T>(string path, T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(value);

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        string json = JsonSerializer.Serialize(value, Options);
        try
        {
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>
    /// Tries to read a snippet. Returns null if the file is missing, locked,
    /// mid-write, or malformed - the caller is expected to retry.
    /// </summary>
    public static Snippet? TryRead(string path) => TryReadCore<Snippet>(path);

    internal static T? TryReadCore<T>(string path) where T : class
    {
        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
