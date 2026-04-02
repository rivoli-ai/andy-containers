using System.Text.RegularExpressions;

namespace Andy.Containers.Api.Services;

public static partial class UserNameDerivation
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "root", "daemon", "bin", "sys", "nobody", "www-data", "mail", "man",
        "sshd", "postgres", "mysql", "redis", "node", "git"
    };

    /// <summary>
    /// Derives a Linux-compatible username from JWT claims.
    /// Priority: preferred_username > email (local part) > sub claim hash.
    /// </summary>
    public static string DeriveUsername(string? preferredUsername, string? email, string sub)
    {
        // Try preferred_username first
        if (!string.IsNullOrWhiteSpace(preferredUsername))
        {
            var sanitized = Sanitize(preferredUsername);
            if (!string.IsNullOrEmpty(sanitized) && !ReservedNames.Contains(sanitized))
                return sanitized;
        }

        // Try email local part
        if (!string.IsNullOrWhiteSpace(email))
        {
            var atIndex = email.IndexOf('@');
            var localPart = atIndex > 0 ? email[..atIndex] : email;
            var sanitized = Sanitize(localPart);
            if (!string.IsNullOrEmpty(sanitized) && !ReservedNames.Contains(sanitized))
                return sanitized;
        }

        // Fallback: hash the sub claim
        var hash = sub.GetHashCode() & 0x7FFFFFFF;
        return $"user-{hash:x8}"[..Math.Min(15, $"user-{hash:x8}".Length)];
    }

    private static string Sanitize(string input)
    {
        // Lowercase
        var result = input.ToLowerInvariant();
        // Replace dots, underscores, spaces with hyphens
        result = result.Replace('.', '-').Replace('_', '-').Replace(' ', '-');
        // Remove anything that's not alphanumeric or hyphen
        result = InvalidCharsRegex().Replace(result, "");
        // Remove leading hyphens/digits (Linux usernames must start with letter)
        result = LeadingInvalidRegex().Replace(result, "");
        // Collapse multiple hyphens
        result = MultiHyphenRegex().Replace(result, "-");
        // Remove trailing hyphens
        result = result.TrimEnd('-');
        // Truncate to 32 chars
        if (result.Length > 32)
            result = result[..32].TrimEnd('-');
        return result;
    }

    [GeneratedRegex("[^a-z0-9-]")]
    private static partial Regex InvalidCharsRegex();

    [GeneratedRegex("^[^a-z]+")]
    private static partial Regex LeadingInvalidRegex();

    [GeneratedRegex("-{2,}")]
    private static partial Regex MultiHyphenRegex();
}
