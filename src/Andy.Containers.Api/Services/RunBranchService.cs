using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

/// <inheritdoc cref="IRunBranchService"/>
public sealed class RunBranchService : IRunBranchService
{
    private readonly ContainersDbContext _db;
    private readonly IContainerService _containerService;
    private readonly ILogger<RunBranchService> _logger;

    private static readonly TimeSpan CheckoutTimeout = TimeSpan.FromSeconds(30);

    public RunBranchService(
        ContainersDbContext db,
        IContainerService containerService,
        ILogger<RunBranchService> logger)
    {
        _db = db;
        _containerService = containerService;
        _logger = logger;
    }

    public async Task EnsureRunBranchAsync(Run run, Guid containerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        var branchName = IRunBranchService.BranchNameFor(run.Id);

        // Only branch repos that actually finished cloning — a Pending /
        // Failed clone has no working tree to check out into.
        var repos = await _db.ContainerGitRepositories
            .Where(r => r.ContainerId == containerId && r.CloneStatus == GitCloneStatus.Cloned)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);

        if (repos.Count == 0)
        {
            // No cloned repos (e.g. an empty container). Still record the
            // intended branch so late subscribers can resolve it; the diff
            // endpoint will surface "no git repo" as an empty-but-OK result.
            run.WorkspaceRef ??= new WorkspaceRef();
            run.WorkspaceRef.Branch = branchName;
            _logger.LogInformation(
                "Run {RunId}: no cloned repos in container {ContainerId}; recorded branch {Branch} without checkout.",
                run.Id, containerId, branchName);
            return;
        }

        var checkedOutAny = false;

        foreach (var repo in repos)
        {
            try
            {
                // Retry without moving an existing run branch. `checkout -B`
                // would reset its ref to the caller's current HEAD and can
                // discard checkpoint commits from an earlier attempt. A new
                // branch is created from the current (configured base) HEAD;
                // an existing branch is merely checked out, preserving its
                // accumulated ancestry. The later checkpoint command verifies
                // the exact attached branch again immediately before staging.
                var q = GitCloneService.ShellQuote(repo.TargetPath);
                var branch = GitCloneService.ShellQuote(branchName);
                var branchRef = GitCloneService.ShellQuote($"refs/heads/{branchName}");
                var command =
                    $"if git -C {q} show-ref --verify --quiet {branchRef}; then "
                    + $"git -C {q} checkout {branch}; else git -C {q} checkout -b {branch}; fi";

                var result = await _containerService.ExecAsync(containerId, command, CheckoutTimeout, ct);

                if (result.ExitCode != 0)
                {
                    _logger.LogWarning(
                        "Run {RunId}: checkout of {Branch} in repo {RepoPath} (container {ContainerId}) failed (exit {Exit}): {Err}",
                        run.Id, branchName, repo.TargetPath, containerId, result.ExitCode, result.StdErr);
                    continue;
                }

                checkedOutAny = true;

                _db.Events.Add(new ContainerEvent
                {
                    ContainerId = containerId,
                    EventType = ContainerEventType.RunBranchCheckedOut,
                    SubjectId = run.Id.ToString(),
                    Details = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        runId = run.Id,
                        repoId = repo.Id,
                        targetPath = repo.TargetPath,
                        baseBranch = repo.Branch,
                        runBranch = branchName
                    })
                });
            }
            catch (Exception ex)
            {
                // A failure to branch one repo must not abort the dispatch —
                // mirrors GitCloneService's "failed clones don't fail the
                // container" stance. Log and move on.
                _logger.LogWarning(ex,
                    "Run {RunId}: error checking out {Branch} in repo {RepoPath} (container {ContainerId}); skipping.",
                    run.Id, branchName, repo.TargetPath, containerId);
            }
        }

        // Persist the branch name regardless of partial failures: the
        // diff endpoint resolves the run branch from here.
        run.WorkspaceRef ??= new WorkspaceRef();
        run.WorkspaceRef.Branch = branchName;

        if (checkedOutAny)
        {
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "Run {RunId}: per-run branch {Branch} prepared in {Count} repo(s) of container {ContainerId}.",
            run.Id, branchName, repos.Count, containerId);
    }
}
