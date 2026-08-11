using System.Security.Cryptography;
using System.Text;

namespace ReproStudio.Shared;

/// <summary>
/// A folder of loose files to copy over a provisioned runner, on top of whatever
/// Windows App SDK version was resolved.
/// <para>
/// This is the "drop a DLL in and run it" path. Provisioning a runner is really just
/// "copy the base runner, then copy a version's native files over it", so testing a
/// private build needs nothing more than one more copy on the end. Drop
/// <c>Microsoft.ui.xaml.dll</c> (or any other runtime binary) into the folder and it
/// wins over the stock file of the same name.
/// </para>
/// <para>
/// Files keep their relative paths, so a subfolder such as <c>Microsoft.UI.Xaml\</c>
/// (the themes directory) works the same way as a loose DLL. Nothing is renamed and
/// nothing is validated - if you drop in a binary that does not load, the runner will
/// fail to start and say so.
/// </para>
/// <para>
/// <c>.txt</c> and <c>.md</c> files are ignored so the folder can carry a README
/// without that counting as content. A folder holding only those is treated as empty.
/// </para>
/// </summary>
public sealed class RunnerPayload
{
    /// <summary>Name of the drop folder looked for next to the host exe.</summary>
    public const string DefaultFolderName = "payload";

    /// <summary>Written into a provisioned runner to record which payload it holds.</summary>
    public const string StampFileName = ".payload-stamp";

    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md" };

    /// <summary>The source folder, as an absolute path.</summary>
    public required string Directory { get; init; }

    /// <summary>Paths of the files to copy, relative to <see cref="Directory"/>.</summary>
    public required IReadOnlyList<string> RelativePaths { get; init; }

    /// <summary>
    /// Identifies this exact set of files. Changes when a file is added, removed,
    /// rebuilt or resized, which is what triggers a re-provision.
    /// </summary>
    public required string Fingerprint { get; init; }

    /// <summary>
    /// Reads a drop folder. Returns null when the folder is missing or has no files
    /// worth copying, so "no payload" and "an empty payload folder" behave the same.
    /// </summary>
    public static RunnerPayload? FromDirectory(string? directory)
    {
        if (directory is not { Length: > 0 } || !System.IO.Directory.Exists(directory))
        {
            return null;
        }

        string root = Path.GetFullPath(directory);
        List<string> relative = [];
        foreach (string file in System.IO.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (IgnoredExtensions.Contains(Path.GetExtension(file)))
            {
                continue;
            }

            relative.Add(Path.GetRelativePath(root, file));
        }

        if (relative.Count == 0)
        {
            return null;
        }

        // Sorted so the fingerprint does not depend on enumeration order.
        relative.Sort(StringComparer.OrdinalIgnoreCase);

        return new RunnerPayload
        {
            Directory = root,
            RelativePaths = relative,
            Fingerprint = ComputeFingerprint(root, relative),
        };
    }

    /// <summary>
    /// Copies the payload over an already-prepared runner folder and records what was
    /// applied, so a later run can tell whether the folder is still current.
    /// </summary>
    public void ApplyTo(string runnerDir)
    {
        ArgumentNullException.ThrowIfNull(runnerDir);

        foreach (string relative in RelativePaths)
        {
            string source = Path.Combine(Directory, relative);
            string target = Path.Combine(runnerDir, relative);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }

        File.WriteAllText(Path.Combine(runnerDir, StampFileName), Fingerprint);
    }

    /// <summary>
    /// True when <paramref name="runnerDir"/> already holds exactly this payload (or
    /// no payload, when <paramref name="payload"/> is null). A mismatch means the
    /// folder has to be rebuilt: overwritten files cannot be un-overwritten in place.
    /// </summary>
    public static bool Matches(string runnerDir, RunnerPayload? payload)
    {
        string stampPath = Path.Combine(runnerDir, StampFileName);
        if (payload is null)
        {
            return !File.Exists(stampPath);
        }

        try
        {
            return File.Exists(stampPath)
                && File.ReadAllText(stampPath).Trim() == payload.Fingerprint;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Hashes the file list plus each file's size and write time. Deliberately does not
    /// read file contents: a payload is often tens of megabytes and this runs on every
    /// launch, whereas size and timestamp already move on every rebuild.
    /// </summary>
    private static string ComputeFingerprint(string root, IEnumerable<string> relativePaths)
    {
        StringBuilder sb = new();
        foreach (string relative in relativePaths)
        {
            FileInfo info = new(Path.Combine(root, relative));
            sb.Append(relative.ToLowerInvariant())
              .Append('|').Append(info.Length)
              .Append('|').Append(info.LastWriteTimeUtc.Ticks)
              .Append('\n');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }
}
