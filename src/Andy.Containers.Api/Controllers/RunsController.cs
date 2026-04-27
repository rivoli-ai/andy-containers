using System.Text.Json;
using Andy.Containers.Api.Services;
using Andy.Containers.Configurator;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Messaging;
using Andy.Containers.Messaging;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;
using Andy.Rbac.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Controllers;

/// <summary>
/// AP2 (rivoli-ai/andy-containers#104). Entry point for agent runs:
/// submit a run spec, observe its state, request cancellation. Container
/// selection and mode-routing are owned by <see cref="IRunModeDispatcher"/>
/// (AP5); the controller persists the run as <see cref="RunStatus.Pending"/>,
/// runs the configurator, then hands off.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class RunsController : ControllerBase
{
    // AP7 (rivoli-ai/andy-containers#109). Grace window for awaiting the
    // runner's terminal write after we signal cancellation. The headless
    // runner's catch-OperationCanceledException path calls TerminateAsync
    // immediately, so most cancels resolve in <100ms — the 30s ceiling is
    // for hung Docker exec streams that don't honour the linked CTS.
    internal static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(30);

    private readonly ContainersDbContext _db;
    private readonly IRunConfigurator _configurator;
    private readonly IRunModeDispatcher _dispatcher;
    private readonly IRunCancellationRegistry _cancellation;
    private readonly ILogger<RunsController> _logger;

    public RunsController(
        ContainersDbContext db,
        IRunConfigurator configurator,
        IRunModeDispatcher dispatcher,
        IRunCancellationRegistry cancellation,
        ILogger<RunsController> logger)
    {
        _db = db;
        _configurator = configurator;
        _dispatcher = dispatcher;
        _cancellation = cancellation;
        _logger = logger;
    }

    [HttpPost]
    [RequirePermission("run:write")]
    public async Task<IActionResult> Create(
        [FromBody] CreateRunRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        if (request.EnvironmentProfileId == Guid.Empty)
        {
            return BadRequest(new { error = "environmentProfileId must be a non-empty Guid." });
        }

        if (request.WorkspaceRef is { WorkspaceId: var wid } && wid == Guid.Empty)
        {
            return BadRequest(new { error = "workspaceRef.workspaceId must be a non-empty Guid when workspaceRef is provided." });
        }

        var now = DateTimeOffset.UtcNow;
        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = request.AgentId,
            AgentRevision = request.AgentRevision,
            Mode = request.Mode,
            EnvironmentProfileId = request.EnvironmentProfileId,
            WorkspaceRef = request.WorkspaceRef is null
                ? new WorkspaceRef()
                : new WorkspaceRef
                {
                    WorkspaceId = request.WorkspaceRef.WorkspaceId,
                    Branch = request.WorkspaceRef.Branch,
                },
            PolicyId = request.PolicyId,
            CorrelationId = request.CorrelationId ?? Guid.NewGuid(),
            Status = RunStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Runs.Add(run);
        await _db.SaveChangesAsync(ct);

        // AP3 (rivoli-ai/andy-containers#105). Build + write the andy-cli
        // headless config now so AP6's runner has a file path the moment it
        // wakes up. Failures here do NOT roll back the Run — the row stays
        // Pending and AP5/AP6 can retry the configurator on the next pass
        // (or surface a transition to Failed once that policy is decided).
        var configResult = await _configurator.ConfigureAsync(run, ct);
        if (!configResult.IsSuccess)
        {
            _logger.LogWarning(
                "Run {RunId} persisted as Pending but configurator failed: {Error}. Skipping dispatch; row stays Pending.",
                run.Id, configResult.Error);
        }
        else
        {
            // AP5 (rivoli-ai/andy-containers#107). Hand off to the mode
            // dispatcher, which selects the container, transitions to
            // Provisioning, and (for headless runs) drives the run to a
            // terminal event. Failures are logged and the row stays as the
            // dispatcher left it; nothing is rolled back.
            var dispatch = await _dispatcher.DispatchAsync(run, configResult.Path!, ct);
            if (dispatch.Kind is RunDispatchKind.Failed or RunDispatchKind.NotImplemented)
            {
                _logger.LogWarning(
                    "Run {RunId} dispatch returned {Kind}: {Error}",
                    run.Id, dispatch.Kind, dispatch.Error);
            }
        }

        var dto = RunDto.FromEntity(run);
        return CreatedAtAction(nameof(Get), new { id = run.Id }, dto);
    }

    [HttpGet("{id:guid}")]
    [RequirePermission("run:read")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var run = await _db.Runs.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run is null)
        {
            return NotFound();
        }

        return Ok(RunDto.FromEntity(run));
    }

    [HttpPost("{id:guid}/cancel")]
    [RequirePermission("run:execute")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var run = await _db.Runs.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run is null)
        {
            return NotFound();
        }

        // AP1 state-machine guard. Cancelled is reachable from Pending,
        // Provisioning, and Running; terminal states map to 409 so callers
        // can distinguish "too late" from "no such run".
        if (!RunStatusTransitions.CanTransition(run.Status, RunStatus.Cancelled))
        {
            return Conflict(new
            {
                error = $"Run {run.Id} is in terminal status {run.Status}; cannot cancel.",
            });
        }

        // AP7 (rivoli-ai/andy-containers#109). Two paths:
        //
        // (a) An AP6 runner is active for this Run. We signal its linked
        //     CTS via the registry; the runner's catch-OCE path calls
        //     TerminateAsync, which transitions to Cancelled and appends
        //     the cancelled outbox event. We await that terminal write
        //     up to CancelGrace so the response reflects committed state.
        //
        // (b) No runner is registered (Pending row not yet picked up by
        //     AP5, or runner already exited). We flip the row ourselves
        //     and emit the cancelled outbox event so subscribers see a
        //     terminal subject either way.
        if (_cancellation.TryCancel(run.Id))
        {
            bool terminal;
            try
            {
                terminal = await _cancellation.WaitForTerminalAsync(run.Id, CancelGrace, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller hung up. Don't escalate — the runner will still
                // observe its own cancellation and write terminal state.
                throw;
            }

            await _db.Entry(run).ReloadAsync(ct);

            if (!terminal)
            {
                // Runner ignored its CTS for 30s. Force the transition
                // and emit the event ourselves so the row doesn't stay
                // Running forever; AP9 metrics can flag this on the
                // outbox side via a duplicate subject if the runner
                // eventually wakes up.
                _logger.LogWarning(
                    "Run {RunId} cancel grace ({GraceSeconds}s) expired before runner terminal write; forcing Cancelled.",
                    run.Id, (int)CancelGrace.TotalSeconds);
                ForceCancel(run);
                await _db.SaveChangesAsync(ct);
            }

            return Ok(RunDto.FromEntity(run));
        }

        // Pending-row path: no active runner to signal, flip directly.
        ForceCancel(run);
        await _db.SaveChangesAsync(ct);

        return Ok(RunDto.FromEntity(run));
    }

    // Best-effort transition + outbox emit. Used for the no-runner Pending
    // path and the grace-expired escalation. CanTransition guards against
    // the rare race where the runner's TerminateAsync committed between
    // our reload and our save — in that case the row is already Cancelled
    // and we leave it alone, but we still emit the outbox event so the
    // caller's API contract ("cancel emits run.cancelled") holds.
    private void ForceCancel(Run run)
    {
        if (RunStatusTransitions.CanTransition(run.Status, RunStatus.Cancelled))
        {
            run.TransitionTo(RunStatus.Cancelled);
        }

        _db.AppendAgentRunEvent(run, RunEventKind.Cancelled);
    }

    /// <summary>
    /// AP9 (rivoli-ai/andy-containers#111). Stream lifecycle events for a
    /// run as newline-delimited JSON. Each line is a serialised
    /// <see cref="RunEventDto"/>; the response closes when the run reaches
    /// a terminal status (after a final drain pass) or the caller
    /// disconnects. Used by <c>andy-containers-cli runs events</c>.
    /// </summary>
    /// <remarks>
    /// We write the response body directly rather than returning
    /// <c>IAsyncEnumerable&lt;RunEventDto&gt;</c> so each event is flushed
    /// to the wire as it lands — the default JSON streaming serializer
    /// buffers and would only flush at chunk boundaries, which defeats
    /// the live-stream UX. Returns 404 if the run is unknown so callers
    /// don't sit on an empty stream forever.
    /// </remarks>
    [HttpGet("{id:guid}/events")]
    [RequirePermission("run:read")]
    public async Task Events(Guid id, CancellationToken ct)
    {
        var exists = await _db.Runs.AsNoTracking().AnyAsync(r => r.Id == id, ct);
        if (!exists)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/x-ndjson";
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("X-Accel-Buffering", "no");

        await foreach (var evt in RunEventStream.AsyncEnumerate(_db, id, ct: ct))
        {
            var json = JsonSerializer.Serialize(evt, EventJson.Options);
            await Response.WriteAsync(json, ct);
            await Response.WriteAsync("\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }
}
