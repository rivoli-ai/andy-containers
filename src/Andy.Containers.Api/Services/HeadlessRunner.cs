using System.Diagnostics;
using System.Text.Json;
using Andy.Containers.Abstractions;
using Andy.Containers.Configurator;
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
    private readonly IRunCancellationRegistry _cancellation;
    private readonly ITokenIssuer _tokens;
    private readonly ILogger<HeadlessRunner> _logger;

    // Outer-watchdog grace: AQ3 honours limits.timeout_seconds internally
    // and exits with code 4 (→ RunEventKind.Timeout) when its CTS fires.
    // We let it have a head start before our outer ExecAsync ceiling so
    // the AQ3 self-timeout is what we observe — the outer one is reserved
    // for genuinely hung processes that don't honour their own deadline.
    private static readonly TimeSpan OuterGrace = TimeSpan.FromSeconds(30);

    // Fallback when the config file isn't readable or doesn't pin a
    // positive timeout. Pre-AQ3, this was the only ceiling. Now it's
    // a defensive default — every well-formed config carries
    // limits.timeout_seconds.
    private static readonly TimeSpan FallbackExecTimeout = TimeSpan.FromMinutes(15);

    public HeadlessRunner(
        IContainerService containers,
        ContainersDbContext db,
        IRunCancellationRegistry cancellation,
        ITokenIssuer tokens,
        ILogger<HeadlessRunner> logger)
    {
        _containers = containers;
        _db = db;
        _cancellation = cancellation;
        _tokens = tokens;
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
        // AP5's dispatcher already transitioned Pending → Provisioning before
        // calling us, so we only need to advance to Running. SafeTransition
        // is a no-op if the run isn't actually in Provisioning (e.g. a test
        // hands us a Pending run directly), which keeps the runner usable
        // standalone without forcing every caller through the dispatcher.
        SafeTransition(run, RunStatus.Provisioning);
        SafeTransition(run, RunStatus.Running);
        await _db.SaveChangesAsync(ct);

        // AP7 (rivoli-ai/andy-containers#109). Register so the cancel
        // endpoint can signal this exec from a different request scope.
        // Disposal removes the entry and signals waiters — the using
        // statement guarantees that every exit path (success, cancel,
        // throw) wakes RunsController.Cancel's WaitForTerminalAsync.
        using var registration = _cancellation.Register(run.Id, ct);
        var execToken = registration.Token;

        var command = $"andy-cli run --headless --config {ShellEscape(configPath)}";
        var execTimeout = await ResolveExecTimeoutAsync(configPath, execToken);
        ExecResult result;
        try
        {
            _logger.LogInformation(
                "Spawning headless agent for Run {RunId} in container {ContainerId} with config {ConfigPath} (outer timeout {Seconds}s)",
                run.Id, containerId, configPath, (int)execTimeout.TotalSeconds);

            result = await _containers.ExecAsync(containerId, command, execTimeout, execToken);
        }
        catch (OperationCanceledException) when (execToken.IsCancellationRequested)
        {
            // Either the caller cancelled (ct flows into the linked
            // CTS) or the registry's TryCancel fired from the cancel
            // endpoint. Both routes land here and produce the same
            // Cancelled outcome — the runner doesn't distinguish.
            sw.Stop();
            _logger.LogWarning("Headless spawn for Run {RunId} cancelled (caller or registry signal)", run.Id);
            return await TerminateAsync(run, RunEventKind.Cancelled, RunStatus.Cancelled,
                exitCode: null, durationSeconds: sw.Elapsed.TotalSeconds, error: "Cancelled", CancellationToken.None);
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

        // AP10 (rivoli-ai/andy-containers#112). Revoke the run-scoped
        // token outside the persistence try/catch so a DB failure
        // doesn't leak credentials and an issuer failure doesn't lose
        // the terminal write. Best-effort: a missing registration
        // (server restart, double-revoke) is fine; we just want the
        // post-condition "no live run-scoped token" to hold once a
        // run is observed terminal.
        try
        {
            await _tokens.RevokeAsync(run.Id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to revoke run-scoped token for Run {RunId}: {Message}",
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

    // Read the config AP3 just wrote and pull limits.timeout_seconds so
    // our outer ExecAsync ceiling is config-driven (with a small grace
    // period above AQ3's internal deadline). On any read/parse failure
    // we fall back to FallbackExecTimeout — a malformed config is
    // surfaced as an exit-code mismatch from andy-cli rather than as a
    // timeout-resolution crash here.
    private async Task<TimeSpan> ResolveExecTimeoutAsync(string configPath, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(configPath, ct);
            var config = JsonSerializer.Deserialize<HeadlessRunConfig>(json, HeadlessConfigJson.Options);
            var inner = config?.Limits?.TimeoutSeconds ?? 0;
            if (inner > 0)
            {
                return TimeSpan.FromSeconds(inner) + OuterGrace;
            }

            _logger.LogWarning(
                "Config at {Path} has limits.timeout_seconds={Inner}; using fallback {Fallback}s",
                configPath, inner, (int)FallbackExecTimeout.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Could not read limits.timeout_seconds from {Path}; using fallback {Fallback}s",
                configPath, (int)FallbackExecTimeout.TotalSeconds);
        }

        return FallbackExecTimeout;
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
