using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

/// <summary>
/// Matches actual tool versions against declared version constraints.
/// </summary>
public static class VersionConstraintMatcher
{
    /// <summary>
    /// Returns true if the actual version satisfies the declared constraint.
    /// </summary>
    public static bool Matches(string? constraint, string actualVersion)
    {
        if (string.IsNullOrWhiteSpace(constraint)) return true;
        if (constraint.Equals("latest", StringComparison.OrdinalIgnoreCase)) return true;

        // Handle comma-separated compound constraints: ">=3.12,<4.0"
        if (constraint.Contains(','))
        {
            return constraint.Split(',')
                .All(part => Matches(part.Trim(), actualVersion));
        }

        // Range operators
        if (constraint.StartsWith(">="))
            return CompareVersions(actualVersion, constraint[2..].Trim()) >= 0;
        if (constraint.StartsWith('>'))
            return CompareVersions(actualVersion, constraint[1..].Trim()) > 0;
        if (constraint.StartsWith("<="))
            return CompareVersions(actualVersion, constraint[2..].Trim()) <= 0;
        if (constraint.StartsWith('<'))
            return CompareVersions(actualVersion, constraint[1..].Trim()) < 0;

        // Wildcard patterns: "8.0.*", "8.*"
        if (constraint.Contains('*') || constraint.Contains('x', StringComparison.OrdinalIgnoreCase))
        {
            var pattern = constraint.Replace("*", "").Replace("x", "").Replace("X", "").TrimEnd('.');
            return actualVersion.StartsWith(pattern + ".", StringComparison.Ordinal) ||
                   actualVersion == pattern;
        }

        // Exact match
        return actualVersion == constraint;
    }

    /// <summary>
    /// Classifies the severity of a version change.
    /// </summary>
    public static ChangeSeverity ClassifyChange(string previousVersion, string newVersion)
    {
        var prev = ParseVersionParts(previousVersion);
        var next = ParseVersionParts(newVersion);

        if (prev.Major != next.Major) return ChangeSeverity.Major;
        if (prev.Minor != next.Minor) return ChangeSeverity.Minor;
        if (prev.Patch != next.Patch) return ChangeSeverity.Patch;
        return ChangeSeverity.Build;
    }

    private static int CompareVersions(string a, string b)
    {
        var pa = ParseVersionParts(a);
        var pb = ParseVersionParts(b);

        var cmp = pa.Major.CompareTo(pb.Major);
        if (cmp != 0) return cmp;
        cmp = pa.Minor.CompareTo(pb.Minor);
        if (cmp != 0) return cmp;
        return pa.Patch.CompareTo(pb.Patch);
    }

    private static (int Major, int Minor, int Patch) ParseVersionParts(string version)
    {
        // Strip leading 'v' if present
        var v = version.TrimStart('v');

        // Strip non-numeric suffixes (e.g., "9.6p1" → "9.6")
        var parts = v.Split('.');
        return (
            ParseInt(parts.Length > 0 ? parts[0] : "0"),
            ParseInt(parts.Length > 1 ? parts[1] : "0"),
            ParseInt(parts.Length > 2 ? parts[2] : "0")
        );
    }

    private static int ParseInt(string s)
    {
        // Take only leading digits (handles "6p1", "404-beta", etc.)
        var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }
}
