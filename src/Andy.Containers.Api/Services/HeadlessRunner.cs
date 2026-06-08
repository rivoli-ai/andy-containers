using System.Diagnostics;
using System.Text.Json;
using Andy.Containers.Abstractions;
using Andy.Containers.Configurator;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Messaging;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;
using Andy.Containers.Storage;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

public sealed class HeadlessRunner : IHeadlessRunner
{
    private readonly IContainerService _containers;
    private readonly ContainersDbContext _db;
    private readonly IRunCancellationRegistry _cancellation;
    private readonly ITokenIssuer _tokens;
    private readonly ILogger<HeadlessRunner> _logger;
    // #316. Optional so the existing test surface keeps working.
    // When null, terminal events publish without an OutputArtifacts
    // manifest — pre-#316 wire shape exactly.
    private readonly IOutputArtifactCollector? _artifactCollector;
    // EX.7 (#328). Optional. When null (or when a run declares no
    // inputs) the spawn path is unchanged. When a run DOES declare inputs
    // but no stager is wired, that's a misconfiguration the stager itself
    // surfaces — so we only need null-tolerance here for the no-inputs
    // common case.
    private readonly IInputArtifactStager? _inputStager;
    // F4.1 (rivoli-ai/conductor#1934). Optional mid-run output bus. When
    // null, the spawn path is the pre-F4.1 buffered exec — no live feed,
    // identical terminal behaviour. When wired, each andy-cli stdout/
    // stderr line is published (token-redacted) as it lands, and the
    // run's output stream is marked terminal on every exit path.
    private readonly IRunOutputBus? _outputBus;

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
        ILogger<HeadlessRunner> logger,
        IOutputArtifactCollector? artifactCollector = null,
        IInputArtifactStager? inputStager = null,
        IRunOutputBus? outputBus = null)
    {
        _containers = containers;
        _db = db;
        _cancellation = cancellation;
        _tokens = tokens;
        _logger = logger;
        _artifactCollector = artifactCollector;
        _inputStager = inputStager;
        _outputBus = outputBus;
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

        // EX.7 (rivoli-ai/andy-containers#328). Stage cross-container input
        // artifacts into /workspace/.andy/inputs/ BEFORE spawning andy-cli.
        // A missing/oversized/failed input fails the run start with a clear
        // typed error — the agent must never start against an empty input.
        // No inputs → no-op, spawn path unchanged.
        var stagingFailure = await StageInputsAsync(run, containerId, configPath, execToken);
        if (stagingFailure is not null)
        {
            sw.Stop();
            return await TerminateAsync(run, RunEventKind.Failed, RunStatus.Failed,
                exitCode: null, durationSeconds: sw.Elapsed.TotalSeconds, error: stagingFailure,
                CancellationToken.None);
        }

        // The configurator writes the headless config to a HOST temp dir, but
        // andy-cli runs INSIDE the container where that path doesn't exist.
        // Read it here (it's required — the agent can't run without it) and
        // stage it into the container as the FIRST step of the same exec, so
        // there's a single command whose exit code is andy-cli's.
        string configJson;
        try
        {
            configJson = await File.ReadAllTextAsync(configPath, execToken);
        }
        catch (OperationCanceledException) when (execToken.IsCancellationRequested)
        {
            return await TerminateAsync(run, RunEventKind.Cancelled, RunStatus.Cancelled,
                exitCode: null, durationSeconds: sw.Elapsed.TotalSeconds, error: "Cancelled before spawn",
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return await TerminateAsync(run, RunEventKind.Failed, RunStatus.Failed,
                exitCode: null, durationSeconds: sw.Elapsed.TotalSeconds,
                error: $"Headless config could not be read from host path '{configPath}': {ex.Message}",
                CancellationToken.None);
        }

        // base64 sidesteps every shell-escaping hazard the raw JSON would carry;
        // `base64 -d` is in the coreutils every andy-headless image ships.
        var inContainerConfigPath = $"/tmp/andy-runs/{run.Id}/config.json";
        var stageDir = $"/tmp/andy-runs/{run.Id}";
        var configB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(configJson));
        var command =
            $"mkdir -p {ShellEscape(stageDir)} && "
            + $"printf %s {ShellEscape(configB64)} | base64 -d > {ShellEscape(inContainerConfigPath)} && "
            + $"andy-cli run --headless --config {ShellEscape(inContainerConfigPath)}";
        var execTimeout = await ResolveExecTimeoutAsync(configPath, execToken);

        // F4.1 (#1934). Resolve the literal run-scoped token so the
        // output redactor can mask it from any echoed line. MintAsync is
        // idempotent — for a run that already minted (the common path,
        // via the configurator) this returns the SAME token rather than
        // a new one. A failure here must never block the run, so we fall
        // back to redactor's defensive ANDY_TOKEN=<value> regex.
        var knownToken = await ResolveKnownTokenAsync(run.Id, execToken);

        ExecResult result;
        try
        {
            _logger.LogInformation(
                "Spawning headless agent for Run {RunId} in container {ContainerId} with config {ConfigPath} (outer timeout {Seconds}s)",
                run.Id, containerId, configPath, (int)execTimeout.TotalSeconds);

            if (_outputBus is not null)
            {
                // Mid-run live feed: publish each line as it lands,
                // redacted, tagged with its stream kind.
                result = await _containers.ExecStreamingAsync(
                    containerId, command, execTimeout,
                    chunk => PublishOutputLine(run.Id, chunk, knownToken),
                    execToken);
            }
            else
            {
                result = await _containers.ExecAsync(containerId, command, execTimeout, execToken);
            }
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
        // #316. Collect artifacts BEFORE the persistence try/catch so a
        // probe-time exec failure (logged inside the collector) can't
        // accidentally short-circuit the terminal-event write. The
        // collector runs only when AP5 assigned a container (no
        // container → nothing inside which to scan).
        IReadOnlyList<RunOutputArtifact>? artifacts = null;
        if (_artifactCollector is not null && run.ContainerId is { } containerId)
        {
            try
            {
                var container = await _db.Containers.FindAsync(new object[] { containerId }, ct);
                if (container is not null)
                {
                    artifacts = await _artifactCollector.CollectAsync(container, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller cancelled the terminal write — let the parent
                // catch handle it; don't mask with an artifact-collection
                // log line.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Artifact collection failed for Run {RunId}; emitting terminal event without manifest.",
                    run.Id);
            }
        }

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

            _db.AppendAgentRunEvent(run, kind, exitCode, durationSeconds, artifacts);
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

        // F4.1 (#1934). Mark the mid-run output stream terminal so any
        // attached SSE subscriber drains its buffer and disconnects
        // cleanly. Idempotent; safe even when no line was ever published
        // (e.g. a no-container Failed path) — late subscribers just see
        // an empty, immediately-closed stream. Best-effort: a bus failure
        // must not mask the terminal outcome.
        try
        {
            _outputBus?.Complete(run.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to mark run-output stream terminal for Run {RunId}: {Message}",
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

    // F4.1 (#1934). Best-effort resolution of the literal run-scoped
    // token for redaction. MintAsync is idempotent, so for an already-
    // minted run this hands back the existing token without creating a
    // new one. Any failure (issuer down, swallowed) returns null and the
    // redactor falls back to its ANDY_TOKEN=<value> env-echo regex.
    private async Task<string?> ResolveKnownTokenAsync(Guid runId, CancellationToken ct)
    {
        if (_outputBus is null)
        {
            return null;
        }
        try
        {
            var token = await _tokens.MintAsync(runId, ct);
            return token.Token;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "F4.1: could not resolve run-scoped token for Run {RunId} redaction; relying on env-echo regex.",
                runId);
            return null;
        }
    }

    // F4.1 (#1934). Publish one streamed exec line to the run-output bus,
    // redacted. Never throws — a publish failure must not interrupt the
    // exec drain loop.
    private void PublishOutputLine(Guid runId, ExecOutputChunk chunk, string? knownToken)
    {
        try
        {
            var redacted = RunOutputRedactor.Redact(chunk.Line, knownToken);
            var stream = chunk.Stream == ExecStreamKind.Stderr
                ? RunOutputStream.Stderr
                : RunOutputStream.Stdout;
            _outputBus!.Publish(runId, new RunOutputLine(stream, redacted, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "F4.1: failed to publish run-output line for Run {RunId}: {Message}",
                runId, ex.Message);
        }
    }

    // EX.7 (rivoli-ai/andy-containers#328). Stage the run's declared input
    // artifacts into the container before andy-cli spawns. Returns null on
    // success (including the no-inputs / no-stager common case) or an
    // actionable error string on failure (which the caller turns into a
    // Failed terminal outcome). We source the inputs from the on-disk
    // config the configurator just wrote — they went through the builder's
    // path-traversal validation there.
    private async Task<string?> StageInputsAsync(
        Run run, Guid containerId, string configPath, CancellationToken ct)
    {
        IReadOnlyList<HeadlessInput>? inputs;
        try
        {
            var json = await File.ReadAllTextAsync(configPath, ct);
            var config = JsonSerializer.Deserialize<HeadlessRunConfig>(json, HeadlessConfigJson.Options);
            inputs = config?.Inputs;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The config was unreadable. If the run declared inputs we
            // cannot honour them, so fail rather than start without them;
            // if it declared none, there's nothing to stage and we let the
            // spawn proceed (a malformed config will surface as an andy-cli
            // exit-code mismatch).
            if (run.Inputs is { Count: > 0 })
            {
                _logger.LogError(ex,
                    "EX.7: could not read inputs from config {Path} for Run {RunId}; failing run start.",
                    configPath, run.Id);
                return $"Could not read input config: {ex.Message}";
            }
            return null;
        }

        if (inputs is not { Count: > 0 })
        {
            return null;
        }

        if (_inputStager is null)
        {
            _logger.LogError(
                "EX.7: Run {RunId} declares {Count} input(s) but no input stager is wired; failing run start.",
                run.Id, inputs.Count);
            return "Run declares input artifacts but input staging is not available on this deployment.";
        }

        var container = await _db.Containers.FindAsync(new object[] { containerId }, ct);
        if (container is null)
        {
            _logger.LogError(
                "EX.7: Run {RunId} container {ContainerId} not found; cannot stage inputs.",
                run.Id, containerId);
            return $"Run container {containerId} not found; cannot stage inputs.";
        }

        try
        {
            await _inputStager.StageAsync(container, inputs, ct);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InputStagingException ex)
        {
            _logger.LogError(ex,
                "EX.7: input staging failed for Run {RunId} (docs-ref {DocsRef}, dest '{Dest}', {Failure}): {Message}",
                run.Id, ex.DocsRef, ex.DestRelativePath, ex.Failure, ex.Message);
            return ex.Message;
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
