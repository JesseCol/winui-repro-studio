using NuGet.Protocol.Core.Types;

namespace ReproStudio_Cli;

internal static class WasdkVersionList
{
    public static bool IsExpectedFailure(Exception exception) =>
        exception is HttpRequestException or IOException or UnauthorizedAccessException
            or InvalidOperationException or OperationCanceledException or NuGetProtocolException;

    public static void Print(
        IReadOnlyList<string> versions,
        string cacheRoot,
        bool includePrerelease,
        string? currentVersion = null)
    {
        string provisionedRoot = Path.Combine(cacheRoot, "versions");
        HashSet<string> provisioned = Directory.Exists(provisionedRoot)
            ? Directory.GetDirectories(provisionedRoot)
                .Select(d => Path.GetFileName(d).Split("__")[0])
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        Log.Step("windows app sdk" + (includePrerelease ? " (including prerelease)" : string.Empty));
        if (versions.Count == 0)
        {
            Log.Warn("No WASDK versions were returned by the configured NuGet feeds.");
            return;
        }

        foreach (string version in versions)
        {
            string? note = string.Equals(version, currentVersion, StringComparison.OrdinalIgnoreCase)
                ? "current"
                : provisioned.Contains(version) ? "on disk" : null;
            Log.Field(string.Empty, version, note);
        }

        Log.Blank();
        Log.Detail(versions.Count + " versions. Set '// wasdk: 2.2' in your repro for the newest 2.2, "
            + "or copy an exact version from this list.");
    }
}
