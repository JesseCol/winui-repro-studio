namespace ReproStudio.Shared;

/// <summary>
/// Turns a partial Windows App SDK version written by hand into a real one.
/// <para>
/// Nobody wants to type <c>1.6.250228001</c> into a repro file, and a pinned build number
/// goes stale. So a repro can say <c>// wasdk: 1.6</c> and get the newest 1.6.
/// </para>
/// </summary>
public static class VersionResolver
{
    /// <summary>
    /// Resolves <paramref name="token"/> against <paramref name="available"/> (newest first).
    /// An exact match wins; otherwise the newest version whose dotted segments all start with
    /// the token, so "1.7" finds "1.7.250401001". Falls back to the token as typed, which lets
    /// a caller pass a version that is real but not listed (for example a prerelease when
    /// prerelease listing is off).
    /// </summary>
    public static string Resolve(string token, IReadOnlyList<string> available)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(available);

        if (available.Contains(token, StringComparer.OrdinalIgnoreCase))
        {
            return token;
        }

        string[] wanted = token.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (wanted.Length == 0)
        {
            return token;
        }

        foreach (string candidate in available)
        {
            string[] parts = candidate.Split('.');
            if (wanted.Length > parts.Length)
            {
                continue;
            }

            bool match = true;
            for (int i = 0; i < wanted.Length; i++)
            {
                if (!string.Equals(parts[i], wanted[i], StringComparison.Ordinal))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return candidate;
            }
        }

        return token;
    }
}
