using System.Text.RegularExpressions;

namespace Andy.Containers.Api.Services;

public static partial class GitUrlValidator
{
    public static bool IsValidGitUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return HttpsGitUrlRegex().IsMatch(url) || SshGitUrlRegex().IsMatch(url);
    }

    public static bool HasEmbeddedCredentials(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return EmbeddedCredentialsRegex().IsMatch(url);
    }

    public static bool IsValidBranchName(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) return false;
        // Full 40-char hex SHA
        if (FullShaRegex().IsMatch(branch)) return true;
        // Branch/tag name
        return BranchNameRegex().IsMatch(branch);
    }

    public static bool IsValidTargetPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Length > 255) return false;
        if (path.StartsWith('/')) return false;
        if (path.Contains("..")) return false;
        return TargetPathRegex().IsMatch(path);
    }

    public static string? ExtractHost(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        // HTTPS: https://github.com/org/repo.git
        var httpsMatch = HttpsHostRegex().Match(url);
        if (httpsMatch.Success) return httpsMatch.Groups[1].Value;

        // SSH: git@github.com:org/repo.git
        var sshMatch = SshHostRegex().Match(url);
        if (sshMatch.Success) return sshMatch.Groups[1].Value;

        return null;
    }

    public static string DeriveTargetPath(string url)
    {
        var lastSegment = url.Split('/').LastOrDefault() ?? url.Split(':').LastOrDefault() ?? "repo";
        if (lastSegment.EndsWith(".git"))
            lastSegment = lastSegment[..^4];
        return lastSegment;
    }

    [GeneratedRegex(@"^https?://[^/]+/.+")]
    private static partial Regex HttpsGitUrlRegex();

    [GeneratedRegex(@"^git@[^:]+:.+")]
    private static partial Regex SshGitUrlRegex();

    [GeneratedRegex(@"://[^@/]*:[^@/]*@")]
    private static partial Regex EmbeddedCredentialsRegex();

    [GeneratedRegex(@"^[a-fA-F0-9]{40}$")]
    private static partial Regex FullShaRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9._/-]+$")]
    private static partial Regex BranchNameRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9._/-]+$")]
    private static partial Regex TargetPathRegex();

    [GeneratedRegex(@"^https?://([^/:]+)")]
    private static partial Regex HttpsHostRegex();

    [GeneratedRegex(@"^git@([^:]+):")]
    private static partial Regex SshHostRegex();
}
