namespace Andy.Containers.Api.Services;

public static class VersionComparer
{
    public static int[] ParseVersion(string version)
    {
        return version.Split('.', '-')
            .Select(p => int.TryParse(p, out var v) ? v : 0)
            .ToArray();
    }

    public static int Compare(string a, string b)
    {
        var aParts = ParseVersion(a);
        var bParts = ParseVersion(b);
        var len = Math.Max(aParts.Length, bParts.Length);
        for (int i = 0; i < len; i++)
        {
            var av = i < aParts.Length ? aParts[i] : 0;
            var bv = i < bParts.Length ? bParts[i] : 0;
            if (av != bv) return av.CompareTo(bv);
        }
        return 0;
    }

    public static string ClassifySeverity(string oldVersion, string newVersion)
    {
        var oldParts = ParseVersion(oldVersion);
        var newParts = ParseVersion(newVersion);

        if (oldParts.Length > 0 && newParts.Length > 0 && oldParts[0] != newParts[0])
            return "Major";
        if (oldParts.Length > 1 && newParts.Length > 1 && oldParts[1] != newParts[1])
            return "Minor";
        return "Patch";
    }

    public static bool SatisfiesConstraint(string constraint, string actualVersion)
    {
        if (string.IsNullOrWhiteSpace(constraint)) return false;
        if (constraint == "latest") return true;

        // Exact match
        if (!constraint.Contains('*') && !constraint.Contains('x') && !constraint.Contains(',')
            && !constraint.StartsWith('>') && !constraint.StartsWith('<') && !constraint.StartsWith('='))
            return constraint == actualVersion;

        // Wildcard
        if (constraint.Contains('*') || constraint.Contains('x'))
            return MatchesWildcard(constraint, actualVersion);

        // Range
        if (constraint.Contains(',') || constraint.StartsWith(">=") || constraint.StartsWith(">")
            || constraint.StartsWith("<=") || constraint.StartsWith("<"))
            return MatchesRange(constraint, actualVersion);

        return constraint == actualVersion;
    }

    private static bool MatchesWildcard(string pattern, string version)
    {
        var patternParts = pattern.Replace('x', '*').Split('.');
        var versionParts = version.Split('.');
        for (int i = 0; i < patternParts.Length; i++)
        {
            if (patternParts[i] == "*") continue;
            if (i >= versionParts.Length) return false;
            if (patternParts[i] != versionParts[i]) return false;
        }
        return true;
    }

    private static bool MatchesRange(string range, string version)
    {
        var parts = range.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (!EvaluateComparison(part, version)) return false;
        }
        return true;
    }

    private static bool EvaluateComparison(string comparison, string version)
    {
        string op;
        string target;
        if (comparison.StartsWith(">=")) { op = ">="; target = comparison[2..]; }
        else if (comparison.StartsWith("<=")) { op = "<="; target = comparison[2..]; }
        else if (comparison.StartsWith('>')) { op = ">"; target = comparison[1..]; }
        else if (comparison.StartsWith('<')) { op = "<"; target = comparison[1..]; }
        else if (comparison.StartsWith('=')) { op = "="; target = comparison[1..]; }
        else return comparison == version;

        var cmp = Compare(version, target);
        return op switch
        {
            ">=" => cmp >= 0,
            "<=" => cmp <= 0,
            ">" => cmp > 0,
            "<" => cmp < 0,
            "=" => cmp == 0,
            _ => false
        };
    }
}
