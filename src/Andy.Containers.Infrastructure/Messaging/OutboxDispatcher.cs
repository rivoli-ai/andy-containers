// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Andy.Containers.Infrastructure.Messaging;

// Background worker that drains the OutboxEntry table to the message bus.
// One instance per service. Polls at a configurable interval, batches
// pending rows, publishes each to its target subject, records success
// or failure. Rows are never deleted — the outbox doubles as an audit
// log. A separate retention policy may purge published rows older than
// N days.
//
// Retry semantics: on publish failure the row stays pending with
// AttemptCount incremented and LastError set. The next drain tick
// picks it up again. Exponential backoff and poison-message DLQ
// routing are NOT in this scaffold — they land with the NATS
// provider and will look at LastAttemptAt to defer retries.
public sealed class OutboxDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxDispatcher> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly int _batchSize;

    public OutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        ILogger<OutboxDispatcher> logger,
        IOptions<OutboxDispatcherOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _pollInterval = options.Value.PollInterval;
        _batchSize = options.Value.BatchSize;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxDispatcher started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var drained = await DrainOnceAsync(stoppingToken);
                if (drained == 0)
                {
                    await Task.Delay(_pollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OutboxDispatcher tick failed; backing off");
                await Task.Delay(_pollInterval, stoppingToken);
            }
        }

        _logger.LogInformation("OutboxDispatcher stopped");
    }

    internal async Task<int> DrainOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var pending = await db.Set<OutboxEntry>()
            .Where(e => e.PublishedAt == null)
            .OrderBy(e => e.CreatedAt)
            .Take(_batchSize)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return 0;
        }

        foreach (var entry in pending)
        {
            try
            {
                var headers = new MessageHeaders(
                    MsgId: entry.Id,
                    CorrelationId: entry.CorrelationId,
                    CausationId: entry.CausationId,
                    Generation: entry.Generation);

                // Payload is stored as JSON text. IMessageBus.PublishAsync
                // expects an object (it re-serializes). Pass a
                // JsonDocument so the bus has something to serialize
                // back. A follow-up PR may add a raw-bytes publish
                // overload to avoid the round trip.
                using var doc = JsonDocument.Parse(entry.PayloadJson);
                await bus.PublishAsync(entry.Subject, doc.RootElement, headers, ct);

                entry.PublishedAt = DateTimeOffset.UtcNow;
                entry.LastError = null;
            }
            catch (Exception ex)
            {
                entry.AttemptCount++;
                entry.LastAttemptAt = DateTimeOffset.UtcNow;
                entry.LastError = ex.Message;
                _logger.LogWarning(ex,
                    "Outbox entry {EntryId} publish failed (attempt {Attempt})",
                    entry.Id, entry.AttemptCount);
            }
        }

        await db.SaveChangesAsync(ct);
        return pending.Count;
    }
}
