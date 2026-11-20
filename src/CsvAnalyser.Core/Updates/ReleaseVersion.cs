namespace CsvAnalyser.Core.Updates;

/// <summary>
/// Versions of published releases: a GitHub tag ("v5.4", "5.4.7", "v5.5.0-beta") compared with the running
/// version ("5.4.7"). Missing parts count as 0, so "v5.4" is 5.4.0.
/// </summary>
public static class ReleaseVersion
{
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;
        string s = text.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0)
            s = s[..cut];
        if (!Version.TryParse(s.Contains('.') ? s : s + ".0", out var parsed))
            return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return true;
    }

    /// <summary>True when <paramref name="releaseTag"/> is a later version than <paramref name="current"/> (false if either cannot be read).</summary>
    public static bool IsNewer(string? releaseTag, string? current) =>
        TryParse(releaseTag, out var release) && TryParse(current, out var running) && release > running;
}
