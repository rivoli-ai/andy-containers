using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// AP5 (rivoli-ai/andy-containers#107). Mode dispatcher selects a container
// from the run's workspace, transitions Pending → Provisioning, and routes
// by Mode: headless → IHeadlessRunner; terminal → Attachable; desktop →
// NotImplemented (no GUI provider yet). Failure modes here keep the run
// row queryable rather than rolling it back.
public class RunModeDispatcherTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IHeadlessRunner> _runner = new();
    private readonly RunModeDispatcher _dispatcher;
    private const string ConfigPath = "/tmp/runs/x/config.json";

    public RunModeDispatcherTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _dispatcher = new RunModeDispatcher(_db, _runner.Object, NullLogger<RunModeDispatcher>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Dispatch_HeadlessHappyPath_AssignsContainer_TransitionsProvisioning_InvokesRunner()
    {
        var (run, workspace) = SeedRunAndWorkspace(RunMode.Headless);
        _runner
            .Setup(r => r.StartAsync(It.IsAny<Run>(), ConfigPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HeadlessRunOutcome
            {
                Kind = RunEventKind.Finished,
                Status = RunStatus.Succeeded,
                ExitCode = 0,
            });

        var outcome = await _dispatcher.DispatchAsync(run, ConfigPath);

        outcome.Kind.Should().Be(RunDispatchKind.Started);
        outcome.HeadlessOutcome.Should().NotBeNull();
        outcome.HeadlessOutcome!.Status.Should().Be(RunStatus.Succeeded);

        run.ContainerId.Should().Be(workspace.DefaultContainerId);
        // The runner advances Provisioning → Running → terminal; we observe
        // the final state, but ContainerId proves AP5 ran first.
        _runner.Verify(r => r.StartAsync(
            It.Is<Run>(rn => rn.ContainerId == workspace.DefaultContainerId),
            ConfigPath,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dispatch_Headless_TransitionsToProvisioningBeforeRunnerCall()
    {
        // Pin the runner-side state at call time: dispatcher must have moved
        // Pending → Provisioning before invoking the runner so AP6 sees the
        // expected starting status.
        var (run, _) = SeedRunAndWorkspace(RunMode.Headless);
        RunStatus? statusSeenByRunner = null;
        _runner
            .Setup(r => r.StartAsync(It.IsAny<Run>(), ConfigPath, It.IsAny<CancellationToken>()))
            .Callback<Run, string, CancellationToken>((r, _, _) => statusSeenByRunner = r.Status)
            .ReturnsAsync(new HeadlessRunOutcome { Kind = RunEventKind.Finished, Status = RunStatus.Succeeded });

        await _dispatcher.DispatchAsync(run, ConfigPath);

        statusSeenByRunner.Should().Be(RunStatus.Provisioning);
    }

    [Fact]
    public async Task Dispatch_Terminal_AssignsContainer_ReturnsAttachable_DoesNotInvokeRunner()
    {
        var (run, workspace) = SeedRunAndWorkspace(RunMode.Terminal);

        var outcome = await _dispatcher.DispatchAsync(run, ConfigPath);

        outcome.Kind.Should().Be(RunDispatchKind.Attachable);
        outcome.HeadlessOutcome.Should().BeNull();
        run.ContainerId.Should().Be(workspace.DefaultContainerId);
        run.Status.Should().Be(RunStatus.Provisioning,
            "Terminal-mode runs are still provisioned — the user attaches separately via the terminal WS");

        _runner.Verify(r => r.StartAsync(It.IsAny<Run>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Dispatch_Desktop_ReturnsNotImplemented_DoesNotTouchRun()
    {
        // Desktop has no GUI provider yet (Epic AP doesn't ship one). The
        // dispatcher must short-circuit before assigning ContainerId so the
        // row isn't half-configured for an execution path that won't fire.
        var (run, _) = SeedRunAndWorkspace(RunMode.Desktop);

        var outcome = await _dispatcher.DispatchAsync(run, ConfigPath);

        outcome.Kind.Should().Be(RunDispatchKind.NotImplemented);
        outcome.Error.Should().NotBeNullOrEmpty();
        run.ContainerId.Should().BeNull();
        run.Status.Should().Be(RunStatus.Pending);
        _runner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Dispatch_NoWorkspaceRef_Fails()
    {
        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = "x",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Status = RunStatus.Pending,
            // WorkspaceRef defaulted, WorkspaceId = Guid.Empty
        };
        _db.Runs.Add(run);
        await _db.SaveChangesAsync();

        var outcome = await _dispatcher.DispatchAsync(run, ConfigPath);

        outcome.Kind.Should().Be(RunDispatchKind.Failed);
        run.ContainerId.Should().BeNull();
    }

    [Fact]
    public async Task Dispatch_WorkspaceNotFound_Fails()
    {
        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = "x",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Status = RunStatus.Pending,
            WorkspaceRef = new WorkspaceRef { WorkspaceId = Guid.NewGuid() }, // not seeded
        };
        _db.Runs.Add(run);
        await _db.SaveChangesAsync();

        var outcome = await _dispatcher.DispatchAsync(run, ConfigPath);

        outcome.Kind.Should().Be(RunDispatchKind.Failed);
        outcome.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task Dispatch_WorkspaceWithoutDefaultContainer_Fails()
    {
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = "ws",
            OwnerId = "u",
            DefaultContainerId = null,
        };
        _db.Workspaces.Add(workspace);
        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = "x",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Status = RunStatus.Pending,
            WorkspaceRef = new WorkspaceRef { WorkspaceId = workspace.Id },
        };
        _db.Runs.Add(run);
        await _db.SaveChangesAsync();

        var outcome = await _dispatcher.DispatchAsync(run, ConfigPath);

        outcome.Kind.Should().Be(RunDispatchKind.Failed);
        outcome.Error.Should().Contain("default container");
        run.ContainerId.Should().BeNull();
    }

    [Fact]
    public async Task Dispatch_RunnerThrows_ReturnsFailed()
    {
        var (run, _) = SeedRunAndWorkspace(RunMode.Headless);
        _runner
            .Setup(r => r.StartAsync(It.IsAny<Run>(), ConfigPath, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("kaboom"));

        var outcome = await _dispatcher.DispatchAsync(run, ConfigPath);

        outcome.Kind.Should().Be(RunDispatchKind.Failed);
        outcome.Error.Should().Be("kaboom");
        // ContainerId is still set — the runner is the canonical place to
        // mark the run Failed; we leave the row in Provisioning so the
        // operator can see exactly where it stopped.
        run.ContainerId.Should().NotBeNull();
    }

    [Fact]
    public async Task Dispatch_NullRun_Throws()
    {
        Func<Task> act = () => _dispatcher.DispatchAsync(null!, ConfigPath);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Dispatch_BlankConfigPath_Throws()
    {
        var (run, _) = SeedRunAndWorkspace(RunMode.Headless);
        Func<Task> act = () => _dispatcher.DispatchAsync(run, "  ");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    private (Run run, Workspace workspace) SeedRunAndWorkspace(RunMode mode)
    {
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = "ws-" + mode.ToString().ToLowerInvariant(),
            OwnerId = "u",
            DefaultContainerId = Guid.NewGuid(),
        };
        _db.Workspaces.Add(workspace);

        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = "triage-agent",
            Mode = mode,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Status = RunStatus.Pending,
            WorkspaceRef = new WorkspaceRef { WorkspaceId = workspace.Id },
        };
        _db.Runs.Add(run);
        _db.SaveChanges();
        return (run, workspace);
    }
}
