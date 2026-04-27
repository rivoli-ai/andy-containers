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
    public required string Subject { get; init; }
    /// <summary>One of <c>finished</c>, <c>failed</c>, <c>cancelled</c>, <c>timeout</c>.</summary>
    public required string Kind { get; init; }
    /// <summary>Mirrors the run's status at emission (e.g. <c>Cancelled</c>, <c>Succeeded</c>).</summary>
    public required string Status { get; init; }
    public int? ExitCode { get; init; }
    public double? DurationSeconds { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required Guid CorrelationId { get; init; }

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
            Subject = entry.Subject,
            Kind = kind,
            Status = payload.Status,
            ExitCode = payload.ExitCode,
            DurationSeconds = payload.DurationSeconds,
            Timestamp = entry.CreatedAt,
            CorrelationId = entry.CorrelationId,
        };
    }
}
