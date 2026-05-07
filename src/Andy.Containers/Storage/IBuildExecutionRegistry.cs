namespace Andy.Containers.Storage;

/// <summary>
/// In-memory registry of active build executions, scoped to the API
/// host. The async executor registers a build at queue time, updates
/// its state as it transitions through queued → running → terminal,
/// and the SSE / status-snapshot endpoints read from it.
/// </summary>
/// <remarks>
/// IM9 (rivoli-ai/andy-containers#263). Not durable — a host restart
/// loses any in-flight build state, and the SSE for those builds
/// will close cleanly. The persisted <see cref="BuildArtifactEntity"/>
/// row is the durable record of any successful build; the registry
/// only holds enough state for live SSE / status polling while a
/// build is in flight.
/// </remarks>
public interface IBuildExecutionRegistry
{
    /// <summary>
    /// Record a new build execution at queue time. Returns the
    /// initial state (typically <see cref="BuildExecutionState"/>
    /// with status <see cref="BuildExecutionStatus.Queued"/>).
    /// </summary>
    BuildExecutionState Register(Guid buildId, ImageBuildRequest request);

    /// <summary>
    /// Update the status of an existing build, optionally setting
    /// digest / references / failure metadata. The registry takes a
    /// shallow copy of the supplied state so concurrent readers see
    /// a consistent snapshot.
    /// </summary>
    void Update(Guid buildId, BuildExecutionState state);

    /// <summary>
    /// Look up the current state of a build, or null if the registry
    /// has no record (build never started, or a host restart cleared
    /// it).
    /// </summary>
    BuildExecutionState? TryGet(Guid buildId);
}

/// <summary>
/// Snapshot of a build's lifecycle state.
/// </summary>
public sealed record BuildExecutionState
{
    public required Guid BuildId { get; init; }
    public required Guid TemplateId { get; init; }
    public required BuildExecutionStatus Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    public string? Digest { get; init; }
    public IReadOnlyList<BuildResultReference> References { get; init; } = [];

    /// <summary>Stable error code on terminal-failure, mapped onto the API response in IM10.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable failure message on terminal-failure.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Captured logs on terminal-failure (truncated at the response boundary).</summary>
    public string? FailureLog { get; init; }
}

public enum BuildExecutionStatus
{
    Queued,
    Running,
    Cached,
    Succeeded,
    Failed,
    Cancelled,
}
