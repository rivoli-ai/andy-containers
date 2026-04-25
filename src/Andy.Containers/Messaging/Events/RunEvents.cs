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

// Terminal-lifecycle kinds published on andy.containers.events.run.{id}.<kind>.
// Container provisioning emits Finished/Failed/Cancelled. AP6's agent-run
// runner additionally emits Timeout, mapped from the AQ2 process exit code 4
// — kept distinct from Failed so consumers (and the Run.Status enum, which
// already has a Timeout member) don't lose the watchdog signal.
public enum RunEventKind
{
    Finished,
    Failed,
    Cancelled,
    Timeout
}

public static class RunEventKindExtensions
{
    public static string ToSubjectKind(this RunEventKind kind) => kind switch
    {
        RunEventKind.Finished => "finished",
        RunEventKind.Failed => "failed",
        RunEventKind.Cancelled => "cancelled",
        RunEventKind.Timeout => "timeout",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
