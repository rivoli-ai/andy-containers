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
    private readonly Mock<IRunModeDispatcher> _dispatcher;

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
        // AP5 wires the dispatcher into Create. Default stub: Started outcome
        // (the controller doesn't act on the result besides logging). The
        // dedicated RunModeDispatcherTests cover routing/container selection.
        _dispatcher = new Mock<IRunModeDispatcher>();
        _dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<Run>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunDispatchOutcome.Started(new HeadlessRunOutcome
            {
                Kind = RunEventKind.Finished,
                Status = RunStatus.Succeeded,
                ExitCode = 0,
            }));
        _controller = new RunsController(_db, _configurator.Object, _dispatcher.Object, NullLogger<RunsController>.Instance);
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
        dto.ContainerId.Should().BeNull("the dispatcher mock here doesn't simulate container selection");
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
    public async Task Create_InvokesDispatcher_AfterConfiguratorSuccess()
    {
        // AP5 wiring: configurator success hands off to the dispatcher with
        // the persisted Run + the config path. Container selection lives in
        // the dispatcher; the controller no longer cares whether ContainerId
        // is set when Create returns.
        var request = new CreateRunRequest
        {
            AgentId = "x",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
        };

        await _controller.Create(request, CancellationToken.None);

        _dispatcher.Verify(d => d.DispatchAsync(
            It.Is<Run>(rn => rn.Id != Guid.Empty),
            "/tmp/noop/config.json",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_DoesNotInvokeDispatcher_WhenConfiguratorFails()
    {
        // No config path means nothing to hand off — the row stays Pending
        // and the dispatcher is skipped entirely.
        _configurator
            .Setup(c => c.ConfigureAsync(It.IsAny<Run>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunConfiguratorResult.Fail("agent not found"));

        var request = new CreateRunRequest
        {
            AgentId = "x",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
        };

        await _controller.Create(request, CancellationToken.None);

        _dispatcher.Verify(d => d.DispatchAsync(It.IsAny<Run>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Create_DispatcherFails_DoesNotRollBackRun()
    {
        // A Failed/NotImplemented dispatch must not roll the row back — the
        // run is persisted for inspection / retry. The controller logs and
        // returns 201; observability comes from the persisted row + outbox.
        _dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<Run>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunDispatchOutcome.Failed("workspace not found"));

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
