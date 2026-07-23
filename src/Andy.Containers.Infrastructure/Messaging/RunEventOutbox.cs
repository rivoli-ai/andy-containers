// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Messaging;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;

namespace Andy.Containers.Infrastructure.Messaging;

// SM.2.6 (rivoli-ai/conductor#2008). Additional outbox helpers for the
// provisioning-abort discrete event and the lifecycle SSE phase contract.

// Helper for appending a run.* OutboxEntry to the DbContext in the same
// unit of work as the domain change that produced the message. Caller
// controls SaveChangesAsync — the outbox row lands with whatever else is
// pending, so dual-write consistency is preserved by EF's transaction scope.
public static class RunEventOutbox
{
    // Container-lifecycle variant. Subject is keyed on Container.Id —
    // this is the legacy run.* path used by the provisioning worker /
    // orchestration service for create/stop/destroy transitions.
    //
    // outputArtifacts (rivoli-ai/andy-containers#316) is optional and
    // flows straight into the payload. The container path does not
    // persist artifacts onto the Container entity (no column) — the
    // agent-run path is where replay matters; here we publish in-memory
    // and let consumers store them downstream.
    public static void AppendRunEvent(
        this ContainersDbContext db,
        Container container,
        RunEventKind kind,
        int? exitCode = null,
        double? durationSeconds = null,
        IReadOnlyList<RunOutputArtifact>? outputArtifacts = null)
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var payload = new RunEventPayload(
            RunId: container.Id,
            StoryId: container.StoryId,
            Status: container.Status.ToString(),
            ExitCode: exitCode,
            DurationSeconds: durationSeconds,
            OutputArtifacts: outputArtifacts,
            AttemptId: container.Id,
            Sequence: RunEventSequence.Next(container.Id),
            OccurredAt: occurredAt);

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
            CreatedAt = occurredAt
        });
    }

    // SM.2.6. Emit a discrete containerProvisioningAborted event on the
    // andy.containers.events.container.{id}.provisioning_aborted subject.
    // Published in addition to the phase=failed SSE event so downstream
    // services (andy-tasks, andy-issues) can react without subscribing to
    // the SSE stream.
    public static void AppendProvisioningAbortedEvent(
        this ContainersDbContext db,
        Container container,
        ProvisioningAbortReason reason,
        string detail,
        Guid correlationId)
    {
        var payload = new ContainerProvisioningAbortedEvent(
            ContainerId: container.Id,
            Reason: reason.ToWireString(),
            Detail: detail,
            CorrelationId: correlationId,
            AbortedAt: DateTimeOffset.UtcNow);

        var subject =
            $"andy.containers.events.container.{container.Id}.provisioning_aborted";

        db.OutboxEntries.Add(new OutboxEntry
        {
            Id = Guid.NewGuid(),
            Subject = subject,
            PayloadType = typeof(ContainerProvisioningAbortedEvent).FullName,
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
    //
    // outputArtifacts (rivoli-ai/andy-containers#316) is additionally
    // persisted on Run.OutputArtifacts so a late subscriber can replay
    // the manifest off the entity even after the outbox row has been
    // dispatched + reaped.
    public static void AppendAgentRunEvent(
        this ContainersDbContext db,
        Run run,
        RunEventKind kind,
        int? exitCode = null,
        double? durationSeconds = null,
        IReadOnlyList<RunOutputArtifact>? outputArtifacts = null,
        RunProgress? progress = null,
        RunEventOutput? output = null)
    {
        // Persist onto the Run row first so the payload and the entity
        // agree byte-for-byte (the payload reads `run.OutputArtifacts`
        // back via the local variable — assigning before serialise keeps
        // the in-memory and on-the-wire copies identical).
        if (outputArtifacts is not null)
        {
            run.OutputArtifacts = outputArtifacts;
        }

        // conductor#2204. Carry the run's actionable failure reason out over
        // the wire so andy-tasks → Conductor surfaces the real cause (exit
        // code + stderr tail) instead of a synthesised "ended with Failed."
        // Run.Error is set by the runner before this append on every
        // non-success terminal path; null for a clean success.
        var occurredAt = DateTimeOffset.UtcNow;
        var sequence = RunEventSequence.Next(run.Id);
        var attemptId = run.AttemptId == Guid.Empty ? run.Id : run.AttemptId;
        var payload = new RunEventPayload(
            RunId: run.Id,
            StoryId: null,
            Status: run.Status.ToString(),
            ExitCode: exitCode,
            DurationSeconds: durationSeconds,
            OutputArtifacts: outputArtifacts,
            Error: run.Error,
            AttemptId: attemptId,
            Sequence: sequence,
            OccurredAt: occurredAt,
            Progress: progress,
            Output: output);

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
            CreatedAt = occurredAt
        });
    }
}
