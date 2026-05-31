using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

/// <inheritdoc cref="IRunModeDispatcher"/>
public sealed class RunModeDispatcher : IRunModeDispatcher
{
    private readonly ContainersDbContext _db;
    private readonly IHeadlessRunner _runner;
    private readonly IRunBranchService _runBranch;
    private readonly ILogger<RunModeDispatcher> _logger;

    public RunModeDispatcher(
        ContainersDbContext db,
        IHeadlessRunner runner,
        IRunBranchService runBranch,
        ILogger<RunModeDispatcher> logger)
    {
        _db = db;
        _runner = runner;
        _runBranch = runBranch;
        _logger = logger;
    }

    public async Task<RunDispatchOutcome> DispatchAsync(Run run, string configPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        // Desktop has no GUI provider wired yet. Bail before touching the
        // workspace so the run stays cleanly Pending — picking a container
        // we'd never use would just confuse later operators / dashboards.
        if (run.Mode == RunMode.Desktop)
        {
            const string reason = "Desktop mode dispatch is not implemented; no GUI provider is wired in andy-containers yet.";
            _logger.LogWarning("Run {RunId} mode=Desktop: {Reason}", run.Id, reason);
            return RunDispatchOutcome.NotImplemented(reason);
        }

        var workspaceId = run.WorkspaceRef?.WorkspaceId ?? Guid.Empty;
        if (workspaceId == Guid.Empty)
        {
            return Fail(run, "Run has no workspace reference; cannot select a container.");
        }

        var workspace = await _db.Workspaces.FirstOrDefaultAsync(w => w.Id == workspaceId, ct);
        if (workspace is null)
        {
            return Fail(run, $"Workspace {workspaceId} not found.");
        }

        if (workspace.DefaultContainerId is not { } containerId)
        {
            return Fail(run, $"Workspace {workspaceId} has no default container; provision one before dispatching the run.");
        }

        run.ContainerId = containerId;

        try
        {
            run.TransitionTo(RunStatus.Provisioning);
        }
        catch (InvalidOperationException ex)
        {
            // Pending → Provisioning is the only legal edge here; if we land
            // on this branch the run was already moved by a parallel actor
            // (cancel, prior dispatch). Treat as a no-op and keep going so
            // an in-flight run isn't double-failed.
            _logger.LogInformation(ex,
                "Run {RunId} could not transition Pending→Provisioning (status={Status}); proceeding without state change.",
                run.Id, run.Status);
        }

        await _db.SaveChangesAsync(ct);

        // F6.1 (rivoli-ai/conductor#1940): give the run an isolated git branch
        // `andy/run/{runId}` in every cloned repo of the selected container,
        // off the workspace base, and persist it into Run.WorkspaceRef.Branch.
        // Best-effort — a branch failure never aborts the dispatch (mirrors
        // GitCloneService's "a failed repo doesn't fail the container").
        try
        {
            await _runBranch.EnsureRunBranchAsync(run, containerId, ct);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Run {RunId}: per-run branch preparation failed for container {ContainerId}; continuing dispatch.",
                run.Id, containerId);
        }

        return run.Mode switch
        {
            RunMode.Headless => await StartHeadlessAsync(run, configPath, ct),
            RunMode.Terminal => RunDispatchOutcome.Attachable(),
            _ => Fail(run, $"Unknown run mode: {run.Mode}."),
        };
    }

    private async Task<RunDispatchOutcome> StartHeadlessAsync(Run run, string configPath, CancellationToken ct)
    {
        try
        {
            var outcome = await _runner.StartAsync(run, configPath, ct);
            return RunDispatchOutcome.Started(outcome);
        }
        catch (Exception ex)
        {
            // The runner owns its own terminal-event writes — if it threw
            // before getting there the row is stuck mid-flight. Surface the
            // failure to the caller; transitioning the run row to Failed is
            // intentionally not done here because the runner's TerminateAsync
            // path is the canonical place for that and reaching in around it
            // would race with a slow-but-recovering runner.
            _logger.LogError(ex,
                "Run {RunId} headless dispatch threw: {Message}", run.Id, ex.Message);
            return RunDispatchOutcome.Failed(ex.Message);
        }
    }

    private RunDispatchOutcome Fail(Run run, string error)
    {
        _logger.LogWarning("Run {RunId} dispatch failed: {Error}", run.Id, error);
        return RunDispatchOutcome.Failed(error);
    }
}
