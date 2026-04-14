// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Messaging;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;

namespace Andy.Containers.Infrastructure.Messaging;

// Helper for appending a run.* OutboxEntry to the DbContext in the same
// unit of work as the container's status transition. Caller controls
// SaveChangesAsync — the outbox row lands with whatever else is pending,
// so dual-write consistency is preserved by EF's transaction scope.
public static class RunEventOutbox
{
    // Build and attach a run event outbox row. Does not SaveChanges.
    public static void AppendRunEvent(
        this ContainersDbContext db,
        Container container,
        RunEventKind kind,
        int? exitCode = null,
        double? durationSeconds = null)
    {
        var payload = new RunEventPayload(
            RunId: container.Id,
            StoryId: container.StoryId,
            Status: container.Status.ToString(),
            ExitCode: exitCode,
            DurationSeconds: durationSeconds);

        var subject = $"andy.containers.events.run.{container.Id}.{kind.ToSubjectKind()}";

        var correlationId = container.StoryId ?? container.Id;

        db.OutboxEntries.Add(new OutboxEntry
        {
            Id = Guid.NewGuid(),
            Subject = subject,
            PayloadType = typeof(RunEventPayload).FullName,
            PayloadJson = JsonSerializer.Serialize(payload, EventJson.Options),
            CorrelationId = correlationId,
            CausationId = null,
            Generation = 0,
            CreatedAt = DateTimeOffset.UtcNow
        });
    }
}
