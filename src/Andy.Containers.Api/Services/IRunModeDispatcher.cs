using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

/// <summary>
/// AP5 (rivoli-ai/andy-containers#107). Routes a freshly-configured
/// <see cref="Run"/> to one of three execution paths based on
/// <see cref="Run.Mode"/>: headless (spawn andy-cli via AP6), terminal
/// (caller attaches via <c>/api/containers/{id}/terminal</c>), or desktop
/// (reuse the GUI provider — not yet implemented).
/// </summary>
/// <remarks>
/// Owns container selection: assigns <see cref="Run.ContainerId"/> from
/// the run's workspace's default container before invoking AP6, and
/// transitions the run from <see cref="RunStatus.Pending"/> to
/// <see cref="RunStatus.Provisioning"/>. The runner picks up from there.
/// </remarks>
public interface IRunModeDispatcher
{
    Task<RunDispatchOutcome> DispatchAsync(Run run, string configPath, CancellationToken ct = default);
}

/// <summary>
/// Outcome of a dispatch attempt. <see cref="RunDispatchKind.Detached"/>
/// (AX.16, rivoli-ai/conductor#2104) means the headless execution was
/// handed to <see cref="IHeadlessRunLauncher"/> and is running in the
/// background — terminal state is observed via run events / polling,
/// never through this return value.
/// </summary>
public sealed record RunDispatchOutcome
{
    public required RunDispatchKind Kind { get; init; }
    public string? Error { get; init; }

    public static RunDispatchOutcome Detached()
        => new() { Kind = RunDispatchKind.Detached };

    public static RunDispatchOutcome Attachable()
        => new() { Kind = RunDispatchKind.Attachable };

    public static RunDispatchOutcome NotImplemented(string reason)
        => new() { Kind = RunDispatchKind.NotImplemented, Error = reason };

    public static RunDispatchOutcome Failed(string error)
        => new() { Kind = RunDispatchKind.Failed, Error = error };
}

public enum RunDispatchKind
{
    /// <summary>Headless execution detached to the background launcher (AX.16).</summary>
    Detached,

    /// <summary>Terminal mode run is bound to a container and ready for WebSocket attach.</summary>
    Attachable,

    /// <summary>Mode is recognised but no execution path exists yet (desktop).</summary>
    NotImplemented,

    /// <summary>Dispatch could not proceed (no workspace, no default container, runner threw, etc.).</summary>
    Failed,
}
