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
    private readonly Mock<IHeadlessRunLauncher> _launcher = new();
    private readonly Mock<IRunBranchService> _runBranch = new();
    private readonly RunModeDispatcher _dispatcher;
    private const string ConfigPath = "/tmp/runs/x/config.json";

    public RunModeDispatcherTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _launcher
            .Setup(l => l.Launch(It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _dispatcher = new RunModeDispatcher(_db, _launcher.Object, _runBranch.Object, NullLogger<RunModeDispatcher>.Instance);
    }

    public void Dispose() => _db.Dispose();

    // F6.1 (rivoli-ai/conductor#1940): the dispatcher prepares the per-run
    // branch on the selected container before handing off to the runner.
    [Fact]
    public async Task Dispatch_Headless_EnsuresRunBranchOnSelectedContainer()
    {
        var (run, workspace) = SeedRunAndWorkspace(RunMode.Headless);

        await _dispatcher.DispatchAsync(run, ConfigPath);

        _runBranch.Verify(b => b.EnsureRunBranchAsync(
            It.Is<Run>(rn => rn.Id == run.Id),
            workspace.DefaultContainerId!.Value,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dispatch_Headless_RunBranchFailure_DoesNotAbortDispatch()
    {
        var (run, _) = SeedRunAndWorkspace(RunMode.Headless);
        _runBranch
            .Setup(b => b.EnsureRunBranchAsync(It.IsAny<Run>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("branch boom"));

        var outcome = await _dispatcher.DispatchAsync(run, ConfigPath);

        outcome.Kind.Should().Be(RunDispatchKind.Detached);
    }

    [Fact]
    public async Task Dispatch_HeadlessHappyPath_AssignsContainer_TransitionsProvisioning_DetachesToLauncher()
    {
        // AX.16 (rivoli-ai/conductor#2104): the dispatcher no longer awaits
        // the runner — it hands the run id + config path to the background
        // launcher and returns Detached immediately. Terminal state reaches
        // callers via run events / polling.
        var (run, workspace) = SeedRunAndWorkspace(RunMode.Headless);

        var outcome = await _dispatcher.DispatchAsync(run, ConfigPath);

        outcome.Kind.Should().Be(RunDispatchKind.Detached);
        run.ContainerId.Should().Be(workspace.DefaultContainerId);
        // Detach happens AFTER the Provisioning transition is persisted, so
        // the background scope re-loads a row that already carries the
        // container assignment.
        run.Status.Should().Be(RunStatus.Provisioning);
        _launcher.Verify(l => l.Launch(run.Id, ConfigPath), Times.Once);
    }

    [Fact]
    public async Task Dispatch_Terminal_AssignsContainer_ReturnsAttachable_DoesNotInvokeRunner()
    {
        var (run, workspace) = SeedRunAndWorkspace(RunMode.Terminal);

        var outcome = await _dispatcher.DispatchAsync(run, ConfigPath);

        outcome.Kind.Should().Be(RunDispatchKind.Attachable);
        run.ContainerId.Should().Be(workspace.DefaultContainerId);
        run.Status.Should().Be(RunStatus.Provisioning,
            "Terminal-mode runs are still provisioned — the user attaches separately via the terminal WS");

        _launcher.Verify(l => l.Launch(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
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
        _launcher.VerifyNoOtherCalls();
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
    public async Task Dispatch_FailurePaths_NeverReachTheLauncher()
    {
        // A run that fails container selection must not be detached — the
        // launcher would re-load a row with no ContainerId and fail later,
        // losing the actionable error the dispatcher already has.
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
        _launcher.Verify(l => l.Launch(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
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

    // rivoli-ai/conductor#2122 — a dispatch-level failure must be TERMINAL:
    // the Run row transitions to Failed with the reason recorded, and the
    // andy.containers.events.run.{id}.failed outbox event is published so
    // downstream consumers (andy-tasks' RunEventConsumer) fold the truth
    // instead of leaving their AgentRun rows Running forever.
    [Fact]
    public async Task Dispatch_NoWorkspaceRef_TransitionsRunToFailed_AndPublishesFailedEvent()
    {
        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = "x",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Status = RunStatus.Pending,
        };
        _db.Runs.Add(run);
        await _db.SaveChangesAsync();

        var outcome = await _dispatcher.DispatchAsync(run, ConfigPath);

        outcome.Kind.Should().Be(RunDispatchKind.Failed);
        run.Status.Should().Be(RunStatus.Failed);
        run.Error.Should().Contain("workspace reference");
        _db.OutboxEntries.Should().ContainSingle(e =>
            e.Subject == $"andy.containers.events.run.{run.Id}.failed");
    }

    [Fact]
    public async Task Dispatch_WorkspaceNotFound_TransitionsRunToFailed_AndPublishesFailedEvent()
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

        await _dispatcher.DispatchAsync(run, ConfigPath);

        run.Status.Should().Be(RunStatus.Failed);
        run.Error.Should().Contain("not found");
        _db.OutboxEntries.Should().ContainSingle(e =>
            e.Subject == $"andy.containers.events.run.{run.Id}.failed");
    }

    // Desktop's NotImplemented bail deliberately keeps the row Pending
    // (a not-yet-wired mode is retryable, not failed) — pin that the new
    // terminal handling did not leak into it.
    [Fact]
    public async Task Dispatch_Desktop_StaysPending_NoFailedEvent()
    {
        var (run, _) = SeedRunAndWorkspace(RunMode.Desktop);

        var outcome = await _dispatcher.DispatchAsync(run, ConfigPath);

        outcome.Kind.Should().Be(RunDispatchKind.NotImplemented);
        run.Status.Should().Be(RunStatus.Pending);
        _db.OutboxEntries.Should().NotContain(e =>
            e.Subject == $"andy.containers.events.run.{run.Id}.failed");
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
