// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Containers.Api.Services;

/// <summary>
/// SM.2.6 (rivoli-ai/conductor#2008). Thrown by
/// <see cref="IContainerStatusClassifier"/> when the service can
/// classify the failure mode of a <c>GET /api/containers/{id}</c> attempt.
/// The controller maps each sub-class to the appropriate HTTP status code
/// so Conductor's §7.2 helper can distinguish proxy-route-staleness (503)
/// from genuine deletion (404) from authentication failure (401).
/// </summary>
public abstract class ContainerStatusException : Exception
{
    /// <summary>
    /// Correlation / execution id carried into the response body and
    /// headers so the SM.0.4 helper can correlate a status error
    /// against in-flight lifecycle SSE events.
    /// </summary>
    public Guid CorrelationId { get; }

    protected ContainerStatusException(string message, Guid correlationId, Exception? inner = null)
        : base(message, inner)
    {
        CorrelationId = correlationId;
    }
}

/// <summary>
/// The infrastructure provider (Docker daemon, Apple Containers runtime,
/// cloud proxy) is currently unreachable or circuit-broken. The container
/// row exists in the database but its current runtime state cannot be
/// confirmed. This is a TRANSIENT condition — the client SHOULD retry after
/// <c>Retry-After</c> seconds (default 30).
///
/// Maps to HTTP 503 Service Unavailable.
/// </summary>
public sealed class ContainerRuntimeUnavailableException : ContainerStatusException
{
    /// <summary>Suggested retry delay in seconds.</summary>
    public int RetryAfterSeconds { get; }

    /// <summary>
    /// Human-readable error code for structured logging/alerts.
    /// </summary>
    public const string ErrorCode = "CONTAINER_RUNTIME_UNAVAILABLE";

    public ContainerRuntimeUnavailableException(
        Guid containerId,
        Guid correlationId,
        string detail,
        int retryAfterSeconds = 30,
        Exception? inner = null)
        : base(
            $"Container runtime is temporarily unavailable for container {containerId}: {detail}",
            correlationId,
            inner)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }
}

/// <summary>
/// The container has been confirmed deleted / does not exist and has never
/// existed with this id for the requesting principal. This is a SUSTAINED
/// (non-transient) 404 — the client SHOULD NOT retry without a user action.
///
/// Maps to HTTP 404 Not Found.
/// </summary>
public sealed class ContainerNotFoundException : ContainerStatusException
{
    public const string ErrorCode = "CONTAINER_NOT_FOUND";

    public ContainerNotFoundException(Guid containerId, Guid correlationId)
        : base($"Container {containerId} not found.", correlationId)
    {
    }
}
