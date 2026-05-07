using Andy.Containers.Abstractions.Images;

namespace Andy.Containers.Storage;

/// <summary>
/// In-process pub/sub for build progress events, keyed by build id.
/// The orchestrator's <see cref="IProgress{T}"/> reporter publishes to
/// the bus; the SSE endpoint subscribes per request. Multiple
/// subscribers can attach to the same build (a UI panel + an
/// observability pipe). Buffered so a subscriber that arrives after
/// some events have already fired sees the recent history rather
/// than skipping straight to "now."
/// </summary>
/// <remarks>
/// IM9 (rivoli-ai/andy-containers#263). The default in-process
/// implementation is <c>InMemoryBuildEventBus</c>. Cloud / multi-host
/// deployments will need a network-fan-out variant later (Redis
/// pubsub or a JetStream subject); the contract here is intentionally
/// narrow so a swap doesn't need to touch the orchestrator or the
/// SSE endpoint.
/// </remarks>
public interface IBuildEventBus
{
    /// <summary>
    /// Publish an event for a build. Non-blocking — slow subscribers
    /// don't backpressure the publisher; bounded queues drop old
    /// events when a subscriber falls behind.
    /// </summary>
    void Publish(Guid buildId, BuildProgressEvent @event);

    /// <summary>
    /// Subscribe to all future + buffered events for a build.
    /// Yields each event in publish order; completes when a terminal
    /// <see cref="BuildCompletedEvent"/> is observed or
    /// <paramref name="ct"/> fires.
    /// </summary>
    /// <param name="lastEventId">
    /// Optional sequence number from a prior subscription. The bus
    /// resumes from the next event in its buffer after this id; if
    /// the requested id has already fallen out of the buffer, the
    /// stream restarts from the oldest buffered event (and the
    /// caller can reconcile gaps via the build status snapshot).
    /// </param>
    IAsyncEnumerable<BuildEventEnvelope> SubscribeAsync(
        Guid buildId,
        long? lastEventId,
        CancellationToken ct);

    /// <summary>
    /// Drop all buffered events for a build. Called by the executor
    /// when a build's data is no longer needed (e.g. after persistence
    /// + a grace period for reconnections).
    /// </summary>
    void Forget(Guid buildId);
}

/// <summary>
/// One event in the stream, tagged with a per-build sequence number
/// the SSE endpoint advertises as <c>id:</c> on the wire so clients
/// can resume via <c>Last-Event-ID</c>.
/// </summary>
public sealed record BuildEventEnvelope(
    long SequenceNumber,
    BuildProgressEvent Event);
