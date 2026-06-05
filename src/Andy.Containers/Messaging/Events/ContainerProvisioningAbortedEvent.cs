// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Containers.Messaging.Events;

/// <summary>
/// SM.2.6 (rivoli-ai/conductor#2008). Discrete outbox event emitted on the
/// <c>andy.containers.events.container.{containerId}.provisioning_aborted</c>
/// subject when a provisioning attempt fails before the runtime is reached
/// (pre-start abort).
/// </summary>
/// <remarks>
/// This event is emitted <em>in addition to</em> the SSE lifecycle
/// <c>phase=failed{reason}</c> transition. The NATS event lets downstream
/// services (andy-tasks, andy-issues) react without subscribing to the SSE
/// stream; the SSE phase transition lets Conductor's SM.4 machine transition
/// state in real time.
///
/// Serialised via <see cref="Andy.Containers.Messaging.EventJson.Options"/>
/// (snake_case) for consistency with all other outbox payloads.
/// </remarks>
public sealed record ContainerProvisioningAbortedEvent(
    /// <summary>
    /// The andy-containers row id of the affected container. Stable
    /// across retry — a new create replaces this row with a new id.
    /// </summary>
    Guid ContainerId,

    /// <summary>
    /// Machine-readable abort reason (wire string, e.g.
    /// <c>"quota_denied"</c>). Consumers that don't recognise the
    /// value MUST treat it as <c>"unknown"</c> without throwing.
    /// </summary>
    string Reason,

    /// <summary>
    /// Human-readable detail carrying the underlying exception message
    /// or provider error text. Never empty — at minimum the exception
    /// type name. Not localised; for diagnostic use only.
    /// </summary>
    string Detail,

    /// <summary>
    /// Correlation id. Equals <c>Container.StoryId</c> when the
    /// container was created in response to a story; otherwise equals
    /// <c>Container.Id</c>. Propagates through the causation chain per
    /// ADR 0001 so a single story-triggered provisioning failure can be
    /// traced end-to-end.
    /// </summary>
    Guid CorrelationId,

    /// <summary>Server UTC wall clock at the moment of abort.</summary>
    DateTimeOffset AbortedAt);
