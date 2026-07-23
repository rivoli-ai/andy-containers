using System.Text.Json;
using Andy.Containers.Messaging;
using Andy.Containers.Messaging.Events;

namespace Andy.Containers.Models;

/// <summary>
/// AP8/AP9 wire-shape for an event yielded by <c>run.events</c> (MCP tool),
/// <c>GET /api/runs/{id}/events</c> (HTTP NDJSON stream), and
/// <c>andy-containers-cli runs events</c>. One DTO per
/// <see cref="OutboxEntry"/>. Carries the parsed <see cref="RunEventPayload"/>
/// fields plus the wire metadata consumers want without making them re-parse
/// JSON.
/// </summary>
public sealed record RunEventDto
{
    public required Guid RunId { get; init; }
    // Non-required CLR members preserve tolerant deserialization of v1-v4
    // NDJSON. New v5 producers always populate both.
    public Guid AttemptId { get; init; }
    public long Sequence { get; init; }
    public required string Subject { get; init; }
    /// <summary>Lifecycle kind encoded by the final NATS subject token.</summary>
    public required string Kind { get; init; }
    /// <summary>Mirrors the run's status at emission (e.g. <c>Cancelled</c>, <c>Succeeded</c>).</summary>
    public required string Status { get; init; }
    public int? ExitCode { get; init; }
    public double? DurationSeconds { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required Guid CorrelationId { get; init; }
    public RunProgress? Progress { get; init; }
    public RunEventOutput? Output { get; init; }

    /// <summary>
    /// Parse an <see cref="OutboxEntry"/> into a <see cref="RunEventDto"/>.
    /// Returns null on a malformed payload — callers skip rather than
    /// surfacing a parse error mid-stream.
    /// </summary>
    public static RunEventDto? FromOutbox(OutboxEntry entry)
    {
        RunEventPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<RunEventPayload>(entry.PayloadJson, EventJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null) return null;

        // Subject suffix after the last '.' is the kind: e.g.
        // andy.containers.events.run.{id}.cancelled → "cancelled".
        var lastDot = entry.Subject.LastIndexOf('.');
        var kind = lastDot >= 0 && lastDot < entry.Subject.Length - 1
            ? entry.Subject[(lastDot + 1)..]
            : entry.Subject;

        return new RunEventDto
        {
            RunId = payload.RunId,
            AttemptId = payload.AttemptId ?? payload.RunId,
            Sequence = payload.Sequence ?? entry.CreatedAt.UtcTicks,
            Subject = entry.Subject,
            Kind = kind,
            Status = payload.Status,
            ExitCode = payload.ExitCode,
            DurationSeconds = payload.DurationSeconds,
            Timestamp = payload.OccurredAt ?? entry.CreatedAt,
            CorrelationId = entry.CorrelationId,
            Progress = payload.Progress,
            Output = payload.Output,
        };
    }
}
