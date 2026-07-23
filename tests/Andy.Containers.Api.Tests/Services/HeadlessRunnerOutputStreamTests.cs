using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Configurator;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Runs.Events;
using Andy.Containers.Messaging;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;
using Andy.Containers.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// F4.1 (rivoli-ai/conductor#1934). The runner-to-bus wiring: when an
// IRunOutputBus is supplied, HeadlessRunner drives the container's
// STREAMING exec and republishes each line onto the bus (redacted,
// stream-kind tagged), then marks the run terminal so attached SSE
// subscribers drain + close. This is the integration layer the spec
// asks for: a fake IContainerService.ExecStreamingAsync that emits N
// lines then exits, asserting the full chain
// (runner → bus → subscriber) yields all N mid-run lines + terminates.
public class HeadlessRunnerOutputStreamTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IContainerService> _containers = new();
    private readonly RunCancellationRegistry _cancellation = new();
    private readonly Mock<ITokenIssuer> _tokens = new();
    private readonly InMemoryRunOutputBus _bus = new();
    private readonly Mock<IMessageBus> _messageBus = new();
    private readonly HeadlessRunner _runner;
    private readonly string _configPath;

    public HeadlessRunnerOutputStreamTests()
    {
        _configPath = Path.Combine(
            Path.GetTempPath(),
            $"andy-containers-output-stream-{Guid.NewGuid():N}.json");
        File.WriteAllText(_configPath, "{}");

        _db = InMemoryDbHelper.CreateContext();
        _tokens
            .Setup(t => t.RevokeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        // Bus path resolves the run-scoped token for redaction.
        _tokens
            .Setup(t => t.MintAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunToken("sk-run-secret-0123456789abcdef", DateTimeOffset.UtcNow.AddHours(1)));
        _messageBus
            .Setup(b => b.PublishAsync(
                It.IsAny<string>(),
                It.IsAny<object>(),
                It.IsAny<MessageHeaders>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _runner = new HeadlessRunner(
            _containers.Object, _db, _cancellation, _tokens.Object,
            NullLogger<HeadlessRunner>.Instance, artifactCollector: null,
            inputStager: null, outputBus: _bus, messageBus: _messageBus.Object);
    }

    public void Dispose()
    {
        _db.Dispose();
        _bus.Dispose();
        File.Delete(_configPath);
    }

    [Fact]
    public async Task StartAsync_StreamsEachExecLineToTheBus_ThenMarksTerminal()
    {
        var run = SeedRun();
        SetupStreamingExec(run.ContainerId!.Value, exitCode: 0, lines:
        [
            (ExecStreamKind.Stdout, "Iteration 1/4"),
            (ExecStreamKind.Stdout, "Iteration 2/4"),
            (ExecStreamKind.Stderr, "transient retry"),
            (ExecStreamKind.Stdout, "done"),
        ]);

        // Subscribe BEFORE running is racy in a unit test; instead drive
        // the run, then drain the bus (which replays the buffered lines
        // and closes because the run is terminal).
        var outcome = await _runner.StartAsync(run, _configPath);
        outcome.Status.Should().Be(RunStatus.Succeeded);

        var lines = await DrainAsync(run.Id);

        lines.Should().HaveCount(4, "every mid-run exec line reaches the bus.");
        lines.Select(e => e.SequenceNumber).Should().Equal(1, 2, 3, 4);
        lines.Select(e => e.Line.Line).Should().Equal(
            "Iteration 1/4", "Iteration 2/4", "transient retry", "done");
        lines[2].Line.Stream.Should().Be(RunOutputStream.Stderr,
            "stderr lines stay distinguishable from stdout on the bus.");
    }

    [Fact]
    public async Task StartAsync_AttemptOutput_PublishesCorrelatedNatsOutputSubjects()
    {
        var run = SeedRun();
        run.AttemptId = Guid.NewGuid();
        await _db.SaveChangesAsync();
        SetupStreamingExec(run.ContainerId!.Value, exitCode: 0, lines:
        [
            (ExecStreamKind.Stdout, "working"),
            (ExecStreamKind.Stderr, "retrying"),
        ]);
        var published = new List<RunEventPayload>();
        _messageBus
            .Setup(b => b.PublishAsync(
                $"andy.containers.events.run.{run.Id}.output",
                It.IsAny<object>(),
                It.IsAny<MessageHeaders>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, object, MessageHeaders, CancellationToken>(
                (_, payload, _, _) => published.Add((RunEventPayload)payload))
            .Returns(Task.CompletedTask);

        await _runner.StartAsync(run, _configPath);

        published.Should().HaveCount(2);
        published.Select(p => p.AttemptId).Should().OnlyContain(id => id == run.AttemptId);
        published.Select(p => p.Sequence).Should().BeInAscendingOrder();
        published.Select(p => p.Output!.Line).Should().Equal("working", "retrying");
    }

    [Fact]
    public async Task StartAsync_RedactsRunScopedTokenFromEchoedOutput()
    {
        var run = SeedRun();
        // The agent dumps its env mid-run — the bearer must not survive
        // onto the bus.
        SetupStreamingExec(run.ContainerId!.Value, exitCode: 0, lines:
        [
            (ExecStreamKind.Stdout, "ANDY_TOKEN=sk-run-secret-0123456789abcdef"),
            (ExecStreamKind.Stdout, "curl -H 'Authorization: Bearer sk-run-secret-0123456789abcdef'"),
        ]);

        await _runner.StartAsync(run, _configPath);

        var lines = await DrainAsync(run.Id);

        lines.Should().NotContain(e => e.Line.Line.Contains("sk-run-secret"),
            "the run-scoped bearer is redacted before it reaches the live stream.");
        lines.Should().Contain(e => e.Line.Line.Contains(RunOutputRedactor.Placeholder));
    }

    [Fact]
    public async Task StartAsync_EmptyOutput_StillMarksTerminal()
    {
        var run = SeedRun();
        SetupStreamingExec(run.ContainerId!.Value, exitCode: 0, lines: []);

        await _runner.StartAsync(run, _configPath);

        // An immediate drain must close (terminal marker present) with no
        // frames — never hang.
        var lines = await DrainAsync(run.Id);
        lines.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_LiveSubscriberSeesLinesBeforeTerminal()
    {
        // Prove the lines are MID-RUN, not just replayed at the end: hold
        // the exec open until a subscriber has consumed the first batch,
        // then let it finish.
        var run = SeedRun();
        var firstBatchPublished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExec = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _containers
            .Setup(c => c.ExecStreamingAsync(
                run.ContainerId!.Value, It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<Action<ExecOutputChunk>>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, TimeSpan, Action<ExecOutputChunk>, CancellationToken>(
                async (_, _, _, onLine, _) =>
                {
                    onLine(new ExecOutputChunk(ExecStreamKind.Stdout, "live-1"));
                    onLine(new ExecOutputChunk(ExecStreamKind.Stdout, "live-2"));
                    firstBatchPublished.TrySetResult(true);
                    await releaseExec.Task;
                    onLine(new ExecOutputChunk(ExecStreamKind.Stdout, "live-3"));
                    return new ExecResult { ExitCode = 0 };
                });

        var startTask = _runner.StartAsync(run, _configPath);

        // Subscribe live and read the first two lines while exec is still
        // running (releaseExec not yet completed → run not terminal).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var seen = new List<RunOutputEnvelope>();
        var readTask = Task.Run(async () =>
        {
            await firstBatchPublished.Task;
            await foreach (var env in _bus.SubscribeAsync(run.Id, null, cts.Token))
            {
                seen.Add(env);
                if (seen.Count >= 2)
                {
                    break; // got the mid-run batch — release exec to finish
                }
            }
        }, cts.Token);

        await firstBatchPublished.Task;
        await readTask;

        seen.Should().HaveCountGreaterThanOrEqualTo(2,
            "the subscriber saw output WHILE the run was still in flight, before the terminal event.");
        seen.Take(2).Select(e => e.Line.Line).Should().Equal("live-1", "live-2");

        releaseExec.TrySetResult(true);
        (await startTask).Status.Should().Be(RunStatus.Succeeded);
    }

    private void SetupStreamingExec(
        Guid containerId, int exitCode, (ExecStreamKind Kind, string Line)[] lines)
    {
        _containers
            .Setup(c => c.ExecStreamingAsync(
                containerId, It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<Action<ExecOutputChunk>>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, TimeSpan, Action<ExecOutputChunk>, CancellationToken>(
                (_, _, _, onLine, _) =>
                {
                    foreach (var (kind, line) in lines)
                    {
                        onLine(new ExecOutputChunk(kind, line));
                    }
                    return Task.FromResult(new ExecResult
                    {
                        ExitCode = exitCode,
                        StdOut = string.Join("\n", lines.Where(l => l.Kind == ExecStreamKind.Stdout).Select(l => l.Line)),
                        StdErr = string.Join("\n", lines.Where(l => l.Kind == ExecStreamKind.Stderr).Select(l => l.Line)),
                    });
                });
    }

    private async Task<List<RunOutputEnvelope>> DrainAsync(Guid runId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var lines = new List<RunOutputEnvelope>();
        await foreach (var env in _bus.SubscribeAsync(runId, null, cts.Token))
        {
            lines.Add(env);
        }
        return lines;
    }

    private Run SeedRun()
    {
        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = "triage-agent",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            ContainerId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Status = RunStatus.Pending,
        };
        _db.Runs.Add(run);
        _db.SaveChanges();
        return run;
    }
}
