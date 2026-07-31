using System.Security.Cryptography;

namespace ReproStudio.Shared;

/// <summary>
/// How to override just the WinUI component when provisioning a runner: either a
/// specific NuGet version of Microsoft.WindowsAppSDK.WinUI, or a local .nupkg file
/// (e.g. a private WinUI build). The <see cref="CacheKey"/> makes the provisioned
/// folder unique per choice.
/// </summary>
public sealed class WinUiOverride
{
    public string? NuGetVersion { get; init; }

    public string? LocalNupkgPath { get; init; }

    public required string CacheKey { get; init; }

    public static WinUiOverride ForVersion(string version) => new()
    {
        NuGetVersion = version,
        CacheKey = "winui-" + version,
    };

    public static WinUiOverride ForLocalPackage(string nupkgPath) => new()
    {
        LocalNupkgPath = nupkgPath,
        CacheKey = "winui-local-" + HashFile(nupkgPath),
    };

    /// <summary>Short content hash so a rebuilt local package re-provisions.</summary>
    public static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }
}
