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
/// Outcome of a dispatch attempt. <see cref="RunDispatchKind.Started"/>
/// carries the inner <see cref="HeadlessRunOutcome"/> so callers can
/// observe headless runs end-to-end without reaching for AP6 directly;
/// the other kinds leave that null.
/// </summary>
public sealed record RunDispatchOutcome
{
    public required RunDispatchKind Kind { get; init; }
    public string? Error { get; init; }
    public HeadlessRunOutcome? HeadlessOutcome { get; init; }

    public static RunDispatchOutcome Started(HeadlessRunOutcome inner)
        => new() { Kind = RunDispatchKind.Started, HeadlessOutcome = inner };

    public static RunDispatchOutcome Attachable()
        => new() { Kind = RunDispatchKind.Attachable };

    public static RunDispatchOutcome NotImplemented(string reason)
        => new() { Kind = RunDispatchKind.NotImplemented, Error = reason };

    public static RunDispatchOutcome Failed(string error)
        => new() { Kind = RunDispatchKind.Failed, Error = error };
}

public enum RunDispatchKind
{
    /// <summary>Headless run started via AP6; <c>HeadlessOutcome</c> populated.</summary>
    Started,

    /// <summary>Terminal mode run is bound to a container and ready for WebSocket attach.</summary>
    Attachable,

    /// <summary>Mode is recognised but no execution path exists yet (desktop).</summary>
    NotImplemented,

    /// <summary>Dispatch could not proceed (no workspace, no default container, runner threw, etc.).</summary>
    Failed,
}
