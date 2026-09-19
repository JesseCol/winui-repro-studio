using NuGet.Versioning;

namespace ReproStudio.Shared;

/// <summary>API selection is independent of the native runtime selection.</summary>
public static class SdkSelection
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("match", StringComparison.OrdinalIgnoreCase))
            return "match";
        if (value.Equals("base", StringComparison.OrdinalIgnoreCase)) return "base";
        if (value != value.Trim() || value.Any(char.IsControl) || !NuGetVersion.TryParse(value, out _))
            throw new ArgumentException("SDK/API must be match, base, or a Windows App SDK version.");
        return value;
    }

    public static async Task<string> ResolveAsync(
        string? value, Func<Task<IReadOnlyList<string>>> getVersions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(getVersions);
        string sdk = Normalize(value);
        ct.ThrowIfCancellationRequested();
        if (sdk is "match" or "base" || sdk.Count(c => c == '.') >= 2) return sdk;
        // The host may share its lookup task; cancel this wait without cancelling
        // that lookup for other consumers.
        IReadOnlyList<string> versions = await getVersions().WaitAsync(ct).ConfigureAwait(false);
        return VersionResolver.Resolve(sdk, versions);
    }
}

/// <summary>Verified provisioning metadata, not a claim based on the requested version.</summary>
public sealed class RunnerPairInfo
{
    public string Key { get; set; } = "";
    public string Sdk { get; set; } = "match";
    public string ApiLabel { get; set; } = "";
    public string NativeLabel { get; set; } = "";
    public string? NativeWasdkVersion { get; set; }
    public string RuntimeIdentifier { get; set; } = "";
    public string TargetFramework { get; set; } = "";
    public string WinUiAssemblyIdentity { get; set; } = "";
    public string WinUiFileVersion { get; set; } = "";
    public string WinUiSha256 { get; set; } = "";

    public string Describe() => $"API: {ApiLabel}; native: {NativeLabel}; {RuntimeIdentifier}";
}
