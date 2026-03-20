using System.Text.RegularExpressions;
using Andy.Containers.Abstractions;

namespace Andy.Containers.Api.Services;

public static partial class GitRepositoryValidator
{
    private static readonly Regex InvalidBranchChars = InvalidBranchCharsRegex();

    public static List<string> Validate(GitRepositoryConfig config)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.Url))
        {
            errors.Add("Repository URL is required");
            return errors;
        }

        // Check URL scheme
        if (!config.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !config.Url.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Only https:// and git@ URL schemes are allowed");
        }

        // Reject embedded credentials in URL
        if (config.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(config.Url);
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                errors.Add("Embedded credentials in URLs are not allowed. Use a credential reference instead");
            }
        }

        // Validate target path (no path traversal)
        if (!string.IsNullOrEmpty(config.TargetPath))
        {
            var normalized = config.TargetPath.Replace('\\', '/');
            if (normalized.Contains("..") ||
                normalized.Contains("~") ||
                (!normalized.StartsWith('/') && !normalized.StartsWith("./")))
            {
                errors.Add("Target path must be absolute and cannot contain path traversal sequences");
            }
        }

        // Validate branch name
        if (!string.IsNullOrEmpty(config.Branch))
        {
            if (config.Branch.Contains("..") ||
                config.Branch.Contains(" ") ||
                config.Branch.EndsWith('.') ||
                config.Branch.EndsWith('/') ||
                config.Branch.Contains("\\") ||
                InvalidBranchChars.IsMatch(config.Branch))
            {
                errors.Add("Invalid branch name");
            }
        }

        return errors;
    }

    public static List<string> ValidateAll(IEnumerable<GitRepositoryConfig> configs)
    {
        var errors = new List<string>();
        var index = 0;
        foreach (var config in configs)
        {
            var repoErrors = Validate(config);
            foreach (var error in repoErrors)
            {
                errors.Add($"Repository [{index}]: {error}");
            }
            index++;
        }
        return errors;
    }

    [GeneratedRegex(@"[\x00-\x1f\x7f~^:?*\[\\]")]
    private static partial Regex InvalidBranchCharsRegex();
}
