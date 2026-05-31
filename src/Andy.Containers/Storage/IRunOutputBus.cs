namespace Andy.Containers.Storage;

/// <summary>
/// F4.1 (rivoli-ai/conductor#1934). In-process pub/sub for the
/// MID-RUN incremental output of a headless agent run, keyed by run id.
/// The AP6 runner (<c>HeadlessRunner</c>) publishes each stdout/stderr
/// line of <c>andy-cli</c> as it is produced; the
/// <c>GET /api/runs/{id}/output</c> SSE endpoint and the
/// <c>GET /api/containers/{id}/logs?follow=1</c> SSE endpoint subscribe
/// per request and stream the lines to the caller before the run is
/// terminal.
/// </summary>
/// <remarks>
/// Modelled on <see cref="IBuildEventBus"/> (IM9 / #263): a per-run ring
/// buffer with monotonic sequence numbers so a subscriber that attaches
/// mid-run catches up, then tails live; resumption keys off the sequence
/// number via <c>Last-Event-ID</c>; bounded queues drop history rather
/// than backpressure the runner; the stream closes after a terminal
/// marker + a final drain so the SSE client disconnects rather than
/// hanging.
///
/// The default in-process implementation is
/// <c>InMemoryRunOutputBus</c>. Cloud / multi-host deployments will need
/// a network-fan-out variant later (the same swap the build bus contract
/// anticipates); the contract here is intentionally narrow so the runner
/// and the SSE endpoints don't have to change.
/// </remarks>
public interface IRunOutputBus
{
    /// <summary>
    /// Publish one output line for a run. Non-blocking — slow
    /// subscribers don't backpressure the runner; bounded queues drop
    /// old lines when a subscriber falls behind.
    /// </summary>
    void Publish(Guid runId, RunOutputLine line);

    /// <summary>
    /// Mark a run's output stream terminal. Subscribers drain any
    /// buffered lines then complete cleanly; subscribers that attach
    /// after this still replay the buffer then close. Idempotent.
    /// </summary>
    void Complete(Guid runId);

    /// <summary>
    /// Subscribe to all future + buffered output lines for a run.
    /// Yields each line in publish order; completes when the run's
    /// output is marked terminal via <see cref="Complete"/> or
    /// <paramref name="ct"/> fires.
    /// </summary>
    /// <param name="lastEventId">
    /// Optional sequence number from a prior subscription. The bus
    /// resumes from the next line in its buffer after this id; if the
    /// requested id has already fallen out of the buffer, the stream
    /// restarts from the oldest buffered line (and the caller can
    /// reconcile gaps via the run status snapshot).
    /// </param>
    IAsyncEnumerable<RunOutputEnvelope> SubscribeAsync(
        Guid runId,
        long? lastEventId,
        CancellationToken ct);

    /// <summary>
    /// Drop all buffered lines for a run. Called by the runner once a
    /// run's output is no longer needed (terminal + a grace period for
    /// reconnections).
    /// </summary>
    void Forget(Guid runId);
}

/// <summary>Which standard stream a run output line came from.</summary>
public enum RunOutputStream
{
    Stdout,
    Stderr,
}

/// <summary>
/// One line of mid-run agent output. <paramref name="Timestamp"/> is the
/// server clock at publish time.
/// </summary>
public sealed record RunOutputLine(
    RunOutputStream Stream,
    string Line,
    DateTimeOffset Timestamp);

/// <summary>
/// One line in the stream, tagged with a per-run sequence number the SSE
/// endpoint advertises as <c>id:</c> on the wire so clients can resume
/// via <c>Last-Event-ID</c>.
/// </summary>
public sealed record RunOutputEnvelope(
    long SequenceNumber,
    RunOutputLine Line);
