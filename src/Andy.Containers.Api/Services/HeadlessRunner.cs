using System.Diagnostics;
using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Messaging;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

public sealed class HeadlessRunner : IHeadlessRunner
{
    private readonly IContainerService _containers;
    private readonly ContainersDbContext _db;
    private readonly ILogger<HeadlessRunner> _logger;

    // Generous upper bound on a single headless run. AQ3 will wire
    // limits.timeout_seconds-aware spawning; until then this is the only
    // ceiling between us and a hung process. ExecAsync surfaces a timeout
    // by throwing OperationCanceledException, which we map to Failed.
    private static readonly TimeSpan DefaultExecTimeout = TimeSpan.FromMinutes(15);

    public HeadlessRunner(
        IContainerService containers,
        ContainersDbContext db,
        ILogger<HeadlessRunner> logger)
    {
        _containers = containers;
        _db = db;
        _logger = logger;
    }

    public async Task<HeadlessRunOutcome> StartAsync(Run run, string configPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        if (run.ContainerId is not { } containerId)
        {
            // AP5 (mode dispatcher) is responsible for assigning ContainerId.
            // If it hasn't run, AP6 has nothing to spawn against.
            var error = $"Run {run.Id} has no ContainerId — AP5 must assign one before AP6 can spawn.";
            _logger.LogError("{Error}", error);
            return await TerminateAsync(run, RunEventKind.Failed, RunStatus.Failed,
                exitCode: null, durationSeconds: null, error: error, CancellationToken.None);
        }

        if (ct.IsCancellationRequested)
        {
            // Caller cancelled before we even started — short-circuit to
            // Cancelled without trying to drive intermediate transitions
            // (the SaveChanges below would just throw on the same token).
            return await TerminateAsync(run, RunEventKind.Cancelled, RunStatus.Cancelled,
                exitCode: null, durationSeconds: 0, error: "Cancelled before spawn", CancellationToken.None);
        }

        var sw = Stopwatch.StartNew();
        // AP1's state-machine requires Pending → Provisioning → Running
        // before any terminal transition. We compress all three into the
        // span of one ExecAsync because AP5 isn't writing them yet; once
        // AP5 emits Provisioning itself, drop that transition here.
        SafeTransition(run, RunStatus.Provisioning);
        SafeTransition(run, RunStatus.Running);
        await _db.SaveChangesAsync(ct);

        var command = $"andy-cli run --headless --config {ShellEscape(configPath)}";
        ExecResult result;
        try
        {
            _logger.LogInformation(
                "Spawning headless agent for Run {RunId} in container {ContainerId} with config {ConfigPath}",
                run.Id, containerId, configPath);

            result = await _containers.ExecAsync(containerId, command, DefaultExecTimeout, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            _logger.LogWarning("Headless spawn for Run {RunId} cancelled by caller", run.Id);
            return await TerminateAsync(run, RunEventKind.Cancelled, RunStatus.Cancelled,
                exitCode: null, durationSeconds: sw.Elapsed.TotalSeconds, error: "Cancelled by caller", CancellationToken.None);
        }
        catch (OperationCanceledException ex)
        {
            // ExecAsync's internal timeout fired — distinct from the AQ2
            // exit-code 4 path, but semantically the same outcome.
            sw.Stop();
            _logger.LogError(ex, "Headless spawn for Run {RunId} hit ExecAsync timeout after {Elapsed}s",
                run.Id, sw.Elapsed.TotalSeconds);
            return await TerminateAsync(run, RunEventKind.Timeout, RunStatus.Timeout,
                exitCode: null, durationSeconds: sw.Elapsed.TotalSeconds, error: "ExecAsync timeout", CancellationToken.None);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Headless spawn for Run {RunId} failed before exit: {Message}", run.Id, ex.Message);
            return await TerminateAsync(run, RunEventKind.Failed, RunStatus.Failed,
                exitCode: null, durationSeconds: sw.Elapsed.TotalSeconds, error: ex.Message, CancellationToken.None);
        }

        sw.Stop();
        var durationSeconds = sw.Elapsed.TotalSeconds;

        if (!string.IsNullOrEmpty(result.StdErr))
        {
            _logger.LogDebug("Run {RunId} stderr: {StdErr}", run.Id, result.StdErr);
        }
        if (!string.IsNullOrEmpty(result.StdOut))
        {
            _logger.LogDebug("Run {RunId} stdout: {StdOut}", run.Id, result.StdOut);
        }

        var (kind, status) = MapExitCode(result.ExitCode);
        _logger.LogInformation(
            "Run {RunId} exited with code {ExitCode} → {Kind}/{Status} after {Duration}s",
            run.Id, result.ExitCode, kind, status, durationSeconds);

        return await TerminateAsync(run, kind, status,
            exitCode: result.ExitCode, durationSeconds: durationSeconds,
            error: status == RunStatus.Succeeded ? null : Truncate(result.StdErr, 500), ct);
    }

    // AQ2 (rivoli-ai/andy-cli#47) exit-code contract. Keep this mapping in
    // sync with HeadlessExitCode in andy-cli — the two enums are parallel
    // by design but live in separate repos so they have to be re-checked
    // whenever either side changes.
    private static (RunEventKind Kind, RunStatus Status) MapExitCode(int exitCode) => exitCode switch
    {
        0 => (RunEventKind.Finished, RunStatus.Succeeded),
        1 => (RunEventKind.Failed, RunStatus.Failed),
        2 => (RunEventKind.Failed, RunStatus.Failed),
        3 => (RunEventKind.Cancelled, RunStatus.Cancelled),
        4 => (RunEventKind.Timeout, RunStatus.Timeout),
        5 => (RunEventKind.Failed, RunStatus.Failed),
        _ => (RunEventKind.Failed, RunStatus.Failed),
    };

    private async Task<HeadlessRunOutcome> TerminateAsync(
        Run run, RunEventKind kind, RunStatus status,
        int? exitCode, double? durationSeconds, string? error,
        CancellationToken ct)
    {
        try
        {
            // Best-effort transition. If the run is already terminal (e.g.
            // a parallel cancel beat us here), keep the existing status.
            if (RunStatusTransitions.CanTransition(run.Status, status))
            {
                run.TransitionTo(status);
            }

            run.ExitCode ??= exitCode;
            if (!string.IsNullOrEmpty(error))
            {
                run.Error ??= error;
            }

            _db.AppendAgentRunEvent(run, kind, exitCode, durationSeconds);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to persist terminal outcome for Run {RunId}: {Message}",
                run.Id, ex.Message);
        }

        return new HeadlessRunOutcome
        {
            Kind = kind,
            Status = status,
            ExitCode = exitCode,
            DurationSeconds = durationSeconds,
            Error = error,
        };
    }

    private void SafeTransition(Run run, RunStatus next)
    {
        if (!RunStatusTransitions.CanTransition(run.Status, next))
        {
            return;
        }

        try
        {
            run.TransitionTo(next);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex,
                "Run {RunId} could not transition {From} → {To}: {Message}",
                run.Id, run.Status, next, ex.Message);
        }
    }

    // POSIX single-quote escape — safe for /bin/sh -c "...". Single quotes
    // close, '\'' inserts a literal quote, single quotes reopen. We don't
    // bother covering edge cases (NULs etc.) because configPath comes
    // from HeadlessConfigWriter which mints filesystem-safe paths.
    private static string ShellEscape(string value)
        => "'" + value.Replace("'", "'\\''") + "'";

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length <= max ? value : value[..max] + "...";
    }
}
