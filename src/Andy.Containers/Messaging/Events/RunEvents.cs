// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Containers.Messaging.Events;

// Payload for andy.containers.events.run.{runId}.{kind} events, per
// ADR 0001 and the Story 15.6 contract in andy-issues. Serialised with
// EventJson.Options (snake_case) when written to the outbox.
//
// RunId is the Container.Id. StoryId is the optional correlation field
// stamped by the caller (andy-issues' SandboxService) at create time.
// Status mirrors the Container's terminal state so consumers don't
// need to parse the subject's trailing kind token.
public sealed record RunEventPayload(
    Guid RunId,
    Guid? StoryId,
    string Status,
    int? ExitCode,
    double? DurationSeconds)
{
    public const int SchemaVersion = 1;

    public int Schema_Version => SchemaVersion;
}

// Three terminal-lifecycle kinds are published. Mapping to container
// status transitions:
//   Finished  — StopContainerAsync (clean shutdown)
//   Failed    — MarkFailedAsync in ProvisioningWorker (provision failure)
//   Cancelled — DestroyContainerAsync (explicit teardown)
public enum RunEventKind
{
    Finished,
    Failed,
    Cancelled
}

public static class RunEventKindExtensions
{
    public static string ToSubjectKind(this RunEventKind kind) => kind switch
    {
        RunEventKind.Finished => "finished",
        RunEventKind.Failed => "failed",
        RunEventKind.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
