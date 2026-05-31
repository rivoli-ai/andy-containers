using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

/// <summary>
/// Checks out an isolated per-run git branch (<c>andy/run/{runId}</c>) in
/// every cloned repository of the run's container, off the workspace's base
/// branch, and persists the chosen branch name into
/// <see cref="Run.WorkspaceRef"/>.<see cref="WorkspaceRef.Branch"/>.
///
/// Story F6.1 (rivoli-ai/conductor#1940). Runs at the dispatcher's
/// <c>Run.ContainerId</c> assignment point (post-clone). Branch creation
/// goes through <c>IContainerService.ExecAsync</c> — the same exec surface
/// <c>GitCloneService</c> uses (ARCHITECTURE §16.3) — not a Docker-Engine
/// verb (decision #17).
/// </summary>
public interface IRunBranchService
{
    /// <summary>
    /// Derive the deterministic per-run branch name <c>andy/run/{runId}</c>.
    /// Pure; safe to call without any container.
    /// </summary>
    static string BranchNameFor(Guid runId) => $"andy/run/{runId}";

    /// <summary>
    /// Check out <c>andy/run/{runId}</c> in each cloned repo of
    /// <paramref name="containerId"/> and write the branch name into
    /// <paramref name="run"/>.WorkspaceRef.Branch. Best-effort per repo:
    /// a repo that isn't a git checkout (or where checkout fails) is logged
    /// and skipped — it never fails the dispatch. Does NOT call SaveChanges
    /// on the run (the caller owns that), but does persist per-repo events.
    /// </summary>
    Task EnsureRunBranchAsync(Run run, Guid containerId, CancellationToken ct = default);
}
