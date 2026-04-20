namespace Andy.Containers.Models;

/// <summary>
/// An agent run — the execution of an Agent (from andy-agents) against a delegation
/// contract inside a provisioned container. Emits events on
/// <c>andy.containers.events.run.{id}.*</c>.
/// </summary>
/// <remarks>
/// Story AP1 (rivoli-ai/andy-containers#103). See Epic AP (#101) for the broader
/// agent-run-execution design.
/// </remarks>
public class Run
{
    public Guid Id { get; set; }

    /// <summary>Agent slug from andy-agents (e.g. "triage-agent").</summary>
    public required string AgentId { get; set; }

    /// <summary>Optional pin to a specific agent revision; null = head.</summary>
    public int? AgentRevision { get; set; }

    public RunMode Mode { get; set; }

    /// <summary>
    /// FK to andy-containers Epic X <c>EnvironmentProfile</c>. Stored without FK
    /// constraint until X lands.
    /// </summary>
    public Guid EnvironmentProfileId { get; set; }

    /// <summary>Owned value object pinning workspace + branch.</summary>
    public WorkspaceRef WorkspaceRef { get; set; } = new();

    /// <summary>Policy from andy-rbac Epic V. Nullable until V lands platform-wide.</summary>
    public Guid? PolicyId { get; set; }

    /// <summary>Set once AP5 mode dispatcher provisions or selects a container.</summary>
    public Guid? ContainerId { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Pending;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public int? ExitCode { get; set; }

    public string? Error { get; set; }

    /// <summary>Root causation id per ADR-0001 header semantics.</summary>
    public Guid CorrelationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Transition the run to <paramref name="next"/> according to AP1's state-machine
    /// invariants. Throws <see cref="InvalidOperationException"/> for illegal
    /// transitions so callers cannot silently corrupt history.
    /// </summary>
    public void TransitionTo(RunStatus next, DateTimeOffset? at = null)
    {
        if (!RunStatusTransitions.CanTransition(Status, next))
        {
            throw new InvalidOperationException(
                $"Illegal run status transition: {Status} -> {next}.");
        }

        var now = at ?? DateTimeOffset.UtcNow;

        // Side-effects tied to specific transitions.
        if (Status == RunStatus.Pending && next == RunStatus.Provisioning)
        {
            // no-op; enter provisioning
        }
        else if (Status == RunStatus.Provisioning && next == RunStatus.Running)
        {
            StartedAt ??= now;
        }
        else if (RunStatusTransitions.IsTerminal(next))
        {
            EndedAt ??= now;
        }

        Status = next;
        UpdatedAt = now;
    }
}

public enum RunMode
{
    Headless,
    Terminal,
    Desktop
}

public enum RunStatus
{
    Pending,
    Provisioning,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Timeout
}

/// <summary>
/// Owned value object on <see cref="Run"/>: pins workspace id + branch.
/// </summary>
public class WorkspaceRef
{
    public Guid WorkspaceId { get; set; }
    public string? Branch { get; set; }
}

/// <summary>
/// Centralised transition rules for <see cref="RunStatus"/>. Kept separate from the
/// enum so callers can query allowed edges without a full entity instance.
/// </summary>
public static class RunStatusTransitions
{
    private static readonly Dictionary<RunStatus, HashSet<RunStatus>> Allowed = new()
    {
        [RunStatus.Pending] = new() { RunStatus.Provisioning, RunStatus.Cancelled, RunStatus.Failed },
        [RunStatus.Provisioning] = new() { RunStatus.Running, RunStatus.Cancelled, RunStatus.Failed, RunStatus.Timeout },
        [RunStatus.Running] = new() { RunStatus.Succeeded, RunStatus.Failed, RunStatus.Cancelled, RunStatus.Timeout },
        // Terminal states cannot transition further.
        [RunStatus.Succeeded] = new(),
        [RunStatus.Failed] = new(),
        [RunStatus.Cancelled] = new(),
        [RunStatus.Timeout] = new(),
    };

    public static bool CanTransition(RunStatus from, RunStatus to)
        => Allowed.TryGetValue(from, out var next) && next.Contains(to);

    public static bool IsTerminal(RunStatus status)
        => status is RunStatus.Succeeded or RunStatus.Failed
            or RunStatus.Cancelled or RunStatus.Timeout;
}
