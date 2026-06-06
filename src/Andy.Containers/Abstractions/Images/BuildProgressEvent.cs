namespace Andy.Containers.Abstractions.Images;

/// <summary>
/// Base type for events surfaced through
/// <see cref="IProgress{T}"/> during a build. Concrete subtypes are
/// pattern-matched by the API layer to map onto the SSE event stream
/// served at <c>GET /api/images/build/{id}/events</c>.
/// </summary>
public abstract record BuildProgressEvent
{
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>A new build step started.</summary>
public sealed record BuildStepStartedEvent : BuildProgressEvent
{
    public required string StepName { get; init; }
    public required int StepIndex { get; init; }
    public required int TotalSteps { get; init; }
}

/// <summary>One line of build output captured from the engine.</summary>
public sealed record BuildStepStdoutEvent : BuildProgressEvent
{
    public required string StepName { get; init; }
    public required string Line { get; init; }
}

/// <summary>An error within a build step (does not necessarily fail the build).</summary>
public sealed record BuildStepErrorEvent : BuildProgressEvent
{
    public required string StepName { get; init; }
    public required string Message { get; init; }
}

/// <summary>The build reached a terminal state.</summary>
public sealed record BuildCompletedEvent : BuildProgressEvent
{
    public required BuildOutcome Outcome { get; init; }
    public string? Digest { get; init; }
    public string? FailureReason { get; init; }
}

/// <summary>
/// SM.2.7 (rivoli-ai/conductor#2009). Structured failure event emitted
/// on the build/pull SSE stream when a pull or image-management operation
/// fails. Carries a stable <see cref="Reason"/> enum, a
/// <see cref="Transient"/> flag so consumers can decide between retry
/// (transient) and surface-terminal (permanent), and a free-text
/// <see cref="Detail"/> for operator diagnostics.
///
/// Emitted in addition to (and before) the terminal
/// <see cref="BuildCompletedEvent"/> — the completed event still fires
/// so consumers with a single terminal-event handler keep working
/// unchanged; this event provides the richer taxonomy.
///
/// Wire type discriminator: <c>build-failed</c>.
/// </summary>
public sealed record BuildFailureEvent : BuildProgressEvent
{
    /// <summary>Stable failure reason from the taxonomy.</summary>
    public required BuildFailureReason Reason { get; init; }

    /// <summary>
    /// <c>true</c> when the failure is considered transient (retry may
    /// succeed); <c>false</c> when it is permanent (retry will not help).
    /// Derived deterministically from <see cref="Reason"/> via
    /// <see cref="BuildFailureReasonExtensions.IsTransient"/>.
    /// </summary>
    public required bool Transient { get; init; }

    /// <summary>Human-readable detail for operator diagnostics. Not stable — do not key on it.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// SM.2.7 (rivoli-ai/conductor#2009). Emitted on the build/pull SSE
/// stream when a cache hit is detected, so the consumer can reconcile
/// against a "present" state without inferring it from silence or the
/// synchronous <c>status: "cached"</c> on the initial HTTP response.
///
/// Aligns with Conductor's existing
/// <c>registrySeedingPullCompleted(alreadyPresent: true)</c> event —
/// the consumer maps this SSE event onto <c>alreadyPresent: true</c>
/// without any additional inference.
///
/// Wire type discriminator: <c>cached</c>.
/// </summary>
public sealed record BuildCachedEvent : BuildProgressEvent
{
    /// <summary>
    /// OCI digest of the already-present artifact, e.g.
    /// <c>sha256:abc123...</c>. May be <c>null</c> when the cache
    /// hit was detected before the digest was resolved.
    /// </summary>
    public string? Digest { get; init; }
}

/// <summary>
/// SM.2.7. Stable reason codes for <see cref="BuildFailureEvent.Reason"/>.
/// Classified as transient (retry may help) or permanent (terminal)
/// via <see cref="BuildFailureReasonExtensions.IsTransient"/>.
/// </summary>
public enum BuildFailureReason
{
    // ---- Transient (retry may succeed) ----

    /// <summary>
    /// The registry could not be reached (DNS, TCP, TLS). Transient.
    /// Maps from: network errors, connection refused, SSL handshake
    /// failures.
    /// </summary>
    RegistryUnreachable,

    /// <summary>
    /// The Docker/container engine was unavailable or failed to start.
    /// Transient.
    /// Maps from: <c>ensure_pull_docker_launch_failed.*</c>,
    /// <c>engine_unavailable</c> 503 codes.
    /// </summary>
    EngineUnavailable,

    /// <summary>
    /// The pull was interrupted mid-transfer (timeout, network drop).
    /// Transient.
    /// </summary>
    PullInterrupted,

    // ---- Permanent (surface terminal; retry will not help) ----

    /// <summary>
    /// The requested image tag or repository does not exist in the
    /// registry. Permanent.
    /// Maps from: registry 404, <c>manifest unknown</c> error.
    /// </summary>
    ManifestUnknown,

    /// <summary>
    /// The pulled image's digest does not match the expected/pinned
    /// value. Permanent — the image in the registry has changed or the
    /// spec is wrong; a retry will pull the same wrong digest.
    /// </summary>
    DigestMismatch,

    /// <summary>
    /// The pull completed but the resulting local image failed
    /// verification (signature, policy). Permanent.
    /// </summary>
    ImagePullFailed,

    /// <summary>
    /// A failure reason not covered by the above taxonomy.
    /// Consumers should treat this as permanent (surface to the
    /// operator for manual investigation).
    /// </summary>
    Unknown,
}

/// <summary>
/// SM.2.7. Transient/permanent classification for
/// <see cref="BuildFailureReason"/>.
/// </summary>
public static class BuildFailureReasonExtensions
{
    /// <summary>
    /// Returns <c>true</c> when the failure is considered transient
    /// (a retry may succeed) and <c>false</c> when it is permanent
    /// (surface terminal to the operator; retry will not help).
    /// </summary>
    public static bool IsTransient(this BuildFailureReason reason) => reason switch
    {
        BuildFailureReason.RegistryUnreachable => true,
        BuildFailureReason.EngineUnavailable   => true,
        BuildFailureReason.PullInterrupted     => true,
        BuildFailureReason.ManifestUnknown     => false,
        BuildFailureReason.DigestMismatch      => false,
        BuildFailureReason.ImagePullFailed     => false,
        BuildFailureReason.Unknown             => false,
        _ => false,
    };
}

public enum BuildOutcome
{
    Succeeded,
    Failed,
    Cancelled,
}
