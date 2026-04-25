using System.Text.Json;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
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
    private readonly HeadlessRunner _runner;

    public HeadlessRunnerTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _runner = new HeadlessRunner(_containers.Object, _db, NullLogger<HeadlessRunner>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task StartAsync_ExitZero_TransitionsToSucceeded_WritesFinishedEvent()
    {
        var run = SeedRun();
        SetupExec(run.ContainerId!.Value, exitCode: 0, stdOut: "ok");

        var outcome = await _runner.StartAsync(run, "/tmp/x/config.json");

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

        var outcome = await _runner.StartAsync(run, "/tmp/x/config.json");

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

    [Fact]
    public async Task StartAsync_ExecThrows_TransitionsToFailed_WritesFailedEvent()
    {
        var run = SeedRun();
        _containers
            .Setup(c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("docker daemon unreachable"));

        var outcome = await _runner.StartAsync(run, "/tmp/x/config.json");

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

        var outcome = await _runner.StartAsync(run, "/tmp/x/config.json", CancellationToken.None);

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

        var outcome = await _runner.StartAsync(run, "/tmp/x/config.json", cts.Token);

        outcome.Kind.Should().Be(RunEventKind.Cancelled);
        outcome.Status.Should().Be(RunStatus.Cancelled);
    }

    [Fact]
    public async Task StartAsync_NoContainerId_DoesNotInvokeExec_TransitionsToFailed()
    {
        var run = SeedRunWithoutContainer();

        var outcome = await _runner.StartAsync(run, "/tmp/x/config.json");

        outcome.Kind.Should().Be(RunEventKind.Failed);
        outcome.Status.Should().Be(RunStatus.Failed);
        outcome.Error.Should().Contain("ContainerId");

        _containers.Verify(c => c.ExecAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var entry = await _db.OutboxEntries.SingleAsync();
        entry.Subject.Should().EndWith(".failed");
    }

    [Fact]
    public async Task StartAsync_ConfigPathIsShellEscaped()
    {
        // A config path with a single quote in it would break a naive
        // command interpolation. Verify the runner emits a properly
        // single-quote-escaped argument so /bin/sh -c can parse it.
        var run = SeedRun();
        string? captured = null;
        _containers
            .Setup(c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, TimeSpan, CancellationToken>((_, cmd, _, _) => captured = cmd)
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        await _runner.StartAsync(run, "/tmp/o'malley/config.json");

        captured.Should().NotBeNull();
        captured.Should().Contain("andy-cli run --headless --config");
        captured.Should().Contain("'/tmp/o'\\''malley/config.json'");
    }

    [Fact]
    public async Task StartAsync_OutboxRowCarriesCorrelationIdFromRun()
    {
        var correlation = Guid.NewGuid();
        var run = SeedRun(correlationId: correlation);
        SetupExec(run.ContainerId!.Value, exitCode: 0);

        await _runner.StartAsync(run, "/tmp/x/config.json");

        var entry = await _db.OutboxEntries.SingleAsync();
        entry.CorrelationId.Should().Be(correlation,
            "ADR-0001 root-causation chain must propagate from the Run");
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
