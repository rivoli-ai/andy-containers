using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

/// <summary>
/// AP6 (rivoli-ai/andy-containers#108). Spawns
/// <c>andy-cli run --headless --config &lt;path&gt;</c> inside the run's
/// container, waits for exit, maps the AQ2 exit-code contract to a
/// <see cref="RunEventKind"/>, transitions the <see cref="Run"/> entity,
/// and writes the terminal event to the outbox.
/// </summary>
/// <remarks>
/// Stdout/stderr are captured for diagnostic logging only — structured
/// event-stream parsing is AQ3's job, not this runner's. Cancellation
/// propagation, idempotency, and watchdog timeouts beyond
/// <c>ExecAsync</c>'s built-in timeout are intentionally out of scope.
/// </remarks>
public interface IHeadlessRunner
{
    Task<HeadlessRunOutcome> StartAsync(Run run, string configPath, CancellationToken ct = default);
}

/// <summary>
/// Terminal outcome of a headless agent-run spawn. <see cref="ExitCode"/>
/// is the raw process exit; <see cref="Kind"/> + <see cref="Status"/> are
/// the mapped semantic projections written to the outbox + Run row.
/// </summary>
public sealed record HeadlessRunOutcome
{
    public required RunEventKind Kind { get; init; }
    public required RunStatus Status { get; init; }
    public int? ExitCode { get; init; }
    public double? DurationSeconds { get; init; }
    public string? Error { get; init; }
}
