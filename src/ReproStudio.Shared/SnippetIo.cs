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
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    // ReplaceFile preserves open delete-sharing reader handles.
                    // MoveFileEx(REPLACE_EXISTING) can return access denied instead.
                    if (File.Exists(path)) File.Replace(temp, path, destinationBackupFileName: null);
                    else File.Move(temp, path);
                    break;
                }
                catch (IOException ex) when (attempt < 9 && IsRetryableReplaceError(ex.HResult & 0xffff))
                {
                    Thread.Sleep(20);
                }
                catch (UnauthorizedAccessException) when (attempt < 9)
                {
                    Thread.Sleep(20);
                }
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    // ReplaceFile error 1175 leaves both original names intact, so retrying is
    // safe. Errors 1176/1177 can move/delete one name and must not be retried here.
    internal static bool IsRetryableReplaceError(int error) =>
        error is 2 or 5 or 32 or 33 or 80 or 183 or 1175;

    /// <summary>
    /// Tries to read a snippet. Returns null if the file is missing, locked,
    /// mid-write, or malformed - the caller is expected to retry.
    /// </summary>
    public static Snippet? TryRead(string path) => TryReadCore<Snippet>(path);

    internal static T? TryReadCore<T>(string path) where T : class
    {
        try
        {
            // A polling reader must not prevent Windows from atomically replacing
            // request/control/result files during a Runner restart.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
