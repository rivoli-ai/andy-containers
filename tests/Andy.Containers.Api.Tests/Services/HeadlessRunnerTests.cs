using System.Text.Json;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Configurator;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// AP6 (rivoli-ai/andy-containers#108). HeadlessRunner spawns andy-cli
// inside the run's container, maps the AQ2 exit-code contract to a
// RunEventKind + RunStatus, and writes the terminal event to the outbox
// keyed on Run.Id (NOT Container.Id — that's the legacy lifecycle path).
public class HeadlessRunnerTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IContainerService> _containers = new();
    private readonly RunCancellationRegistry _cancellation = new();
    private readonly Mock<ITokenIssuer> _tokens = new();
    private readonly HeadlessRunner _runner;

    // The runner now stages the on-disk headless config INTO the container
    // before spawning andy-cli (it reads the host file + writes it via exec),
    // so the config path must point at a real, readable file.
    private readonly string _configPath;

    public HeadlessRunnerTests()
    {
        _configPath = Path.Combine(Path.GetTempPath(), $"headless-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(_configPath, "{}");
        _db = InMemoryDbHelper.CreateContext();
        // AP10 (#112): runner now revokes the run-scoped token on every
        // terminal path. Default to a no-op revoke; AP10-specific tests
        // assert the call, while existing tests don't need to care.
        _tokens
            .Setup(t => t.RevokeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _runner = new HeadlessRunner(
            _containers.Object, _db, _cancellation, _tokens.Object,
            NullLogger<HeadlessRunner>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_configPath); } catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public async Task StartAsync_ExitZero_TransitionsToSucceeded_WritesFinishedEvent()
    {
        var run = SeedRun();
        SetupExec(run.ContainerId!.Value, exitCode: 0, stdOut: "ok");

        var outcome = await _runner.StartAsync(run, _configPath);

        outcome.Kind.Should().Be(RunEventKind.Finished);
        outcome.Status.Should().Be(RunStatus.Succeeded);
        outcome.ExitCode.Should().Be(0);

        var persisted = await _db.Runs.FindAsync(run.Id);
        persisted!.Status.Should().Be(RunStatus.Succeeded);
        persisted.EndedAt.Should().NotBeNull();
        persisted.StartedAt.Should().NotBeNull();
        persisted.ExitCode.Should().Be(0);

        var entry = await _db.OutboxEntries.SingleAsync();
        entry.Subject.Should().Be($"andy.containers.events.run.{run.Id}.finished");
        entry.CorrelationId.Should().Be(run.CorrelationId);

        using var doc = JsonDocument.Parse(entry.PayloadJson);
        doc.RootElement.GetProperty("run_id").GetString().Should().Be(run.Id.ToString());
        doc.RootElement.GetProperty("status").GetString().Should().Be("Succeeded");
        doc.RootElement.GetProperty("exit_code").GetInt32().Should().Be(0);
    }

    [Theory]
    [InlineData(1, RunEventKind.Failed, RunStatus.Failed, "failed")]
    [InlineData(2, RunEventKind.Failed, RunStatus.Failed, "failed")]
    [InlineData(3, RunEventKind.Cancelled, RunStatus.Cancelled, "cancelled")]
    [InlineData(4, RunEventKind.Timeout, RunStatus.Timeout, "timeout")]
    [InlineData(5, RunEventKind.Failed, RunStatus.Failed, "failed")]
    public async Task StartAsync_ExitCode_MapsToExpectedKindAndStatus(
        int exitCode, RunEventKind kind, RunStatus status, string subjectSuffix)
    {
        var run = SeedRun();
        SetupExec(run.ContainerId!.Value, exitCode: exitCode, stdErr: "boom");

        var outcome = await _runner.StartAsync(run, _configPath);

        outcome.Kind.Should().Be(kind);
        outcome.Status.Should().Be(status);
        outcome.ExitCode.Should().Be(exitCode);

        var persisted = await _db.Runs.FindAsync(run.Id);
        persisted!.Status.Should().Be(status);

        var entry = await _db.OutboxEntries.SingleAsync();
        entry.Subject.Should().EndWith($".{subjectSuffix}");
        entry.Subject.Should().Contain(run.Id.ToString(),
            "AP6 must key on Run.Id, not Container.Id");
    }

    // conductor#2204. A non-zero andy-cli exit must surface an ACTIONABLE
    // reason — the exit code AND the container's stderr tail — both on
    // Run.Error AND out over the run-event wire (the payload the andy-tasks
    // consumer reads). Before the fix the reason was the bare stderr (and
    // null when stderr was empty), and RunEventPayload didn't even carry an
    // Error field, so the user saw only "Run <id> ended with Failed."
    [Fact]
    public async Task StartAsync_ExitNonZeroWithStderr_SurfacesExitCodeAndStderr_OnRunErrorAndWire()
    {
        var run = SeedRun();
        // The canonical "andy-cli not found" shape: exit 127 + a stderr line.
        SetupExec(run.ContainerId!.Value, exitCode: 127,
            stdErr: "/bin/sh: andy-cli: command not found");

        var outcome = await _runner.StartAsync(run, _configPath);

        outcome.Status.Should().Be(RunStatus.Failed);
        outcome.ExitCode.Should().Be(127);

        // Outcome reason carries the exit code + stderr + the greppable code.
        outcome.Error.Should().Contain("127");
        outcome.Error.Should().Contain("command not found");
        outcome.Error.Should().Contain("[AC-HEADLESS-EXIT]");

        // Persisted on the Run row.
        var persisted = await _db.Runs.FindAsync(run.Id);
        persisted!.Error.Should().Contain("127");
        persisted.Error.Should().Contain("command not found");

        // And — the actual gap this fixes — it travels on the wire payload
        // the andy-tasks consumer deserialises (RunEventPayload.error).
        var entry = await _db.OutboxEntries.SingleAsync();
        entry.Subject.Should().EndWith(".failed");
        using var doc = JsonDocument.Parse(entry.PayloadJson);
        doc.RootElement.GetProperty("exit_code").GetInt32().Should().Be(127);
        var wireError = doc.RootElement.GetProperty("error").GetString();
        wireError.Should().Contain("127");
        wireError.Should().Contain("command not found");
        wireError.Should().Contain("[AC-HEADLESS-EXIT]");
    }

    // conductor#2204. Even when stderr is empty (an exit-127 shell that logs
    // to stdout, or no output at all), the reason must still name the exit
    // code and fall back to the stdout tail rather than collapsing to null.
    [Fact]
    public async Task StartAsync_ExitNonZeroNoStderr_StillSurfacesExitCodeAndStdoutTail()
    {
        var run = SeedRun();
        SetupExec(run.ContainerId!.Value, exitCode: 127,
            stdOut: "andy-cli: No such file or directory", stdErr: null);

        var outcome = await _runner.StartAsync(run, _configPath);

        outcome.Status.Should().Be(RunStatus.Failed);
        outcome.Error.Should().NotBeNull();
        outcome.Error.Should().Contain("127");
        outcome.Error.Should().Contain("No such file or directory");

        var entry = await _db.OutboxEntries.SingleAsync();
        using var doc = JsonDocument.Parse(entry.PayloadJson);
        doc.RootElement.GetProperty("error").GetString()
            .Should().Contain("No such file or directory");
    }

    // conductor#2204. The stderr tail is bounded so a chatty agent can't
    // bloat the run-event payload.
    [Fact]
    public async Task StartAsync_ExitNonZeroWithHugeStderr_TruncatesReason()
    {
        var run = SeedRun();
        var huge = new string('x', 5000);
        SetupExec(run.ContainerId!.Value, exitCode: 1, stdErr: huge);

        var outcome = await _runner.StartAsync(run, _configPath);

        outcome.Error.Should().NotBeNull();
        // Bounded: exit-code prefix + the greppable code + 500-char tail +
        // ellipsis — comfortably under 700 chars even though stderr was 5000.
        outcome.Error!.Length.Should().BeLessThan(700);
        outcome.Error.Should().EndWith("...");
    }

    [Fact]
    public async Task StartAsync_ExecThrows_TransitionsToFailed_WritesFailedEvent()
    {
        var run = SeedRun();
        _containers
            .Setup(c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("docker daemon unreachable"));

        var outcome = await _runner.StartAsync(run, _configPath);

        outcome.Kind.Should().Be(RunEventKind.Failed);
        outcome.Status.Should().Be(RunStatus.Failed);
        outcome.ExitCode.Should().BeNull();
        outcome.Error.Should().Contain("docker daemon");

        var persisted = await _db.Runs.FindAsync(run.Id);
        persisted!.Status.Should().Be(RunStatus.Failed);
        persisted.Error.Should().Contain("docker daemon");

        var entry = await _db.OutboxEntries.SingleAsync();
        entry.Subject.Should().EndWith(".failed");
    }

    [Fact]
    public async Task StartAsync_ExecAsyncTimeoutThrows_MapsToTimeout()
    {
        // ExecAsync's internal timeout surfaces as OperationCanceledException
        // even though the caller's token never fired. Distinct from caller
        // cancellation (Cancelled) — this is the watchdog path.
        var run = SeedRun();
        _containers
            .Setup(c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("exec timeout"));

        var outcome = await _runner.StartAsync(run, _configPath, CancellationToken.None);

        outcome.Kind.Should().Be(RunEventKind.Timeout);
        outcome.Status.Should().Be(RunStatus.Timeout);

        var entry = await _db.OutboxEntries.SingleAsync();
        entry.Subject.Should().EndWith(".timeout");
    }

    [Fact]
    public async Task StartAsync_CallerCancels_MapsToCancelled()
    {
        var run = SeedRun();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _containers
            .Setup(c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var outcome = await _runner.StartAsync(run, _configPath, cts.Token);

        outcome.Kind.Should().Be(RunEventKind.Cancelled);
        outcome.Status.Should().Be(RunStatus.Cancelled);
    }

    [Fact]
    public async Task StartAsync_RegistryCancelDuringExec_TerminatesAsCancelled()
    {
        // AP7 (rivoli-ai/andy-containers#109). The cancel endpoint signals
        // the runner via the registry; the linked CTS fires inside ExecAsync
        // and the runner's catch-OCE path should produce a Cancelled
        // outcome + outbox event regardless of how the spawn was kicked
        // off (caller token vs. registry signal).
        var run = SeedRun();
        var spawnedTcs = new TaskCompletionSource<bool>();
        _containers
            .Setup(c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, TimeSpan, CancellationToken>(async (_, _, _, token) =>
            {
                spawnedTcs.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("delay should have thrown");
            });

        var startTask = _runner.StartAsync(run, _configPath);

        // Wait for ExecAsync to be in flight before signalling — the
        // runner's registration only exists between the SaveChanges of
        // the Running transition and the using-disposal at end of method.
        await spawnedTcs.Task;

        _cancellation.TryCancel(run.Id).Should().BeTrue(
            "the runner registers itself before invoking ExecAsync");

        var outcome = await startTask;

        outcome.Kind.Should().Be(RunEventKind.Cancelled);
        outcome.Status.Should().Be(RunStatus.Cancelled);

        var persisted = await _db.Runs.FindAsync(run.Id);
        persisted!.Status.Should().Be(RunStatus.Cancelled);
        persisted.EndedAt.Should().NotBeNull();

        var entry = await _db.OutboxEntries.SingleAsync();
        entry.Subject.Should().EndWith(".cancelled");
    }

    [Fact]
    public async Task StartAsync_RegistryEntryRemovedAfterTerminal()
    {
        // The registration is `using`-scoped so disposal happens whether
        // the run succeeds, fails, or is cancelled. After StartAsync
        // returns, TryCancel must report no active registration so a
        // late cancel POST falls through to the controller's no-runner
        // path (flip + emit) instead of waiting forever.
        var run = SeedRun();
        SetupExec(run.ContainerId!.Value, exitCode: 0);

        await _runner.StartAsync(run, _configPath);

        _cancellation.TryCancel(run.Id).Should().BeFalse(
            "registration must be removed after the runner terminates");
    }

    [Fact]
    public async Task StartAsync_NoContainerId_DoesNotInvokeExec_TransitionsToFailed()
    {
        var run = SeedRunWithoutContainer();

        var outcome = await _runner.StartAsync(run, _configPath);

        outcome.Kind.Should().Be(RunEventKind.Failed);
        outcome.Status.Should().Be(RunStatus.Failed);
        outcome.Error.Should().Contain("ContainerId");

        _containers.Verify(c => c.ExecAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var entry = await _db.OutboxEntries.SingleAsync();
        entry.Subject.Should().EndWith(".failed");
    }

    // AP10 (#112). Every terminal path must revoke the run-scoped token.
    // Cover all four — no-container Failed, exit-code Succeeded/Failed,
    // exec-throw Failed, registry-cancel Cancelled, watchdog Timeout —
    // so a future regression that adds a new exit path can't slip a leak.

    [Theory]
    [InlineData(0)]   // Succeeded
    [InlineData(1)]   // Failed
    [InlineData(3)]   // Cancelled
    [InlineData(4)]   // Timeout
    public async Task StartAsync_ExitCodeTerminalPath_RevokesToken(int exitCode)
    {
        var run = SeedRun();
        SetupExec(run.ContainerId!.Value, exitCode);

        await _runner.StartAsync(run, _configPath);

        _tokens.Verify(t => t.RevokeAsync(run.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartAsync_NoContainerId_StillRevokesToken()
    {
        // Configurator already minted the token before AP5 failed to
        // assign a container; we still need to revoke so the token
        // doesn't outlive the run row.
        var run = SeedRunWithoutContainer();

        await _runner.StartAsync(run, _configPath);

        _tokens.Verify(t => t.RevokeAsync(run.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartAsync_RevokeFailureDoesNotMaskOutcome()
    {
        // A revoke that throws must not lose the terminal outcome.
        var run = SeedRun();
        SetupExec(run.ContainerId!.Value, exitCode: 0);
        _tokens
            .Setup(t => t.RevokeAsync(run.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("issuer down"));

        var outcome = await _runner.StartAsync(run, _configPath);

        outcome.Status.Should().Be(RunStatus.Succeeded,
            "issuer failure must be logged, not propagated as a run failure");
    }

    [Fact]
    public async Task StartAsync_StagesConfigIntoContainer_AndShellEscapesInContainerPath()
    {
        // andy-cli runs INSIDE the container where the host config path doesn't
        // exist, so the runner stages the config there (base64-decoded into a
        // fixed in-container path) as the first step of the same exec, then
        // points andy-cli at that path — single-quote-wrapped so /bin/sh -c
        // parses it.
        var run = SeedRun();
        string? captured = null;
        _containers
            .Setup(c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, TimeSpan, CancellationToken>((_, cmd, _, _) => captured = cmd)
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        await _runner.StartAsync(run, _configPath);

        captured.Should().NotBeNull();
        // Stage step: base64-decode the config into the in-container path.
        captured.Should().Contain($"base64 -d > '/tmp/andy-runs/{run.Id}/config.json'");
        // Run step: andy-cli against the single-quote-escaped in-container path.
        captured.Should().Contain($"andy-cli run --headless --config '/tmp/andy-runs/{run.Id}/config.json'");
    }

    [Fact]
    public async Task StartAsync_ReadsLimitsTimeoutSecondsFromConfig_AddsGrace()
    {
        // AP6's outer ExecAsync ceiling is config-driven: limits.timeout_seconds
        // + a 30s grace. The grace gives AQ3 a head start so its own internal
        // CTS fires first (mapping to exit code 4 → RunEventKind.Timeout)
        // before our outer watchdog kicks in. Without it, both fire
        // simultaneously and we lose the ability to distinguish a self-timeout
        // from a hung process.
        var run = SeedRun();
        var configPath = WriteRealConfig(timeoutSeconds: 120);

        TimeSpan? capturedTimeout = null;
        _containers
            .Setup(c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, TimeSpan, CancellationToken>((_, _, t, _) => capturedTimeout = t)
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        await _runner.StartAsync(run, configPath);

        capturedTimeout.Should().Be(TimeSpan.FromSeconds(150),
            "120s inner + 30s grace should land at ExecAsync.");
    }

    [Fact]
    public async Task StartAsync_ConfigUnreadable_FailsRunWithoutSpawningAndyCli()
    {
        // The headless config is staged INTO the container (andy-cli reads it
        // there), so a config that can't be read on the host is a hard failure
        // — the agent must never start without its config. (Previously the
        // runner fell back to a default timeout and spawned anyway; that masked
        // a misconfigured run as a hung one.)
        var run = SeedRun();
        var missingPath = Path.Combine(Path.GetTempPath(), $"never-existed-{Guid.NewGuid():N}.json");

        string? spawnCommand = null;
        _containers
            .Setup(c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, TimeSpan, CancellationToken>((_, cmd, _, _) => spawnCommand = cmd)
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        var outcome = await _runner.StartAsync(run, missingPath);

        outcome.Kind.Should().Be(RunEventKind.Failed);
        outcome.Status.Should().Be(RunStatus.Failed);
        outcome.Error.Should().Contain("config could not be read");
        spawnCommand.Should().BeNull("andy-cli must not spawn when its config is unreadable");
    }

    [Fact]
    public async Task StartAsync_ConfigTimeoutZero_FallsBackToFifteenMinuteDefault()
    {
        // A schema-valid config can still carry a non-positive timeout;
        // fall back rather than calling ExecAsync with a zero / negative
        // ceiling that the underlying provider would interpret unpredictably.
        var run = SeedRun();
        var configPath = WriteRealConfig(timeoutSeconds: 0);

        TimeSpan? capturedTimeout = null;
        _containers
            .Setup(c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, TimeSpan, CancellationToken>((_, _, t, _) => capturedTimeout = t)
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        await _runner.StartAsync(run, configPath);

        capturedTimeout.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task StartAsync_OutboxRowCarriesCorrelationIdFromRun()
    {
        var correlation = Guid.NewGuid();
        var run = SeedRun(correlationId: correlation);
        SetupExec(run.ContainerId!.Value, exitCode: 0);

        await _runner.StartAsync(run, _configPath);

        var entry = await _db.OutboxEntries.SingleAsync();
        entry.CorrelationId.Should().Be(correlation,
            "ADR-0001 root-causation chain must propagate from the Run");
    }

    // ----- rivoli-ai/andy-containers#316 OutputArtifacts wiring -----

    [Fact]
    public async Task StartAsync_WithArtifactCollector_PersistsAndPublishesArtifacts()
    {
        // End-to-end through the runner's terminal path: collector is
        // invoked, results land on Run.OutputArtifacts AND on the
        // outbox payload's output_artifacts array.
        var artifacts = new List<RunOutputArtifact>
        {
            new("report.pdf", "report.pdf", 100, new string('a', 64), "application/pdf"),
            new("data.json", "sub/data.json", 50, new string('b', 64), "application/json"),
        };
        var collector = new Mock<IOutputArtifactCollector>();
        collector
            .Setup(c => c.CollectAsync(It.IsAny<Container>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(artifacts);

        // Seed a real container row so the runner can FindAsync it.
        var run = SeedRun();
        SeedContainer(run.ContainerId!.Value);

        var runner = new HeadlessRunner(
            _containers.Object, _db, _cancellation, _tokens.Object,
            NullLogger<HeadlessRunner>.Instance, collector.Object);
        SetupExec(run.ContainerId.Value, exitCode: 0);

        await runner.StartAsync(run, _configPath);

        var persisted = await _db.Runs.FindAsync(run.Id);
        persisted!.OutputArtifacts.Should().HaveCount(2);
        persisted.OutputArtifacts!.Should().Contain(a => a.RelativePath == "report.pdf");

        var entry = await _db.OutboxEntries.SingleAsync();
        using var doc = JsonDocument.Parse(entry.PayloadJson);
        var arr = doc.RootElement.GetProperty("output_artifacts");
        arr.GetArrayLength().Should().Be(2);
        arr[0].GetProperty("relative_path").GetString().Should().Be("report.pdf");

        collector.Verify(
            c => c.CollectAsync(It.Is<Container>(ct => ct.Id == run.ContainerId.Value),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task StartAsync_CollectorThrows_TerminalEventStillWritten()
    {
        // Collector failures must NEVER block the terminal-event write.
        // The runner logs and proceeds with a null manifest (no
        // output_artifacts field on the payload).
        var collector = new Mock<IOutputArtifactCollector>();
        collector
            .Setup(c => c.CollectAsync(It.IsAny<Container>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("probe exec died"));

        var run = SeedRun();
        SeedContainer(run.ContainerId!.Value);

        var runner = new HeadlessRunner(
            _containers.Object, _db, _cancellation, _tokens.Object,
            NullLogger<HeadlessRunner>.Instance, collector.Object);
        SetupExec(run.ContainerId.Value, exitCode: 0);

        var outcome = await runner.StartAsync(run, _configPath);

        outcome.Status.Should().Be(RunStatus.Succeeded,
            "collector failures must not corrupt the run outcome");

        var entry = await _db.OutboxEntries.SingleAsync();
        entry.Subject.Should().EndWith(".finished");

        using var doc = JsonDocument.Parse(entry.PayloadJson);
        doc.RootElement.TryGetProperty("output_artifacts", out _).Should().BeFalse(
            "a failed probe omits the field so v1 consumers stay happy");

        var persisted = await _db.Runs.FindAsync(run.Id);
        persisted!.OutputArtifacts.Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_NoCollector_BehavesLikePreFix()
    {
        // Pre-#316 wire shape: no collector configured → payload
        // omits output_artifacts entirely, Run.OutputArtifacts stays null.
        var run = SeedRun();
        SetupExec(run.ContainerId!.Value, exitCode: 0);

        // Default _runner (constructed in the class ctor) has no
        // collector. Exercise it directly.
        await _runner.StartAsync(run, _configPath);

        var entry = await _db.OutboxEntries.SingleAsync();
        using var doc = JsonDocument.Parse(entry.PayloadJson);
        doc.RootElement.TryGetProperty("output_artifacts", out _).Should().BeFalse();

        var persisted = await _db.Runs.FindAsync(run.Id);
        persisted!.OutputArtifacts.Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_NoContainerId_DoesNotInvokeCollector()
    {
        // AP5 never assigned a container → nothing to scan, no exec
        // round-trip to spend.
        var collector = new Mock<IOutputArtifactCollector>();
        var run = SeedRunWithoutContainer();

        var runner = new HeadlessRunner(
            _containers.Object, _db, _cancellation, _tokens.Object,
            NullLogger<HeadlessRunner>.Instance, collector.Object);

        await runner.StartAsync(run, _configPath);

        collector.Verify(
            c => c.CollectAsync(It.IsAny<Container>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void SeedContainer(Guid containerId)
    {
        // Minimal Container row for the runner's collector-path FindAsync.
        // The Container Status doesn't matter for these tests since the
        // mocked collector ignores it; we still seed it as Running for
        // realism.
        _db.Containers.Add(new Container
        {
            Id = containerId,
            Name = $"c-{containerId:N}",
            OwnerId = "u",
            ExternalId = "ext-" + containerId.ToString("N")[..8],
            Status = ContainerStatus.Running,
        });
        _db.SaveChanges();
    }

    // Writes a real headless config to a temp file so the runner can
    // parse limits.timeout_seconds. Other fields are placeholders — only
    // the limits block is exercised by these tests, but a complete object
    // round-trips through HeadlessConfigJson.Options without touching the
    // schema validator (validation is andy-cli's job).
    private static string WriteRealConfig(int timeoutSeconds, int maxIterations = 4)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ap6-test-{Guid.NewGuid():N}.json");
        var config = new Andy.Containers.Configurator.HeadlessRunConfig
        {
            RunId = Guid.NewGuid(),
            Limits = new Andy.Containers.Configurator.HeadlessLimits
            {
                MaxIterations = maxIterations,
                TimeoutSeconds = timeoutSeconds,
            },
        };
        File.WriteAllText(path, Andy.Containers.Configurator.HeadlessConfigJson.Serialize(config));
        return path;
    }

    private void SetupExec(Guid containerId, int exitCode, string? stdOut = null, string? stdErr = null)
    {
        _containers
            .Setup(c => c.ExecAsync(
                containerId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult
            {
                ExitCode = exitCode,
                StdOut = stdOut,
                StdErr = stdErr,
            });
    }

    // ----- EX.7 (rivoli-ai/andy-containers#328) input staging -----

    [Fact]
    public async Task StartAsync_RunWithInputs_StagesBeforeSpawningAndyCli()
    {
        var containerId = Guid.NewGuid();
        var run = SeedRun(containerId);
        SeedContainer(containerId);
        SetupExec(containerId, exitCode: 0, stdOut: "ok");

        var docId = Guid.NewGuid();
        var configPath = WriteRealConfigWithInputs(
            new HeadlessInput { DocsRef = docId, DestRelativePath = "prior/report.json" });

        var stager = new Mock<IInputArtifactStager>();
        var runner = new HeadlessRunner(
            _containers.Object, _db, _cancellation, _tokens.Object,
            NullLogger<HeadlessRunner>.Instance, artifactCollector: null, inputStager: stager.Object);

        var outcome = await runner.StartAsync(run, configPath);

        outcome.Status.Should().Be(RunStatus.Succeeded);
        // The stager was invoked with the config's declared input.
        stager.Verify(s => s.StageAsync(
            It.Is<Container>(c => c.Id == containerId),
            It.Is<IReadOnlyList<HeadlessInput>>(i => i.Count == 1 && i[0].DocsRef == docId),
            It.IsAny<CancellationToken>()),
            Times.Once);
        File.Delete(configPath);
    }

    [Fact]
    public async Task StartAsync_StagingFails_RunFailsWithoutSpawningAndyCli()
    {
        var containerId = Guid.NewGuid();
        var run = SeedRun(containerId);
        SeedContainer(containerId);

        string? spawnCommand = null;
        _containers
            .Setup(c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, TimeSpan, CancellationToken>((_, cmd, _, _) => spawnCommand = cmd)
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        var docId = Guid.NewGuid();
        var configPath = WriteRealConfigWithInputs(
            new HeadlessInput { DocsRef = docId, DestRelativePath = "a.txt" });

        var stager = new Mock<IInputArtifactStager>();
        stager.Setup(s => s.StageAsync(
                It.IsAny<Container>(), It.IsAny<IReadOnlyList<HeadlessInput>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InputStagingException(
                docId, "a.txt", InputStagingFailure.NotFound, "EX.7: cannot stage input 'a.txt': document not found."));

        var runner = new HeadlessRunner(
            _containers.Object, _db, _cancellation, _tokens.Object,
            NullLogger<HeadlessRunner>.Instance, artifactCollector: null, inputStager: stager.Object);

        var outcome = await runner.StartAsync(run, configPath);

        outcome.Status.Should().Be(RunStatus.Failed);
        outcome.Error.Should().Contain("a.txt");
        spawnCommand.Should().BeNull("staging failure must short-circuit before andy-cli spawns");

        var persisted = await _db.Runs.FindAsync(run.Id);
        persisted!.Status.Should().Be(RunStatus.Failed);
        File.Delete(configPath);
    }

    [Fact]
    public async Task StartAsync_NoInputs_DoesNotInvokeStager()
    {
        var run = SeedRun();
        SetupExec(run.ContainerId!.Value, exitCode: 0);
        var configPath = WriteRealConfig(timeoutSeconds: 60); // no inputs

        var stager = new Mock<IInputArtifactStager>();
        var runner = new HeadlessRunner(
            _containers.Object, _db, _cancellation, _tokens.Object,
            NullLogger<HeadlessRunner>.Instance, artifactCollector: null, inputStager: stager.Object);

        var outcome = await runner.StartAsync(run, configPath);

        outcome.Status.Should().Be(RunStatus.Succeeded);
        stager.Verify(s => s.StageAsync(
            It.IsAny<Container>(), It.IsAny<IReadOnlyList<HeadlessInput>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        File.Delete(configPath);
    }

    private static string WriteRealConfigWithInputs(params HeadlessInput[] inputs)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ex7-test-{Guid.NewGuid():N}.json");
        var config = new HeadlessRunConfig
        {
            RunId = Guid.NewGuid(),
            Limits = new HeadlessLimits { MaxIterations = 4, TimeoutSeconds = 60 },
            Inputs = inputs,
        };
        File.WriteAllText(path, HeadlessConfigJson.Serialize(config));
        return path;
    }

    private Run SeedRun(Guid? containerId = null, Guid? correlationId = null)
        => SeedRunCore(containerId ?? Guid.NewGuid(), correlationId);

    private Run SeedRunWithoutContainer()
        => SeedRunCore(null, null);

    private Run SeedRunCore(Guid? containerId, Guid? correlationId)
    {
        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = "triage-agent",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            ContainerId = containerId,
            CorrelationId = correlationId ?? Guid.NewGuid(),
            Status = RunStatus.Pending,
        };
        _db.Runs.Add(run);
        _db.SaveChanges();
        return run;
    }
}
