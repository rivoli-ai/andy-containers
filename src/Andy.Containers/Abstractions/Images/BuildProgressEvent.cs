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

public enum BuildOutcome
{
    Succeeded,
    Failed,
    Cancelled,
}
