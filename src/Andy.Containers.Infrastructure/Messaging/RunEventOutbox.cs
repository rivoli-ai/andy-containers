// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Messaging;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;

namespace Andy.Containers.Infrastructure.Messaging;

// Helper for appending a run.* OutboxEntry to the DbContext in the same
// unit of work as the domain change that produced the message. Caller
// controls SaveChangesAsync — the outbox row lands with whatever else is
// pending, so dual-write consistency is preserved by EF's transaction scope.
public static class RunEventOutbox
{
    // Container-lifecycle variant. Subject is keyed on Container.Id —
    // this is the legacy run.* path used by the provisioning worker /
    // orchestration service for create/stop/destroy transitions.
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

    // Agent-run variant (AP6). Subject is keyed on Run.Id — the AP1 entity
    // distinct from Container.Id, so headless-run consumers can correlate
    // back to the run row directly. Status mirrors Run.Status; the
    // CorrelationId chain prefers Run.CorrelationId over a fresh id so
    // ADR-0001 header semantics are preserved end-to-end.
    public static void AppendAgentRunEvent(
        this ContainersDbContext db,
        Run run,
        RunEventKind kind,
        int? exitCode = null,
        double? durationSeconds = null)
    {
        var payload = new RunEventPayload(
            RunId: run.Id,
            StoryId: null,
            Status: run.Status.ToString(),
            ExitCode: exitCode,
            DurationSeconds: durationSeconds);

        var subject = $"andy.containers.events.run.{run.Id}.{kind.ToSubjectKind()}";

        var correlationId = run.CorrelationId == Guid.Empty ? run.Id : run.CorrelationId;

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
