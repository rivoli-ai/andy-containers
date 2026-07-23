// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Containers.Models;

namespace Andy.Containers.Messaging.Events;

// Payload for andy.containers.events.run.{runId}.{kind} events, per
// ADR 0001 and the Story 15.6 contract in andy-issues. Serialised with
// EventJson.Options (snake_case) when written to the outbox.
//
// RunId is the Container.Id. StoryId is the optional correlation field
// stamped by the caller (andy-issues' SandboxService) at create time.
// Status mirrors the Container's terminal state so consumers don't
// need to parse the subject's trailing kind token.
//
// OutputArtifacts (rivoli-ai/andy-containers#316) is the v2 addition.
// Producers populate it at terminal-event time by walking the agent's
// well-known outputs root. Nullable on the wire (and omitted by the
// EventJson WhenWritingNull policy) so legacy v1 consumers continue to
// deserialise without schema friction; consumers that opt in
// (andy-tasks#275) project it onto TaskNode.OutputDocRefs.
//
// v3 (rivoli-ai/andy-containers#320) extends each RunOutputArtifact with
// an optional DocsRef (DocumentId + LinkId) populated when the bytes
// were successfully uploaded to andy-docs during collection. The
// payload shape itself is unchanged — only the per-artifact record
// grew a new nullable field. v2 consumers continue to deserialise
// successfully (DocsRef is ignored as an unknown property under the
// EventJson tolerant-read policy).
// Error (rivoli-ai/conductor#2204) is the v4 addition. When a run ends
// non-successfully the producer (AP6 HeadlessRunner) stamps Run.Error with
// an actionable, bounded reason — the andy-cli exit code plus a stderr/
// stdout tail, carrying the greppable [AC-HEADLESS-EXIT] code. Before this
// field existed the reason never left andy-containers, so andy-tasks could
// only synthesise a bare "Run <id> ended with Failed." from the kind alone.
// Nullable + omitted-when-null so pre-v4 consumers deserialise unchanged;
// consumers that opt in surface it as the task-failure reason instead of
// the synthesised placeholder.
//
// v5 (#380) adds AttemptId, Sequence, OccurredAt, Progress, and Output.
// Agent lifecycle rows use the transactional outbox; live output uses the
// same payload on the `.output` subject and the same sequence on SSE frames.
public sealed record RunEventPayload(
    Guid RunId,
    Guid? StoryId,
    string Status,
    int? ExitCode,
    double? DurationSeconds,
    IReadOnlyList<RunOutputArtifact>? OutputArtifacts = null,
    string? Error = null,
    Guid? AttemptId = null,
    long? Sequence = null,
    DateTimeOffset? OccurredAt = null,
    RunProgress? Progress = null,
    RunEventOutput? Output = null)
{
    // v5 adds attempt-correlated monotonic lifecycle/output metadata.
    // Nullable additions preserve tolerant reads of legacy v1-v4 events.
    public const int SchemaVersion = 5;

    public int Schema_Version => SchemaVersion;
}

// Terminal-lifecycle kinds published on andy.containers.events.run.{id}.<kind>.
// Container provisioning emits Finished/Failed/Cancelled. AP6's agent-run
// runner additionally emits Timeout, mapped from the AQ2 process exit code 4
// — kept distinct from Failed so consumers (and the Run.Status enum, which
// already has a Timeout member) don't lose the watchdog signal.
public enum RunEventKind
{
    Queued,
    Provisioning,
    Ready,
    Running,
    Progress,
    Output,
    Finished,
    Failed,
    Cancelled,
    Timeout
}

public static class RunEventKindExtensions
{
    public static string ToSubjectKind(this RunEventKind kind) => kind switch
    {
        RunEventKind.Queued => "queued",
        RunEventKind.Provisioning => "provisioning",
        RunEventKind.Ready => "ready",
        RunEventKind.Running => "running",
        RunEventKind.Progress => "progress",
        RunEventKind.Output => "output",
        RunEventKind.Finished => "finished",
        RunEventKind.Failed => "failed",
        RunEventKind.Cancelled => "cancelled",
        RunEventKind.Timeout => "timeout",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}

public sealed record RunProgress(
    string Message,
    double? Percent = null);

public sealed record RunEventOutput(
    string Stream,
    string Line);
