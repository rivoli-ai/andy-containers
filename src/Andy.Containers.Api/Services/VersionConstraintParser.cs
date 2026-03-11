using System.Text.RegularExpressions;

namespace Andy.Containers.Api.Services;

public static partial class VersionConstraintParser
{
    public static bool IsValid(string? constraint)
    {
        if (string.IsNullOrWhiteSpace(constraint)) return false;
        if (constraint == "latest") return true;
        // Exact version: 3.12.1 or 8.0.404
        if (ExactVersionRegex().IsMatch(constraint)) return true;
        // Wildcard: 8.0.* or 8.* or 3.12.x
        if (WildcardVersionRegex().IsMatch(constraint)) return true;
        // Range: >=3.12,<4.0 or >=1.0
        if (IsValidRange(constraint)) return true;
        return false;
    }

    public static bool Matches(string constraint, string version)
    {
        if (string.IsNullOrWhiteSpace(constraint) || string.IsNullOrWhiteSpace(version)) return false;
        if (constraint == "latest") return true;

        // Exact match
        if (ExactVersionRegex().IsMatch(constraint) && !constraint.Contains('*') && !constraint.Contains('x'))
            return constraint == version;

        // Wildcard match
        if (constraint.Contains('*') || constraint.Contains('x'))
            return MatchesWildcard(constraint, version);

        // Range match
        if (constraint.Contains(',') || constraint.StartsWith(">=") || constraint.StartsWith(">") ||
            constraint.StartsWith("<=") || constraint.StartsWith("<") || constraint.StartsWith("="))
            return MatchesRange(constraint, version);

        return constraint == version;
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
            if (!EvaluateComparison(part.Trim(), version)) return false;
        }
        return true;
    }

    private static bool EvaluateComparison(string comparison, string version)
    {
        string op;
        string target;

        if (comparison.StartsWith(">="))
        {
            op = ">="; target = comparison[2..];
        }
        else if (comparison.StartsWith("<="))
        {
            op = "<="; target = comparison[2..];
        }
        else if (comparison.StartsWith('>'))
        {
            op = ">"; target = comparison[1..];
        }
        else if (comparison.StartsWith('<'))
        {
            op = "<"; target = comparison[1..];
        }
        else if (comparison.StartsWith('='))
        {
            op = "="; target = comparison[1..];
        }
        else
        {
            return comparison == version;
        }

        var cmp = CompareVersions(version, target);
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

    private static int CompareVersions(string a, string b)
    {
        var aParts = a.Split('.').Select(p => int.TryParse(p, out var v) ? v : 0).ToArray();
        var bParts = b.Split('.').Select(p => int.TryParse(p, out var v) ? v : 0).ToArray();
        var len = Math.Max(aParts.Length, bParts.Length);
        for (int i = 0; i < len; i++)
        {
            var av = i < aParts.Length ? aParts[i] : 0;
            var bv = i < bParts.Length ? bParts[i] : 0;
            if (av != bv) return av.CompareTo(bv);
        }
        return 0;
    }

    private static bool IsValidRange(string range)
    {
        var parts = range.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        foreach (var part in parts)
        {
            if (!ComparisonRegex().IsMatch(part.Trim())) return false;
        }
        return true;
    }

    [GeneratedRegex(@"^\d+(\.\d+)*(-[a-zA-Z0-9.]+)?$")]
    private static partial Regex ExactVersionRegex();

    [GeneratedRegex(@"^\d+(\.\d+)*(\.\*|\.x)$")]
    private static partial Regex WildcardVersionRegex();

    [GeneratedRegex(@"^(>=|<=|>|<|=)?\d+(\.\d+)*$")]
    private static partial Regex ComparisonRegex();
}
