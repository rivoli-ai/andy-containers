using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Andy.Containers.Api.Services;
using Andy.Containers.Configurator;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Messaging;
using Andy.Containers.Messaging;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Andy.Containers.Api.Mcp;

/// <summary>
/// AP8 (rivoli-ai/andy-containers#110). MCP surface for Epic AP's agent
/// runs: <c>run.create</c>, <c>run.get</c>, <c>run.cancel</c>,
/// <c>run.events</c>. Mirrors the <c>/api/runs</c> HTTP endpoints in shape
/// and side-effects so MCP clients see the same lifecycle the REST clients
/// see — same RunDto, same outbox subjects, same state-machine guarantees.
/// </summary>
/// <remarks>
/// Permissions are enforced inline against the user's primary org via
/// <see cref="IOrganizationMembershipService"/> — the same convention
/// <see cref="ContainersMcpTools"/> uses. Admins bypass org checks.
/// Tools return <c>null</c> on permission denial / 404 / illegal state
/// transitions to keep the surface uniform; full error envelopes are
/// HTTP-only.
/// </remarks>
[McpServerToolType]
public class RunsMcpTools
{
    private readonly ContainersDbContext _db;
    private readonly IRunConfigurator _configurator;
    private readonly IRunModeDispatcher _dispatcher;
    private readonly IRunCancellationRegistry _cancellation;
    private readonly ICurrentUserService _currentUser;
    private readonly IOrganizationMembershipService _orgMembership;
    private readonly ILogger<RunsMcpTools> _logger;

    // Mirror the HTTP cancel-grace ceiling (RunsController.CancelGrace).
    // Most cancels resolve in <100ms; 30s is the hung-exec backstop.
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(30);

    // Outbox poll interval for run.events. Short enough that a fresh
    // event lands in the stream within a hundred ms or two; not so
    // short that we hammer the DB with empty selects on a quiet run.
    private static readonly TimeSpan EventsPollInterval = TimeSpan.FromMilliseconds(250);

    public RunsMcpTools(
        ContainersDbContext db,
        IRunConfigurator configurator,
        IRunModeDispatcher dispatcher,
        IRunCancellationRegistry cancellation,
        ICurrentUserService currentUser,
        IOrganizationMembershipService orgMembership,
        ILogger<RunsMcpTools> logger)
    {
        _db = db;
        _configurator = configurator;
        _dispatcher = dispatcher;
        _cancellation = cancellation;
        _currentUser = currentUser;
        _orgMembership = orgMembership;
        _logger = logger;
    }

    [McpServerTool(Name = "run.create"), Description(
        "Create an agent run. Persists the run as Pending, builds the headless config, and hands off to the mode dispatcher. " +
        "Returns the RunDto reflecting whatever state the run reached before this call returns. " +
        "Requires run:write.")]
    public async Task<RunDto?> Create(
        [Description("Agent slug from andy-agents (e.g. 'triage-agent').")] string agentId,
        [Description("Mode: 'Headless', 'Terminal', or 'Desktop'.")] string mode,
        [Description("EnvironmentProfile id (GUID).")] string environmentProfileId,
        [Description("Optional agent revision pin; null/0 = head.")] int? agentRevision = null,
        [Description("Optional workspace id (GUID).")] string? workspaceId = null,
        [Description("Optional branch name.")] string? branch = null,
        [Description("Optional policy id (GUID).")] string? policyId = null,
        [Description("Optional ADR-0001 root causation id (GUID); minted if omitted.")] string? correlationId = null,
        CancellationToken ct = default)
    {
        if (!await EnsurePermission(Permissions.RunWrite, ct)) return null;

        if (!Enum.TryParse<RunMode>(mode, ignoreCase: true, out var parsedMode))
        {
            _logger.LogWarning("run.create rejected: unknown mode '{Mode}'", mode);
            return null;
        }

        if (!Guid.TryParse(environmentProfileId, out var profileId) || profileId == Guid.Empty)
        {
            _logger.LogWarning("run.create rejected: environmentProfileId '{Id}' is not a non-empty GUID", environmentProfileId);
            return null;
        }

        WorkspaceRef workspaceRef = new();
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            if (!Guid.TryParse(workspaceId, out var wid) || wid == Guid.Empty)
            {
                _logger.LogWarning("run.create rejected: workspaceId '{Id}' is not a non-empty GUID", workspaceId);
                return null;
            }
            workspaceRef = new WorkspaceRef { WorkspaceId = wid, Branch = branch };
        }

        Guid? parsedPolicyId = null;
        if (!string.IsNullOrWhiteSpace(policyId))
        {
            if (!Guid.TryParse(policyId, out var pid)) return null;
            parsedPolicyId = pid;
        }

