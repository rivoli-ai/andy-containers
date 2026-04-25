using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Configurator;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

// AP2 (rivoli-ai/andy-containers#104). Verifies the three /api/runs endpoints
// directly against an in-memory ContainersDbContext — no service mocks needed
// because RunsController works on the DbContext directly. Cancellation
// transition rules are owned by Run.TransitionTo (AP1); these tests assert
// only the controller-level edge handling (404 on missing, 409 on illegal
// transition, 200/201 on success).
public class RunsControllerTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly RunsController _controller;
    private readonly Mock<IRunConfigurator> _configurator;
    private readonly Mock<IHeadlessRunner> _runner;

    public RunsControllerTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        // AP3 wires the configurator into Create. These tests focus on
        // controller behaviour, not configurator behaviour, so stub it to a
        // no-op success — the dedicated configurator tests cover its surface.
        _configurator = new Mock<IRunConfigurator>();
        _configurator
            .Setup(c => c.ConfigureAsync(It.IsAny<Run>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunConfiguratorResult.Ok("/tmp/noop/config.json"));
        // AP6 wires the runner into Create. Default stub: no-op outcome.
        // Runs without a ContainerId never invoke it (the controller skips
        // the call), and the dedicated HeadlessRunnerTests cover its surface.
        _runner = new Mock<IHeadlessRunner>();
        _runner
            .Setup(r => r.StartAsync(It.IsAny<Run>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HeadlessRunOutcome
            {
                Kind = RunEventKind.Finished,
                Status = RunStatus.Succeeded,
                ExitCode = 0,
            });
        _controller = new RunsController(_db, _configurator.Object, _runner.Object, NullLogger<RunsController>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task Create_ValidRequest_PersistsRunAsPending_ReturnsCreated()
    {
        var request = new CreateRunRequest
        {
            AgentId = "triage-agent",
            AgentRevision = 3,
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            WorkspaceRef = new WorkspaceRefRequest
            {
                WorkspaceId = Guid.NewGuid(),
                Branch = "main",
            },
            PolicyId = Guid.NewGuid(),
        };

        var result = await _controller.Create(request, CancellationToken.None);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.ActionName.Should().Be(nameof(RunsController.Get));
        var dto = created.Value.Should().BeOfType<RunDto>().Subject;

        dto.Id.Should().NotBeEmpty();
        dto.Status.Should().Be(RunStatus.Pending);
        dto.ContainerId.Should().BeNull("AP5 mode dispatcher hasn't run yet");
        dto.AgentId.Should().Be("triage-agent");
        dto.WorkspaceRef.Branch.Should().Be("main");
        dto.CorrelationId.Should().NotBeEmpty("controller mints a root id when caller doesn't supply one");
        dto.Links.Self.Should().Be($"/api/runs/{dto.Id}");
        dto.Links.Cancel.Should().Be($"/api/runs/{dto.Id}/cancel", "non-terminal runs expose a cancel link");

        var persisted = await _db.Runs.FindAsync(dto.Id);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(RunStatus.Pending);
        persisted.AgentRevision.Should().Be(3);
    }

    [Fact]
    public async Task Create_InvokesConfiguratorAfterPersisting()
    {
        // AP3 wiring: the controller calls the configurator with the
        // persisted Run (Id assigned, Status Pending). A configurator failure
        // does NOT roll the row back; the row stays Pending so AP5/AP6 can
        // retry on the next pass.
        _configurator
            .Setup(c => c.ConfigureAsync(It.IsAny<Run>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunConfiguratorResult.Fail("agent not found"));

        var request = new CreateRunRequest
        {
            AgentId = "triage-agent",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
        };

        var result = await _controller.Create(request, CancellationToken.None);

        result.Should().BeOfType<CreatedAtActionResult>();
        _configurator.Verify(c => c.ConfigureAsync(
            It.Is<Run>(r => r.Id != Guid.Empty && r.Status == RunStatus.Pending),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_DoesNotInvokeRunner_WhenContainerIdNull()
    {
        // AP6 wiring: AP5 hasn't run yet, so ContainerId is null and the
        // controller must not attempt to spawn — the runner needs a target.
        var request = new CreateRunRequest
        {
            AgentId = "x",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
        };

        await _controller.Create(request, CancellationToken.None);

        _runner.Verify(r => r.StartAsync(It.IsAny<Run>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Create_InvokesRunner_WhenConfiguratorSucceeds_AndContainerIdPresent()
    {
        // Once AP5 lands and assigns a ContainerId, the controller hands
        // off to the runner with the configurator's path. We simulate that
        // by intercepting the configurator and stamping ContainerId on the
        // run before the runner check fires.
        var containerId = Guid.NewGuid();
        _configurator
            .Setup(c => c.ConfigureAsync(It.IsAny<Run>(), It.IsAny<CancellationToken>()))
            .Callback<Run, CancellationToken>((r, _) => r.ContainerId = containerId)
            .ReturnsAsync(RunConfiguratorResult.Ok("/tmp/runs/x/config.json"));

        var request = new CreateRunRequest
        {
            AgentId = "x",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
        };

        await _controller.Create(request, CancellationToken.None);

        _runner.Verify(r => r.StartAsync(
            It.Is<Run>(rn => rn.ContainerId == containerId),
            "/tmp/runs/x/config.json",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_RunnerThrows_DoesNotRollBackRun()
    {
        // Spawn failures must not roll the row back — the configurator's
        // existing failure semantics apply to AP6 too, so the Run row
        // persists for inspection / cleanup.
        var containerId = Guid.NewGuid();
        _configurator
            .Setup(c => c.ConfigureAsync(It.IsAny<Run>(), It.IsAny<CancellationToken>()))
            .Callback<Run, CancellationToken>((r, _) => r.ContainerId = containerId)
            .ReturnsAsync(RunConfiguratorResult.Ok("/tmp/runs/x/config.json"));
        _runner
            .Setup(r => r.StartAsync(It.IsAny<Run>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("kaboom"));

        var request = new CreateRunRequest
        {
            AgentId = "x",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
        };

        var result = await _controller.Create(request, CancellationToken.None);

        var dto = result.Should().BeOfType<CreatedAtActionResult>().Subject.Value
            .Should().BeOfType<RunDto>().Subject;
        var persisted = await _db.Runs.FindAsync(dto.Id);
        persisted.Should().NotBeNull();
    }

    [Fact]
    public async Task Create_PreservesCallerSuppliedCorrelationId()
    {
        var caller = Guid.NewGuid();
        var request = new CreateRunRequest
        {
            AgentId = "x",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = caller,
        };

        var result = await _controller.Create(request, CancellationToken.None);

        var dto = result.Should().BeOfType<CreatedAtActionResult>().Subject.Value
            .Should().BeOfType<RunDto>().Subject;
        dto.CorrelationId.Should().Be(caller);
    }

    [Fact]
    public async Task Create_EmptyEnvironmentProfileId_ReturnsBadRequest()
    {
        var request = new CreateRunRequest
        {
            AgentId = "x",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.Empty,
        };

        var result = await _controller.Create(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_WorkspaceRefWithEmptyId_ReturnsBadRequest()
    {
        var request = new CreateRunRequest
        {
            AgentId = "x",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            WorkspaceRef = new WorkspaceRefRequest { WorkspaceId = Guid.Empty },
        };

        var result = await _controller.Create(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Get_ExistingRun_ReturnsOk()
    {
        var run = SeedRun(RunStatus.Running);

        var result = await _controller.Get(run.Id, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<RunDto>().Subject;
        dto.Id.Should().Be(run.Id);
        dto.Status.Should().Be(RunStatus.Running);
        dto.Links.Cancel.Should().NotBeNull("Running is a non-terminal state");
    }

    [Fact]
    public async Task Get_NonexistentRun_ReturnsNotFound()
    {
        var result = await _controller.Get(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Cancel_PendingRun_TransitionsToCancelled_ReturnsOk()
    {
        var run = SeedRun(RunStatus.Pending);

        var result = await _controller.Cancel(run.Id, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<RunDto>().Subject;
        dto.Status.Should().Be(RunStatus.Cancelled);
        dto.EndedAt.Should().NotBeNull("terminal transition stamps EndedAt");
        dto.Links.Cancel.Should().BeNull("terminal runs cannot be cancelled again");

        var persisted = await _db.Runs.FindAsync(run.Id);
        persisted!.Status.Should().Be(RunStatus.Cancelled);
    }

    [Fact]
    public async Task Cancel_TerminalRun_ReturnsConflict()
    {
        var run = SeedRun(RunStatus.Succeeded);

        var result = await _controller.Cancel(run.Id, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>(
            "Run.TransitionTo throws on illegal transitions and the controller maps to 409");
    }

    [Fact]
    public async Task Cancel_NonexistentRun_ReturnsNotFound()
    {
        var result = await _controller.Cancel(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    private Run SeedRun(RunStatus status)
    {
        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = "seed-agent",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Status = status,
        };
        // Terminal states need EndedAt for invariants downstream; the test
        // doesn't depend on that, so leaving it null is fine.
        _db.Runs.Add(run);
        _db.SaveChanges();
        return run;
    }
}
