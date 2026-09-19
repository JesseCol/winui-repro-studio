using System.Text.Json;

namespace ReproStudio.Shared;

/// <summary>Independent of snippets, versions and launch identity. Last saved choice wins.</summary>
public sealed class RunnerPreferences
{
    public bool IsPinned { get; set; }

    public static string GetPath(string cacheRoot) => Path.GetFullPath(Path.Combine(cacheRoot, "runner-preferences.json"));

    public static RunnerPreferences Load(string path)
    {
        try
        {
            // Share deletion so an atomic writer never observes a partially written file.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<RunnerPreferences>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? throw new JsonException("The preference file must contain a JSON object.");
        }
        catch (FileNotFoundException)
        {
            return new RunnerPreferences();
        }
        catch (DirectoryNotFoundException)
        {
            return new RunnerPreferences();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Serialize independent Runners as well as threads. The lock file can remain;
        // only its open handle owns the lock, including after a crash.
        for (int attempt = 0; ; attempt++)
        {
            FileStream lease;
            try
            {
                lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 40)
            {
                Thread.Sleep(50);
                continue;
            }
            using (lease) SnippetIo.WriteAtomicCore(path, this);
            return;
        }
    }
}
