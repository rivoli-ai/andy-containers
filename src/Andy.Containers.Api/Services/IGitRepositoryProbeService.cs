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
    /// Repos requiring credentials that are not available are skipped (not an error).
    /// </summary>
    Task<List<string>> ProbeRepositoriesAsync(
        IReadOnlyList<GitRepositoryConfig> repos,
        string ownerId,
        CancellationToken ct = default);
}
