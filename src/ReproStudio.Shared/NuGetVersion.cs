namespace ReproStudio.Shared;

/// <summary>
/// Tiny version helper for sorting NuGet version strings like "1.8.260529003".
/// Compares dotted numeric segments; keeps the original string for display.
/// Understands SemVer prerelease labels (the part after a '-'): a stable build
/// outranks its own prerelease, and two prereleases compare by label text.
/// </summary>
public readonly struct NuGetVersion : IComparable<NuGetVersion>
{
    private readonly long[] _segments;
    private readonly string _prerelease;

    private NuGetVersion(string original, long[] segments, string prerelease)
    {
        Original = original;
        _segments = segments;
        _prerelease = prerelease;
    }

    public string Original { get; }

    public static NuGetVersion Parse(string version)
    {
        ArgumentNullException.ThrowIfNull(version);

        // Split off the SemVer prerelease label (everything after the first '-') so
        // the numeric core sorts on its own, e.g. "1.8.250515003-experimental1".
        int dash = version.IndexOf('-', StringComparison.Ordinal);
        string numericPart = dash < 0 ? version : version[..dash];
        string prerelease = dash < 0 ? string.Empty : version[(dash + 1)..];

        long[] segments = numericPart
            .Split('.')
            .Select(part => long.TryParse(part, out long value) ? value : 0L)
            .ToArray();
        return new NuGetVersion(version, segments, prerelease);
    }

    public int CompareTo(NuGetVersion other)
    {
        int length = Math.Max(_segments.Length, other._segments.Length);
        for (int i = 0; i < length; i++)
        {
            long left = i < _segments.Length ? _segments[i] : 0L;
            long right = i < other._segments.Length ? other._segments[i] : 0L;
            int compared = left.CompareTo(right);
            if (compared != 0)
            {
                return compared;
            }
        }

        // Same numeric version: a stable build (no label) beats any prerelease, and
        // two prereleases compare by label so "preview1" sorts before "preview2".
        bool leftStable = _prerelease.Length == 0;
        bool rightStable = other._prerelease.Length == 0;
        if (leftStable != rightStable)
        {
            return leftStable ? 1 : -1;
        }

        return string.CompareOrdinal(_prerelease, other._prerelease);
    }
}
