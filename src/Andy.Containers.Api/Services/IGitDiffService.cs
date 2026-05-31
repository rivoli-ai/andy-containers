using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

/// <summary>
/// Computes the unified git diff of a container's run branch versus its base,
/// by running <c>git diff</c> / <c>git status</c> through the infrastructure
/// provider's exec surface (ARCHITECTURE §16.3). Read-only; never mutates the
/// working tree. Not a Docker-Engine verb (decision #17).
///
/// Story F6.1 (rivoli-ai/conductor#1940).
/// </summary>
public interface IGitDiffService
{
    /// <summary>
    /// Per-file patch truncation cap (64 KiB), mirroring the build-log cap.
    /// </summary>
    const int MaxPatchBytesPerFile = 64 * 1024;

    /// <summary>
    /// Compute the diff for the run hosted by <paramref name="containerId"/>.
    /// When <paramref name="repoId"/> is supplied the diff is scoped to that
    /// one repo; otherwise every cloned repo is aggregated with per-repo path
    /// prefixes. A clean tree / no-git-repo / detached HEAD yields a 200-OK
    /// empty result (no <see cref="GitDiffFile"/>s), never an error.
    /// </summary>
    Task<GitDiffResult> GetDiffAsync(Guid containerId, Guid? repoId, CancellationToken ct = default);
}
