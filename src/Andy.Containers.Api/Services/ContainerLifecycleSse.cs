// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Containers.Storage;
using Microsoft.AspNetCore.Http;

namespace Andy.Containers.Api.Services;

/// <summary>
/// SM.2.6 (rivoli-ai/conductor#2008). Shared SSE serialiser for the
/// fleet-wide container lifecycle phase stream. Used by
/// <c>GET /api/containers/events</c> (ContainersController.Events).
/// </summary>
/// <remarks>
/// Wire format per docs/api-contracts/andy-containers.md §lifecycle:
/// <code>
/// id: &lt;sequence&gt;
/// event: lifecycle
/// data: {"containerId":"...","phase":"running","phaseData":{},"correlationId":"...","timestamp":"..."}
/// </code>
/// terminated by a blank line. Heartbeats are <c>: heartbeat\n\n</c>
/// (SSE comment frame) every <see cref="HeartbeatInterval"/>. The
/// stream stays open until the client disconnects (<paramref name="ct"/>
/// fires).
/// </remarks>
public static class ContainerLifecycleSse
{
    /// <summary>Heartbeat cadence. Keeps idle connections alive through
    /// load-balancer idle timeouts.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task StreamAsync(
        HttpResponse response,
        HttpRequest request,
        IContainerLifecycleBus bus,
        CancellationToken ct,
        Func<ContainerLifecycleEnvelope, CancellationToken, ValueTask<bool>>? isVisible = null)
    {
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no";

        // Honour Last-Event-ID for reconnection resumption.
        long? lastEventId = null;
        if (request.Headers.TryGetValue("Last-Event-ID", out var headerValue) &&
            long.TryParse(headerValue.ToString(), out var parsed))
        {
            lastEventId = parsed;
        }

        // Merge the event stream with a recurring heartbeat timer.
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var eventTask = PumpEventsAsync(response, bus, lastEventId, isVisible, ct);
        var heartbeatTask = PumpHeartbeatsAsync(response, heartbeatCts.Token);

        await Task.WhenAny(eventTask, heartbeatTask);
        await heartbeatCts.CancelAsync();
        // Propagate any exception from the event pump (heartbeat failures
        // are best-effort and silently ignored).
        await eventTask;
    }

    private static async Task PumpEventsAsync(
        HttpResponse response,
        IContainerLifecycleBus bus,
        long? lastEventId,
        Func<ContainerLifecycleEnvelope, CancellationToken, ValueTask<bool>>? isVisible,
        CancellationToken ct)
    {
        await foreach (var envelope in bus.SubscribeAsync(lastEventId, ct))
        {
            // The bus is intentionally fleet-wide. Authorization belongs at
            // the HTTP boundary so every connection receives only containers
            // visible to its principal while publishers remain non-blocking
            // and unaware of request identity.
            if (isVisible is not null && !await isVisible(envelope, ct))
            {
                continue;
            }
            await WriteEventFrameAsync(response, envelope, ct);
        }
    }

    private static async Task PumpHeartbeatsAsync(
        HttpResponse response,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, ct);
                await response.WriteAsync(": heartbeat\n\n", ct);
                await response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* best-effort */ }
    }

    private static async Task WriteEventFrameAsync(
        HttpResponse response,
        ContainerLifecycleEnvelope envelope,
        CancellationToken ct)
    {
        var wire = new LifecycleEventWire(
            envelope.Event.ContainerId,
            envelope.Event.Phase,
            new PhaseDataWire(
                envelope.Event.PhaseData.ExitCode,
                envelope.Event.PhaseData.Reason),
            envelope.Event.CorrelationId,
            envelope.Event.Timestamp);

        var json = JsonSerializer.Serialize(wire, JsonOptions);
        var frame =
            $"id: {envelope.SequenceNumber}\n" +
            "event: lifecycle\n" +
            $"data: {json}\n\n";
        await response.WriteAsync(frame, ct);
        await response.Body.FlushAsync(ct);
    }

    // ---------------------------------------------------------------
    // Wire shapes
    // ---------------------------------------------------------------

    /// <summary>
    /// Wire payload for <c>event: lifecycle</c>. The
    /// <c>phaseData</c> object contains only non-null fields (see
    /// <see cref="JsonOptions"/>).
    /// </summary>
    private sealed record LifecycleEventWire(
        Guid ContainerId,
        string Phase,
        PhaseDataWire PhaseData,
        Guid CorrelationId,
        DateTimeOffset Timestamp);

    private sealed record PhaseDataWire(
        int? ExitCode,
        string? Reason);
}
