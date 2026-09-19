using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReproStudio.Shared;

public sealed class RunnerPairManifest
{
    public const int CurrentSchema = 2;
    public const string FileName = "runner-pair.json";
    private static readonly HashSet<string> RegistrationFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppxManifest.xml",
        Path.Combine("Assets", "StoreLogo.png"),
        Path.Combine("Assets", "Square44x44Logo.png"),
        Path.Combine("Assets", "Square150x150Logo.png"),
    };
    public int Schema { get; set; } = CurrentSchema;
    public string SelectionKey { get; set; } = "";
    public string BaseIdentity { get; set; } = "";
    public RunnerPairInfo Pair { get; set; } = new();
    public string[] Packages { get; set; } = [];
    public Dictionary<string, string> Assets { get; set; } = new();
    public Dictionary<string, string> Files { get; set; } = new();

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static Dictionary<string, string> Inventory(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(p =>
            {
                string relative = Path.GetRelativePath(directory, p);
                // Loose package registration stages/removes these without changing the SDK/runtime pair.
                return !relative.Equals(FileName, StringComparison.OrdinalIgnoreCase)
                    && !RegistrationFiles.Contains(relative);
            })
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => Path.GetRelativePath(directory, p), HashFile, StringComparer.OrdinalIgnoreCase);

    public static string ContentIdentity(IReadOnlyDictionary<string, string> files) =>
        HashText(string.Join("\n", files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.Key.ToLowerInvariant() + "=" + f.Value)));

    public static RunnerPairManifest? Read(string directory) =>
        SnippetIo.TryReadCore<RunnerPairManifest>(Path.Combine(directory, FileName));

    public void Save(string directory) => SnippetIo.WriteAtomicCore(Path.Combine(directory, FileName), this);

    public bool IsValid(string directory)
    {
        if (Schema != CurrentSchema || Files.Count == 0) return false;
        try
        {
            var actual = Inventory(directory);
            return actual.Count == Files.Count && Files.All(f => actual.TryGetValue(f.Key, out string? hash) && hash == f.Value)
                && Assets.All(a => !File.Exists(a.Key) || HashFile(a.Key) == a.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
