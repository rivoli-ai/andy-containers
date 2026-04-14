// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;

namespace Andy.Containers.Messaging;

// Cross-service messaging abstraction. Implementations publish to a bus
// (default: NATS JetStream per ADR 0001) and yield incoming messages from
// durable subscriptions.
//
// This interface is deliberately small and shape-identical to andy-tasks'
// IMessageBus so the two services can eventually share a NuGet package
// without breaking callers on either side. Subject convention and
// causation rules are enforced by callers, not by the bus — the bus is a
// transport.
public interface IMessageBus
{
    // Publish a single message. The bus serializes payload to JSON and
    // attaches headers. Callers should not call this directly; prefer
    // writing to the OutboxEntry table inside the same transaction as
    // the domain change, and let the OutboxDispatcher drain it. Direct
    // publish is permitted only for ephemeral messages that do not need
    // at-least-once delivery (e.g. system.health heartbeats).
    Task PublishAsync(
        string subject,
        object payload,
        MessageHeaders headers,
        CancellationToken ct = default);

    // Create a durable subscription. The stream yields incoming messages
    // until cancellation or until the bus terminates. Consumers are
    // expected to call Ack/Nack on each IncomingMessage before taking
    // the next. Generation overflow (> MessageHeaders.MaxGeneration) is
    // handled by the bus: offending messages are dropped before reaching
    // the consumer, with an error-level log that includes the full
    // causation chain.
    IAsyncEnumerable<IncomingMessage> SubscribeAsync(
        string subjectFilter,
        SubscriptionOptions options,
        CancellationToken ct = default);
}

// Four-field header envelope required on every message per ADR 0001.
public sealed record MessageHeaders(
    Guid MsgId,
    Guid CorrelationId,
    Guid? CausationId,
    int Generation
)
{
    // Hop limit from ADR 0001. Messages exceeding this generation count
    // are dropped by the bus as a runtime circuit breaker for cycles.
    public const int MaxGeneration = 10;

    // Start a new causation chain. Use for messages triggered directly
    // by user input or by a scheduled worker tick — anything not
    // produced in response to an incoming message.
    public static MessageHeaders NewRoot(Guid? correlationId = null)
    {
        var id = Guid.NewGuid();
        return new MessageHeaders(
            MsgId: id,
            CorrelationId: correlationId ?? id,
            CausationId: null,
            Generation: 0);
    }

    // Continue an existing causation chain. Use when emitting a message
    // in response to an incoming message from another service.
    public static MessageHeaders Follow(MessageHeaders parent)
    {
        return new MessageHeaders(
            MsgId: Guid.NewGuid(),
            CorrelationId: parent.CorrelationId,
            CausationId: parent.MsgId,
            Generation: parent.Generation + 1);
    }

    public bool ExceedsGenerationLimit => Generation > MaxGeneration;
}

// Abstract incoming-message wrapper. Concrete implementations (e.g.
// NatsIncomingMessage) attach the provider-specific ack/nack mechanism.
public abstract class IncomingMessage
{
    public required MessageHeaders Headers { get; init; }
    public required string Subject { get; init; }
    public required ReadOnlyMemory<byte> Payload { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }

    // Decode the payload as the given type. Returns null if the payload
    // is empty. Defaults to the canonical ADR 0001 wire options
    // (EventJson.Options) so producers and consumers don't need to
    // negotiate snake_case / camelCase out of band.
    public T? Deserialize<T>(JsonSerializerOptions? options = null) where T : class
    {
        if (Payload.IsEmpty) return null;
        return JsonSerializer.Deserialize<T>(Payload.Span, options ?? EventJson.Options);
    }

    // Acknowledge successful processing. The bus advances its durable
    // consumer offset and will not redeliver this message.
    public abstract Task AckAsync(CancellationToken ct = default);

    // Negative-acknowledge. The bus will redeliver according to its
    // retry policy up to SubscriptionOptions.MaxDeliver attempts, then
    // move the message to the dead-letter subject.
    public abstract Task NackAsync(CancellationToken ct = default);
}

public sealed record SubscriptionOptions(
    // Durable consumer name. Required for JetStream subscriptions so
    // consumer offsets survive restarts. Must be stable across service
    // restarts — typically "<service-name>.<purpose>" e.g.
    // "andy-containers.run-events".
    string DurableName,

    // Maximum delivery attempts before moving to the dead-letter
    // subject. Default 10 per ADR 0001.
    int MaxDeliver = 10,

    // Whether the consumer must explicitly Ack/Nack each message.
    // When false, the bus auto-acks on delivery (at-most-once).
    bool ManualAck = true,

    // Optional narrower filter within the subscription's base filter.
    string? SubjectFilter = null
);
