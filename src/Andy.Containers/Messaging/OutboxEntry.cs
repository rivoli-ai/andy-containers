// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Containers.Messaging;

// Transactional outbox row per ADR 0001. Publishers write to this table
// in the same transaction as the domain change that produced the message;
// the OutboxDispatcher background worker drains pending rows to the bus.
// This guarantees at-least-once delivery without dual-write consistency
// problems — a crash between commit and publish leaves the row pending
// and it is picked up on the next dispatcher tick.
public class OutboxEntry
{
    // Primary key. Also used as the MsgId when published — the outbox
    // row id IS the message id, so reprocessing is naturally idempotent
    // from the consumer's perspective.
    public Guid Id { get; set; }

    // NATS subject the message will be published to. Must follow the
    // taxonomy defined in ADR 0001:
    //   andy.<service>.events.<entity>.<id>.<kind>
    public string Subject { get; set; } = string.Empty;

    // Fully-qualified CLR type name of the payload. Used by consumers
    // to pick the right deserialization target. Optional — subjects
    // already encode intent, but this is a useful safety net.
    public string? PayloadType { get; set; }

    // JSON-encoded payload. Stored as text for provider portability.
    public string PayloadJson { get; set; } = "{}";

    // Correlation chain, copied into MessageHeaders at publish time.
    public Guid CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
    public int Generation { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Nullable sentinels for dispatch state. PublishedAt is set on the
    // first successful publish; rows are never deleted so the outbox
    // doubles as an audit log. A separate retention policy job may
    // purge rows older than N days.
    public DateTimeOffset? PublishedAt { get; set; }

    // Attempt counter and last-error diagnostics for failed publishes.
    // The dispatcher uses these to surface poison messages.
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string? LastError { get; set; }
}
