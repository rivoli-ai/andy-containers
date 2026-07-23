using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Containers.Storage;
using Microsoft.AspNetCore.Http;

namespace Andy.Containers.Api.Services;

/// <summary>
/// F4.1 (rivoli-ai/conductor#1934). Shared SSE serialiser for the
/// mid-run agent output stream. Used by both
/// <c>GET /api/runs/{id}/output</c> and
/// <c>GET /api/containers/{id}/logs?follow=1</c> so the wire format,
/// <c>Last-Event-ID</c> resumption, and terminal-stop semantics live in
/// exactly one place.
/// </summary>
/// <remarks>
/// Wire format mirrors the build-progress SSE endpoint (IM9 / #263) and
/// matches what Conductor's <c>AndyContainersSSEStreamFactory</c> already
/// expects from the container-logs feed:
/// <code>
/// id: &lt;sequence&gt;
/// event: log
/// data: {"stream":"stdout","line":"...","timestamp":"2026-..."}
/// </code>
/// terminated by a blank line. <c>stream</c> is the camelCase string enum
/// (<c>stdout</c>/<c>stderr</c>) the Swift <c>ContainerLogStream</c>
/// decoder keys off. The stream closes when the bus signals terminal.
/// </remarks>
public static class RunOutputSse
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task StreamAsync(
        HttpResponse response,
        HttpRequest request,
        IRunOutputBus bus,
        Guid runId,
        CancellationToken ct,
        TimeSpan? heartbeatInterval = null,
        bool honorLogQuery = false)
    {
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no";

        // Honour Last-Event-ID for reconnection — the bus's buffered
        // replay picks up after the supplied id (or restarts from the
        // oldest buffered line if the id has aged out).
        long? lastEventId = null;
        if (request.Headers.TryGetValue("Last-Event-ID", out var headerValue) &&
            long.TryParse(headerValue.ToString(), out var parsed))
        {
            lastEventId = parsed;
        }

        int? tail = null;
        DateTimeOffset? since = null;
        var follow = true;
        if (honorLogQuery)
        {
            tail = 200;
            if (int.TryParse(request.Query["tail"], out var requestedTail))
            {
                tail = Math.Clamp(requestedTail, 0, 1000);
            }
            if (DateTimeOffset.TryParse(request.Query["since"], out var requestedSince))
            {
                since = requestedSince;
            }
            follow = int.TryParse(request.Query["follow"], out var requestedFollow)
                && requestedFollow == 1;
        }

        var interval = heartbeatInterval ?? HeartbeatInterval;
        await using var enumerator = bus
            .SubscribeAsync(runId, lastEventId, ct, tail, since, follow)
            .GetAsyncEnumerator(ct);
        var moveNext = enumerator.MoveNextAsync().AsTask();

        try
        {
            while (true)
            {
                var heartbeat = Task.Delay(interval, ct);
                if (await Task.WhenAny(moveNext, heartbeat) == heartbeat)
                {
                    await response.WriteAsync(": heartbeat\n\n", ct);
                    await response.Body.FlushAsync(ct);
                    continue;
                }

                if (!await moveNext)
                {
                    break;
                }

                await WriteFrameAsync(response, enumerator.Current, ct);
                moveNext = enumerator.MoveNextAsync().AsTask();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected. The request-aborted token is the normal
            // lifetime boundary for a following SSE connection.
        }
    }

    public static async Task WriteTerminalErrorAsync(
        HttpResponse response,
        string code,
        string message,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(
            new TerminalErrorWire(code, message),
            JsonOptions);
        await response.WriteAsync(
            $"event: terminal-error\ndata: {json}\n\n",
            ct);
        await response.Body.FlushAsync(ct);
    }

    private static async Task WriteFrameAsync(
        HttpResponse response, RunOutputEnvelope envelope, CancellationToken ct)
    {
        var payload = new RunOutputWire(
            envelope.Line.Stream,
            envelope.Line.Line,
            envelope.Line.Timestamp);
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        var frame =
            $"id: {envelope.SequenceNumber}\n" +
            "event: log\n" +
            $"data: {json}\n\n";
        await response.WriteAsync(frame, ct);
        await response.Body.FlushAsync(ct);
    }

    // Wire shape consumed by Conductor's LogEventPayload decoder:
    // { stream: "stdout"|"stderr", line: string, timestamp: ISO-8601 }.
    private sealed record RunOutputWire(
        RunOutputStream Stream,
        string Line,
        DateTimeOffset Timestamp);

    private sealed record TerminalErrorWire(string Code, string Message);
}
