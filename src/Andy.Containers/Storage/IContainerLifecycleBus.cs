// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Containers.Storage;

/// <summary>
/// SM.2.6 (rivoli-ai/conductor#2008). In-process pub/sub for fleet-wide
/// container lifecycle phase transitions. The provisioning worker and the
/// orchestration service publish one <see cref="ContainerLifecycleEvent"/>
/// per phase change; the <c>GET /api/containers/events</c> SSE endpoint
/// subscribes to broadcast events to all connected clients.
/// </summary>
/// <remarks>
/// Modelled on <see cref="IBuildEventBus"/> (IM9 / #263) and
/// <see cref="IRunOutputBus"/> (F4.1 / #1934): a rolling in-process buffer
/// with sequence numbers so a subscriber that attaches mid-stream catches up
/// on recent transitions before tailing live. Cloud / multi-host deployments
/// can swap in a network-fan-out variant without changing producers or
/// consumers.
/// </remarks>
public interface IContainerLifecycleBus
{
    /// <summary>
    /// Publish a lifecycle phase transition for a container. Non-blocking;
    /// slow SSE subscribers do not backpressure producers.
    /// </summary>
    void Publish(ContainerLifecycleEvent @event);

    /// <summary>
    /// Subscribe to all future + recently buffered lifecycle events across
    /// ALL containers visible to the principal. Yields in publish order;
    /// completes when <paramref name="ct"/> fires.
    /// </summary>
    /// <param name="lastEventId">
    /// Optional sequence number from a prior connection (from the SSE
    /// <c>Last-Event-ID</c> header). The bus resumes from immediately
    /// after this id. On buffer miss the stream restarts from the oldest
    /// buffered event (clients reconcile via the container list).
    /// </param>
    IAsyncEnumerable<ContainerLifecycleEnvelope> SubscribeAsync(
        long? lastEventId,
        CancellationToken ct);
}

/// <summary>
/// One lifecycle phase event. Serialised to the SSE
/// <c>event: lifecycle</c> wire format by
/// <c>ContainerLifecycleSse</c>.
/// </summary>
public sealed record ContainerLifecycleEvent(
    /// <summary>The andy-containers row id of the affected container.</summary>
    Guid ContainerId,

    /// <summary>
    /// Phase name (snake_case wire string, e.g. <c>"running"</c>,
    /// <c>"failed"</c>). Consumers MUST skip unrecognised values per
    /// the contract.
    /// </summary>
    string Phase,

    /// <summary>
    /// Per-phase payload. Nullable fields are omitted on serialisation
    /// (WhenWritingNull) so legacy consumers keep deserialising without
    /// schema friction.
    /// </summary>
    ContainerLifecyclePhaseData PhaseData,

    /// <summary>
    /// Correlation id. Equals <c>Container.StoryId</c> when the
    /// container was created in response to a story, otherwise equals
    /// <c>Container.Id</c>. Propagated to every SSE event so
    /// Conductor's §7.2 helper can correlate transitions that arrived
    /// out of order.
    /// </summary>
    Guid CorrelationId,

    /// <summary>Server UTC wall clock at the moment of the transition.</summary>
    DateTimeOffset Timestamp);

/// <summary>
/// Per-phase optional payload fields. Unused fields are null and omitted
/// from the wire format.
/// </summary>
public sealed record ContainerLifecyclePhaseData(
    /// <summary>
    /// Process exit code. Present on <c>exited</c> phase only.
    /// </summary>
    int? ExitCode = null,

    /// <summary>
    /// Abort / failure reason (wire string). Present on <c>failed</c>
    /// phase. For pre-start aborts this is the
    /// <see cref="Andy.Containers.Messaging.Events.ProvisioningAbortReason"/>
    /// wire string (e.g. <c>"quota_denied"</c>); for post-start failures
    /// it is the provider's raw error token (e.g. <c>"CrashLoopBackOff"</c>).
    /// </summary>
    string? Reason = null);

/// <summary>
/// A lifecycle event tagged with a fleet-wide monotonic sequence number
/// for <c>Last-Event-ID</c> / <c>since</c> resumption.
/// </summary>
public sealed record ContainerLifecycleEnvelope(
    long SequenceNumber,
    ContainerLifecycleEvent Event);
