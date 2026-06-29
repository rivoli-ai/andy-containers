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

    // #2231. Optional per-run model-scoped proxy-token mint. The container's
    // create-time OPENAI_API_KEY is scoped only to the container-default
    // model slugs (Proxy:HeadlessModelSlugs, e.g. "deepseek-v4-flash") — it
    // does NOT know which model THIS run's agent will actually request. When
    // the planner assigns any other model (e.g. "openrouter/qwen3-coder"),
    // andy-models' proxy 403s every completion: "token was not minted for
    // model 'X'. Token-scoped slugs: deepseek-v4-flash." We re-mint a token
    // scoped to the run's ACTUAL model.id (read from the headless config) and
    // override OPENAI_API_KEY for the andy-cli process only. Optional so the
    // existing test surface (and any deployment with no proxy) is unchanged —
    // a null service / null base-url / mint failure leaves the container's
    // own OPENAI_API_KEY in place (pre-#2231 behaviour).
    private readonly IProxyTokenService? _proxyTokenService;
    private readonly Microsoft.Extensions.Configuration.IConfiguration? _configuration;
    // 2026-06-29. (Re)materialises git credentials into the container at run
    // dispatch so a sourceControl.github.pat saved AFTER provisioning still
    // reaches a long-lived task container. Optional so standalone/test
    // constructions of the runner are unaffected.
    private readonly IGitCredentialMaterializer? _gitCredentialMaterializer;

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
        IRunOutputBus? outputBus = null,
        IProxyTokenService? proxyTokenService = null,
        Microsoft.Extensions.Configuration.IConfiguration? configuration = null,
        IGitCredentialMaterializer? gitCredentialMaterializer = null)
    {
        _containers = containers;
        _db = db;
        _cancellation = cancellation;
        _tokens = tokens;
        _logger = logger;
        _artifactCollector = artifactCollector;
        _inputStager = inputStager;
        _outputBus = outputBus;
        _proxyTokenService = proxyTokenService;
        _configuration = configuration;
        _gitCredentialMaterializer = gitCredentialMaterializer;
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

        // 2026-06-29. (Re)materialise git credentials into the container BEFORE
        // spawning the agent — NOT only at provisioning. A task container is
        // one-per-workspace and long-lived; if the operator saved
        // sourceControl.github.pat AFTER it was provisioned, the PAT never
        // reached it and every PR-author run committed locally but failed to
        // push ([PR-VERIFY-002]). The injection is idempotent and best-effort:
        // a failure here must never block the run (the agent may still do work
        // that doesn't need to push).
        if (_gitCredentialMaterializer is not null)
        {
            try
            {
                var container = await _db.Containers.FindAsync(new object[] { containerId }, execToken);
                if (container is not null)
                {
                    await _gitCredentialMaterializer.MaterializeAsync(
                        containerId, container.ContainerUser, container.OwnerId, execToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Git credential (re)materialisation failed for Run {RunId} / container {ContainerId}; git push / gh pr create may fail.",
                    run.Id, containerId);
            }
        }

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

        // #2231. Mint a proxy token scoped to THIS run's actual model and
        // override OPENAI_API_KEY for the andy-cli process. The container's
        // create-time key is scoped only to the container-default slugs, so
        // a run on any other model would 403 at the proxy. Prefixing the
        // andy-cli invocation with `OPENAI_API_KEY='<jwt>'` overrides the
        // env for that one process without touching the container's own env
        // (which other tools / shells in the container still rely on). The
        // assignment is wrapped in the same single command so a failed mint
        // (→ null) just leaves the container default in place. Best-effort:
        // no proxy service / no base url / mint failure → no prefix.
        var modelKeyPrefix = await BuildModelKeyOverrideAsync(run, configJson, containerId, execToken);

        // conductor MSB1003 / andy-tasks#383. The repo is cloned DIRECTLY into
        // the workspace root (GitCloneService: `cp -a {tmp}/. {target}/`,
        // TargetPath defaults to /workspace), so e.g. /workspace/Andy.Cli.sln
        // exists. But the exec endpoint runs `sh -c "<command>"` with NO
        // WorkingDir (the ExecRequest wire shape is {Command, TimeoutSeconds}
        // only), so andy-cli would otherwise run from the image's default
        // WORKDIR — NOT the checkout root. andy-cli is normally invoked from
        // inside the cloned repo and relies on its process CWD being the
        // checkout, so a missing `cd` makes the agent operate against the wrong
        // directory (tooling that resolves paths relative to CWD fails). The
        // companion fix andy-tasks#383 already prefixes the VERIFIER command
        // with `cd '/workspace' && `; this is the same fix for the dispatched
        // coding-agent run. Single chokepoint: we prepend exactly one `cd`,
        // and only the andy-cli sub-command runs under it — the mkdir/stage
        // steps stay CWD-agnostic (they use absolute /tmp paths).
        var workspaceRoot = ResolveWorkspaceRoot(configJson);

        var command =
            $"mkdir -p {ShellEscape(stageDir)} && "
            + $"printf %s {ShellEscape(configB64)} | base64 -d > {ShellEscape(inContainerConfigPath)} && "
            + $"cd {ShellEscape(workspaceRoot)} && "
            + $"{modelKeyPrefix}andy-cli run --headless --config {ShellEscape(inContainerConfigPath)}";
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

        // #2204. A non-zero exec used to surface only the raw stderr (and
        // *nothing* when stderr was empty — e.g. an exit-code-127 "andy-cli:
        // not found" that writes to a swallowed stream), so the user saw a
        // bare "Run <id> ended with Failed." with zero diagnostic content.
        // Enrich the reason with the exit code AND a bounded slice of the
        // container's stderr/stdout so the cause travels all the way to
        // andy-tasks → Conductor. The [AC-HEADLESS-EXIT] code is greppable
        // per the project rule that user-facing errors carry a unique code.
        return await TerminateAsync(run, kind, status,
            exitCode: result.ExitCode, durationSeconds: durationSeconds,
            error: status == RunStatus.Succeeded
                ? null
                : BuildExitFailureReason(result),
            ct);
    }

    // #2204 / #2232. Compose the actionable failure reason carried on
    // Run.Error and out over the run-event wire. Always names the exit code;
    // appends a bounded slice of the container's stderr (falling back to
    // stdout when stderr is empty, so an exit-127 that logs to stdout isn't
    // silently dropped).
    //
    // #2232: the slice is taken from the TAIL, not the HEAD. andy-cli's first
    // output is the ToolRegistry "Registered tool ..." banner; the ACTUAL
    // error (a key-resolution failure, a model 4xx, a stack trace) lands at
    // the END. Truncating from the start surfaced registration noise instead
    // of the cause, so the user saw "Registered tool read_file..." rather
    // than why the run failed. We keep the last ~25 lines / last 500 chars,
    // bounded so a chatty agent can't bloat the event payload.
    private static string BuildExitFailureReason(ExecResult result)
    {
        const int maxChars = 500;
        const int maxLines = 25;
        var stderr = result.StdErr?.Trim();
        var stdout = result.StdOut?.Trim();

        string detail;
        if (!string.IsNullOrEmpty(stderr))
        {
            detail = $" — {Tail(stderr, maxLines, maxChars)}";
        }
        else if (!string.IsNullOrEmpty(stdout))
        {
            // No stderr (common for exit-127 / missing-binary shells, but also
            // for agents that log everything to stdout) — the useful line is
            // the tail of stdout; surface it rather than nothing.
            detail = $" — (no stderr) {Tail(stdout, maxLines, maxChars)}";
        }
        else
        {
            detail = " — no output captured";
        }

        return $"[AC-HEADLESS-EXIT] andy-cli run failed: exit code {result.ExitCode}{detail}";
    }

    // #2232. Return the TAIL of a multi-line output, bounded to the last
    // `maxLines` lines AND the last `maxChars` characters (whichever is
    // tighter). A leading ellipsis marks that earlier output (the banner)
    // was dropped. The real error sits at the end of andy-cli's output, so
    // this is the slice the user actually needs.
    private static string Tail(string value, int maxLines, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        // Take the last N non-empty-trimmed lines first so we don't spend the
        // char budget on trailing blank lines, then apply the char ceiling.
        var lines = value.Replace("\r\n", "\n").Split('\n');
        var startLine = Math.Max(0, lines.Length - maxLines);
        var tail = string.Join("\n", lines, startLine, lines.Length - startLine).Trim();
        var droppedLines = startLine > 0;

        if (tail.Length > maxChars)
        {
            tail = tail[^maxChars..];
            return "..." + tail;
        }

        return droppedLines ? "..." + tail : tail;
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

    // #2231. Build the `OPENAI_API_KEY='<jwt>' ` prefix that overrides the
    // andy-cli process's model credential with a token scoped to the run's
    // ACTUAL model. Returns an empty string (no override) whenever we can't
    // or shouldn't re-mint — so the container's own create-time key stays in
    // effect and behaviour is identical to pre-#2231:
    //   * no IProxyTokenService wired (tests, no-proxy deployments)
    //   * AndyModels:BaseUrl unset (MintForContainerAsync would throw)
    //   * the config carries no usable model.id
    //   * the mint round-trip fails (logged; never aborts the run — the
    //     container default still works for default-model runs)
    // The minted JWT is single-quote-escaped into the command; a shell
    // metacharacter in a JWT is impossible (it's base64url + dots), but we
    // escape defensively so the contract matches ShellEscape everywhere else.
    private async Task<string> BuildModelKeyOverrideAsync(
        Run run, string configJson, Guid containerId, CancellationToken ct)
    {
        if (_proxyTokenService is null)
        {
            return string.Empty;
        }

        var modelId = TryExtractModelId(configJson);
        if (string.IsNullOrWhiteSpace(modelId))
        {
            _logger.LogDebug(
                "#2231: Run {RunId} config has no usable model.id; leaving the container-default OPENAI_API_KEY in place.",
                run.Id);
            return string.Empty;
        }

        // AndyModels:BaseUrl is the same config key AndyModelsOptions binds.
        // Without it MintForContainerAsync throws ProxyTokenException, so skip
        // the round-trip and keep the container default rather than failing the
        // run on a missing-config that the create path already tolerated.
        var modelsBaseUrl = _configuration?[$"{AndyModelsOptions.SectionName}:BaseUrl"];
        if (string.IsNullOrWhiteSpace(modelsBaseUrl))
        {
            _logger.LogDebug(
                "#2231: AndyModels:BaseUrl is not configured; cannot re-mint a model-scoped proxy token for Run {RunId} (model {Model}).",
                run.Id, modelId);
            return string.Empty;
        }

        // The mint's subjectId must match the container owner the create-time
        // mint used (andy-models attributes the token to a subject). Load the
        // container row for its OwnerId; fall back to a stable literal if the
        // row is somehow gone (the run still has the container id).
        var container = await _db.Containers.FindAsync(new object[] { containerId }, ct).ConfigureAwait(false);
        var subjectId = string.IsNullOrWhiteSpace(container?.OwnerId) ? "headless" : container!.OwnerId;

        try
        {
            var minted = await _proxyTokenService
                .MintForContainerAsync(containerId.ToString(), subjectId, new[] { modelId }, ct)
                .ConfigureAwait(false);
            if (minted is null || string.IsNullOrWhiteSpace(minted.Jwt))
            {
                _logger.LogWarning(
                    "#2231: model-scoped proxy-token mint returned null for Run {RunId} (model {Model}); the run will use the container-default key and may 403 if the model differs.",
                    run.Id, modelId);
                return string.Empty;
            }

            _logger.LogInformation(
                "#2231: minted model-scoped proxy token {TokenId} for Run {RunId} (model {Model}); overriding OPENAI_API_KEY for the andy-cli process.",
                minted.TokenId, run.Id, modelId);
            return $"OPENAI_API_KEY={ShellEscape(minted.Jwt)} ";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A mint failure must NOT abort the run — the container's own key
            // still works for default-model runs, and a non-default model that
            // 403s now surfaces (post-#2232) a clear tail error rather than a
            // silently-dropped reason. Logged greppably for triage.
            _logger.LogWarning(ex,
                "#2231: could not mint model-scoped proxy token for Run {RunId} (model {Model}); falling back to the container-default OPENAI_API_KEY. Reason: {Reason}",
                run.Id, modelId, ex.Message);
            return string.Empty;
        }
    }

    // conductor MSB1003 / andy-tasks#383. The directory the dispatched
    // coding-agent run must `cd` into before invoking andy-cli — the repo
    // checkout root. The on-disk headless config carries the authoritative
    // value at `workspace.root` (HeadlessConfigBuilder sets it from the
    // container's ContainerGitRepository.TargetPath, default "/workspace").
    // We honour that so a non-default TargetPath would still land us in the
    // right place; we fall back to the same DefaultWorkspaceRoot constant when
    // the config can't be parsed or doesn't pin a root (e.g. the bare "{}"
    // configs the runner's standalone test surface uses).
    private const string DefaultWorkspaceRoot = "/workspace";

    private static string ResolveWorkspaceRoot(string configJson)
    {
        try
        {
            var config = JsonSerializer.Deserialize<HeadlessRunConfig>(configJson, HeadlessConfigJson.Options);
            var root = config?.Workspace?.Root;
            return string.IsNullOrWhiteSpace(root) ? DefaultWorkspaceRoot : root.Trim();
        }
        catch (Exception)
        {
            return DefaultWorkspaceRoot;
        }
    }

    // #2231. Pull `model.id` out of the headless config JSON. The config is
    // the snake_case wire shape HeadlessConfigBuilder emitted; deserialise
    // with the same options so `model.id` resolves regardless of casing
    // policy. Returns null on any parse failure (the caller treats that as
    // "no override").
    private static string? TryExtractModelId(string configJson)
    {
        try
        {
            var config = JsonSerializer.Deserialize<HeadlessRunConfig>(configJson, HeadlessConfigJson.Options);
            var id = config?.Model?.Id;
            return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        }
        catch (Exception)
        {
            return null;
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

}
