// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Containers.Infrastructure.Messaging;

// Knobs for the OutboxDispatcher background worker. Bound from the
// "Messaging:Outbox" configuration section. Integration tests override
// PollInterval down to ~50ms so the end-to-end loops finish in tens of
// milliseconds instead of seconds.
public sealed class OutboxDispatcherOptions
{
    public const string SectionName = "Messaging:Outbox";

    // Delay between drains when the outbox is empty. When a drain
    // finds rows, the worker loops immediately to keep up with a
    // burst; only the empty-poll path sleeps.
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    // Max rows per drain. Bounds the transaction size and the failure
    // blast-radius of a poison message.
    public int BatchSize { get; set; } = 100;
}
