namespace Andy.Containers.Api.Services;

/// <summary>
/// AP7 (rivoli-ai/andy-containers#109). In-process registry of running
/// agent-run handles, keyed by Run.Id. The headless runner registers a
/// linked CTS at spawn so the cancel endpoint can signal it without
/// reaching across DI scopes; the same registration carries a
/// <see cref="System.Threading.Tasks.TaskCompletionSource"/> so the
/// cancel endpoint can await the runner's terminal write before it
/// returns the DTO.
/// </summary>
/// <remarks>
/// Singleton-scoped because runner registrations span request scopes:
/// AP6 today is synchronous (the controller awaits StartAsync), but the
/// cancel POST arrives on a different request. The registry lives in
/// process memory only — once AP5's queue moves runs to a worker and
/// horizontal scale-out lands, this needs to be replaced with a control
/// channel that survives across nodes (likely the andy-cli cancel
/// command over the AQ3 control plane). Until then, single-node is fine.
/// </remarks>
public interface IRunCancellationRegistry
{
    /// <summary>
    /// Register a run as actively executing. The returned handle owns
    /// the linked CTS used to signal cancellation; pass
    /// <see cref="IRunRegistration.Token"/> into ExecAsync. Disposing
    /// the handle removes the entry and signals waiters that the run
    /// has reached a terminal state — call from a <c>finally</c> so
    /// every exit path (success, cancel, throw) signals.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown if a registration already exists for <paramref name="runId"/>.
    /// AP6 starts at most one runner per Run; double-register indicates
    /// a caller bug, not a normal race.
    /// </exception>
    IRunRegistration Register(Guid runId, CancellationToken outerToken);

    /// <summary>
    /// Signal cancellation to the registered runner for
    /// <paramref name="runId"/>. Returns false when no runner is
    /// active — callers (the cancel endpoint) take that as the
    /// "no in-flight process to signal" branch.
    /// </summary>
    bool TryCancel(Guid runId);

    /// <summary>
    /// Await the runner's terminal write for <paramref name="runId"/>,
    /// or the supplied timeout — whichever comes first. Returns true
    /// on terminal observation, false on timeout. Returns true
    /// immediately if the run is not registered (already terminal or
    /// never registered), so callers don't have to special-case the
    /// race between TryCancel and the runner's finally block.
    /// </summary>
    Task<bool> WaitForTerminalAsync(Guid runId, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// Active-run handle returned by <see cref="IRunCancellationRegistry.Register"/>.
/// Disposing removes the entry and signals waiters.
/// </summary>
public interface IRunRegistration : IDisposable
{
    /// <summary>
    /// Cancellation token linked to the registry's CTS and the caller's
    /// outer token. Pass to ExecAsync so a TryCancel signal aborts the
    /// in-flight Docker exec stream.
    /// </summary>
    CancellationToken Token { get; }
}
