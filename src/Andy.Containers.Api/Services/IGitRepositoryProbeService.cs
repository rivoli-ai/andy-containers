using Andy.Containers.Abstractions;

namespace Andy.Containers.Api.Services;

/// <summary>
/// Validates that git repository URLs are reachable before provisioning.
/// Uses git ls-remote with a short timeout for lightweight accessibility checks.
/// </summary>
public interface IGitRepositoryProbeService
{
    /// <summary>
    /// Probes a list of git repository URLs to check if they are reachable.
    /// Returns a list of error messages for unreachable repos. Empty list means all OK.
    /// When requireCredentials is true, repos that need auth but have no credentials
    /// are reported as errors instead of being silently skipped.
    /// </summary>
    Task<List<string>> ProbeRepositoriesAsync(
        IReadOnlyList<GitRepositoryConfig> repos,
        string ownerId,
        bool requireCredentials = false,
        CancellationToken ct = default);
}

/// <summary>Result of probing a single repository URL.</summary>
public enum ProbeResult
{
    /// <summary>Repository is accessible.</summary>
    Accessible,
    /// <summary>Repository requires authentication.</summary>
    AuthRequired,
    /// <summary>Repository not found or other error.</summary>
    NotFound,
    /// <summary>Probe was skipped (SSH URL, git unavailable, etc.).</summary>
    Skipped,
    /// <summary>Probe timed out.</summary>
    TimedOut,
}