        Guid? parsedCorrelationId = null;
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            if (!Guid.TryParse(correlationId, out var cid)) return null;
            parsedCorrelationId = cid;
        }

        var now = DateTimeOffset.UtcNow;
        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            AgentRevision = agentRevision,
            Mode = parsedMode,
            EnvironmentProfileId = profileId,
            WorkspaceRef = workspaceRef,
            PolicyId = parsedPolicyId,
            CorrelationId = parsedCorrelationId ?? Guid.NewGuid(),
            Status = RunStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Runs.Add(run);
        await _db.SaveChangesAsync(ct);

        // Same handoff sequence as RunsController.Create: configurator
        // writes the andy-cli config, then the dispatcher takes over.
        // Both are best-effort — failures leave the row in a non-terminal
        // state for a later retry rather than rolling back submission.
        var configResult = await _configurator.ConfigureAsync(run, ct);
        if (!configResult.IsSuccess)
        {
            _logger.LogWarning(
                "run.create: Run {RunId} persisted as Pending but configurator failed: {Error}.",
                run.Id, configResult.Error);
        }
        else
        {
            var dispatch = await _dispatcher.DispatchAsync(run, configResult.Path!, ct);
            if (dispatch.Kind is RunDispatchKind.Failed or RunDispatchKind.NotImplemented)
            {
                _logger.LogWarning(
                    "run.create: Run {RunId} dispatch returned {Kind}: {Error}",
                    run.Id, dispatch.Kind, dispatch.Error);
            }
        }

        return RunDto.FromEntity(run);
    }

    [McpServerTool(Name = "run.get"), Description(
        "Fetch a run by id. Returns null when the run is unknown or the caller lacks run:read.")]
    public async Task<RunDto?> Get(
        [Description("Run id (GUID).")] string runId,
        CancellationToken ct = default)
    {
        if (!await EnsurePermission(Permissions.RunRead, ct)) return null;
        if (!Guid.TryParse(runId, out var id)) return null;

        var run = await _db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        return run is null ? null : RunDto.FromEntity(run);
    }

    [McpServerTool(Name = "run.cancel"), Description(
        "Cancel a non-terminal run. Signals the active runner via the cancellation registry and awaits its terminal write " +
        "up to a 30s grace; falls back to flipping the row directly when no runner is active. " +
        "Returns null when the run is unknown, already terminal, or the caller lacks run:execute.")]
    public async Task<RunDto?> Cancel(
        [Description("Run id (GUID).")] string runId,
        CancellationToken ct = default)
    {
        if (!await EnsurePermission(Permissions.RunExecute, ct)) return null;
        if (!Guid.TryParse(runId, out var id)) return null;

        var run = await _db.Runs.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run is null) return null;

        if (!RunStatusTransitions.CanTransition(run.Status, RunStatus.Cancelled))
        {
            // MCP contract surfaces 'already terminal' as null. Callers
            // get the same information by reading the run via run.get.
            return null;
        }

        if (_cancellation.TryCancel(run.Id))
        {
            var terminal = await _cancellation.WaitForTerminalAsync(run.Id, CancelGrace, ct);
            await _db.Entry(run).ReloadAsync(ct);

            if (!terminal)
            {
                _logger.LogWarning(
                    "run.cancel: Run {RunId} cancel grace ({GraceSeconds}s) expired before runner terminal write; forcing Cancelled.",
                    run.Id, (int)CancelGrace.TotalSeconds);
                ForceCancel(run);
                await _db.SaveChangesAsync(ct);
            }

            return RunDto.FromEntity(run);
        }

        ForceCancel(run);
        await _db.SaveChangesAsync(ct);
        return RunDto.FromEntity(run);
    }

    [McpServerTool(Name = "run.events"), Description(
        "Stream lifecycle events for a run on andy.containers.events.run.{id}.* — finished, failed, cancelled, timeout. " +
        "Yields any backfill (events that already landed in the outbox) then live events as they commit. " +
        "Stops when the run reaches a terminal state or the caller cancels. Requires run:read.")]
    public async IAsyncEnumerable<RunEventDto> Events(
        [Description("Run id (GUID).")] string runId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!await EnsurePermission(Permissions.RunRead, ct)) yield break;
        if (!Guid.TryParse(runId, out var id)) yield break;

        // AP9 (rivoli-ai/andy-containers#111). Shared outbox-poll loop —
        // identical semantics to the HTTP NDJSON endpoint and the CLI
        // events command.
        await foreach (var evt in RunEventStream.AsyncEnumerate(_db, id, EventsPollInterval, ct))
        {
            yield return evt;
        }
    }

    private async Task<bool> EnsurePermission(string permission, CancellationToken ct)
    {
        if (_currentUser.IsAdmin()) return true;

        var userId = _currentUser.GetUserId();
        if (string.IsNullOrEmpty(userId)) return false;

        var orgId = _currentUser.GetOrganizationId();
        if (orgId is null)
        {
            // No primary org → can't evaluate org-scoped permission.
            // Match the existing MCP convention (deny on missing org).
            return false;
        }

        return await _orgMembership.HasPermissionAsync(userId, orgId.Value, permission, ct);
    }

    private void ForceCancel(Run run)
    {
        if (RunStatusTransitions.CanTransition(run.Status, RunStatus.Cancelled))
        {
            run.TransitionTo(RunStatus.Cancelled);
        }

        _db.AppendAgentRunEvent(run, RunEventKind.Cancelled);
    }
}

