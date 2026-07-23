using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Messaging;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

/// <inheritdoc cref="IRunModeDispatcher"/>
public sealed class RunModeDispatcher : IRunModeDispatcher
{
    private readonly ContainersDbContext _db;
    private readonly IHeadlessRunLauncher _launcher;
    private readonly IRunBranchService _runBranch;
    private readonly ILogger<RunModeDispatcher> _logger;

    public RunModeDispatcher(
        ContainersDbContext db,
        IHeadlessRunLauncher launcher,
        IRunBranchService runBranch,
        ILogger<RunModeDispatcher> logger)
    {
        _db = db;
        _launcher = launcher;
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

        // The "workspace" indirection is gone: andy-tasks passes a CONTAINER
        // id directly in WorkspaceRef.WorkspaceId (the provider returns a
        // container id labelled as WorkspaceId). Resolve it straight against
        // the Containers table — no Workspaces lookup, no DefaultContainerId
        // hop. (The field rename WorkspaceRef.WorkspaceId → ContainerId is a
        // separate cleanup; only the RESOLUTION changes here.)
        var containerId = run.WorkspaceRef?.WorkspaceId ?? Guid.Empty;
        if (containerId == Guid.Empty)
        {
            return await FailAsync(run, "[TX-DISPATCH-NO-CONTAINER] Run has no container reference; cannot select a container.", ct);
        }

        var container = await _db.Containers.FirstOrDefaultAsync(c => c.Id == containerId, ct);
        if (container is null)
        {
            return await FailAsync(run, $"[TX-DISPATCH-CONTAINER-NOT-FOUND] Container {containerId} not found.", ct);
        }

        // A run execs into a live container; a Stopped/Failed/Destroyed one
        // can't host it. Fail with an actionable reason rather than handing a
        // dead container to the launcher and surfacing an opaque exec error.
        if (container.Status is ContainerStatus.Stopped or ContainerStatus.Failed
            or ContainerStatus.Destroying or ContainerStatus.Destroyed)
        {
            return await FailAsync(run, $"[TX-DISPATCH-CONTAINER-NOT-RUNNING] Container {containerId} is {container.Status}, cannot dispatch a run into it.", ct);
        }

        run.ContainerId = containerId;

        try
        {
            run.TransitionTo(RunStatus.Provisioning);
            _db.AppendAgentRunEvent(run, RunEventKind.Provisioning);
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

        // The container and run branch are now prepared. Ready is a distinct
        // observation from Running: consumers can show that dispatch finished
        // even while the detached launcher has not started the agent process.
        _db.AppendAgentRunEvent(run, RunEventKind.Ready);
        await _db.SaveChangesAsync(ct);

        return run.Mode switch
        {
            RunMode.Headless => StartHeadlessDetached(run, configPath),
            RunMode.Terminal => RunDispatchOutcome.Attachable(),
            _ => await FailAsync(run, $"Unknown run mode: {run.Mode}.", ct),
        };
    }

    // AX.16 (rivoli-ai/conductor#2104). The andy-cli exec can outlast any
    // sane HTTP timeout (a 480B coding model legitimately runs for many
    // minutes), so the dispatch hands off to the background launcher and
    // returns immediately. Terminal state reaches callers through the run
    // events the runner publishes over NATS + GET /api/runs/{id} polling —
    // the contract andy-tasks' ConductorExecutor already consumes.
    private RunDispatchOutcome StartHeadlessDetached(Run run, string configPath)
    {
        _ = _launcher.Launch(run.Id, configPath);
        _logger.LogInformation(
            "Run {RunId} headless execution detached to background; POST returns with status {Status}.",
            run.Id, run.Status);
        return RunDispatchOutcome.Detached();
    }

    // rivoli-ai/conductor#2122: a dispatch-level failure is TERMINAL for
    // the run, not a log line. Before this, Fail() only logged — the Run
    // row stayed Pending, no andy.containers.events.run.{id}.failed was
    // ever published (only provisioning/runner failures publish), and
    // andy-tasks' RunEventConsumer waited forever on an event that never
    // comes, leaving its AgentRun row Running with nothing behind it.
    // Transition the row to Failed, record the reason, and publish the
    // terminal event through the same outbox the runner uses so every
    // downstream consumer folds the truth.
    private async Task<RunDispatchOutcome> FailAsync(Run run, string error, CancellationToken ct)
    {
        _logger.LogWarning("Run {RunId} dispatch failed: {Error}", run.Id, error);

        run.Error = error;
        if (RunStatusTransitions.CanTransition(run.Status, RunStatus.Failed))
        {
            run.TransitionTo(RunStatus.Failed);
            _db.AppendAgentRunEvent(run, RunEventKind.Failed);
            await _db.SaveChangesAsync(ct);
        }
        else
        {
            _logger.LogWarning(
                "Run {RunId} dispatch failure could not transition {Status}→Failed; leaving status untouched.",
                run.Id, run.Status);
        }

        return RunDispatchOutcome.Failed(error);
    }
}
