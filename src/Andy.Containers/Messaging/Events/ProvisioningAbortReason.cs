// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Containers.Messaging.Events;

/// <summary>
/// SM.2.6 (rivoli-ai/conductor#2008). Taxonomy of reasons a container
/// provisioning attempt can abort before the runtime is reached (pre-start
/// phase). Consumers (Conductor's SM.4 ContainerLifecycle machine) switch
/// on this to surface an actionable "provisioning failed — retry" state
/// rather than a stuck spinner.
/// </summary>
/// <remarks>
/// Each value maps to a wire string via
/// <see cref="ProvisioningAbortReasonExtensions.ToWireString"/>.
/// Consumers MUST treat unrecognised strings as
/// <see cref="Unknown"/> and continue without throwing.
/// </remarks>
public enum ProvisioningAbortReason
{
    /// <summary>
    /// Default / catch-all. Returned when the abort was caused by an
    /// unclassified exception and no narrower value applies.
    /// </summary>
    Unknown,

    /// <summary>
    /// The requesting user hit the per-user simultaneous-container cap
    /// (Conductor #878). The container row was created but never
    /// dispatched to a runtime — destroying it frees a slot.
    /// </summary>
    QuotaDenied,

    /// <summary>
    /// The required container image could not be found in the registry
    /// (missing tag, private repo without credentials, registry outage).
    /// A retry may succeed after the image is pushed or credentials are
    /// fixed.
    /// </summary>
    ImageNotFound,

    /// <summary>
    /// The infrastructure provider (Docker, Apple Containers, cloud
    /// runtime) is unreachable or returned a definitive "cannot start"
    /// error. Typically transient — a retry after the engine recovers
    /// should succeed.
    /// </summary>
    EngineUnavailable,

    /// <summary>
    /// The provisioning attempt was cancelled mid-flight by the server
    /// (e.g. service shutdown, graceful stop). The runtime may or may not
    /// have allocated resources; re-creating the container is safe.
    /// </summary>
    Cancelled,

    /// <summary>
    /// The provisioning exceeded the configured timeout
    /// (<see cref="ContainerProvisioningWorker.ProvisionTimeout"/>)
    /// before the runtime reported success. The container may be
    /// partially started; it is marked Failed and can be destroyed.
    /// </summary>
    Timeout,
}

public static class ProvisioningAbortReasonExtensions
{
    /// <summary>
    /// Maps the enum to the stable snake_case wire string emitted in
    /// SSE <c>phase=failed</c> events and in the
    /// <c>containerProvisioningAborted</c> outbox event.
    /// </summary>
    public static string ToWireString(this ProvisioningAbortReason reason) => reason switch
    {
        ProvisioningAbortReason.QuotaDenied      => "quota_denied",
        ProvisioningAbortReason.ImageNotFound    => "image_not_found",
        ProvisioningAbortReason.EngineUnavailable => "engine_unavailable",
        ProvisioningAbortReason.Cancelled        => "cancelled",
        ProvisioningAbortReason.Timeout          => "timeout",
        _                                        => "unknown",
    };

    /// <summary>
    /// Parses the wire string back to the enum.  Unrecognised values
    /// return <see cref="ProvisioningAbortReason.Unknown"/>.
    /// </summary>
    public static ProvisioningAbortReason FromWireString(string? value) => value switch
    {
        "quota_denied"       => ProvisioningAbortReason.QuotaDenied,
        "image_not_found"    => ProvisioningAbortReason.ImageNotFound,
        "engine_unavailable" => ProvisioningAbortReason.EngineUnavailable,
        "cancelled"          => ProvisioningAbortReason.Cancelled,
        "timeout"            => ProvisioningAbortReason.Timeout,
        _                    => ProvisioningAbortReason.Unknown,
    };
}
