using Andy.Containers.Api.Mcp;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Configurator;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Messaging;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Mcp;

// AP8 (rivoli-ai/andy-containers#110). RunsMcpTools mirrors the RunsController
// surface for MCP clients. These tests pin the contract that matters:
// permission gating, validation parity with the controller, and the
// event-stream's terminal-stop guarantee. The tools call the real
// IRunCancellationRegistry so the runner-signal path doesn't drift from
// what AP7 wired into the HTTP layer.
public class RunsMcpToolsTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IRunConfigurator> _configurator;
    private readonly Mock<IRunModeDispatcher> _dispatcher;
    private readonly RunCancellationRegistry _cancellation;
    private readonly Mock<ICurrentUserService> _currentUser;
    private readonly Mock<IOrganizationMembershipService> _orgMembership;
    private readonly RunsMcpTools _tools;
    private readonly Guid _orgId = Guid.NewGuid();

    public RunsMcpToolsTests()
    {
        _db = InMemoryDbHelper.CreateContext();

        _configurator = new Mock<IRunConfigurator>();
        _configurator
            .Setup(c => c.ConfigureAsync(It.IsAny<Run>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunConfiguratorResult.Ok("/tmp/noop/config.json"));

        _dispatcher = new Mock<IRunModeDispatcher>();
        _dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<Run>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunDispatchOutcome.Started(new HeadlessRunOutcome
            {
                Kind = RunEventKind.Finished,
                Status = RunStatus.Succeeded,
                ExitCode = 0,
            }));

        _cancellation = new RunCancellationRegistry();

        _currentUser = new Mock<ICurrentUserService>();
        _currentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _currentUser.Setup(u => u.IsAdmin()).Returns(false);
        _currentUser.Setup(u => u.GetOrganizationId()).Returns(_orgId);

        _orgMembership = new Mock<IOrganizationMembershipService>();
        // Default: user has every run permission. Specific tests flip
        // individual perms to false to exercise denial paths.
        _orgMembership
            .Setup(o => o.HasPermissionAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _tools = new RunsMcpTools(
            _db, _configurator.Object, _dispatcher.Object, _cancellation,
            _currentUser.Object, _orgMembership.Object,
            NullLogger<RunsMcpTools>.Instance);
    }

    public void Dispose() => _db.Dispose();

    // run.create

    [Fact]
    public async Task Create_HappyPath_PersistsRunAndCallsDispatcher()
    {
        var profileId = Guid.NewGuid();

        var dto = await _tools.Create(
            agentId: "triage-agent",
            mode: "Headless",
            environmentProfileId: profileId.ToString());

        dto.Should().NotBeNull();
        dto!.AgentId.Should().Be("triage-agent");
        dto.Mode.Should().Be(RunMode.Headless);
        dto.EnvironmentProfileId.Should().Be(profileId);

        var persisted = await _db.Runs.FirstOrDefaultAsync(r => r.Id == dto.Id);
        persisted.Should().NotBeNull();

        _configurator.Verify(c => c.ConfigureAsync(It.IsAny<Run>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _dispatcher.Verify(d => d.DispatchAsync(It.IsAny<Run>(), "/tmp/noop/config.json", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Create_NonAdminWithoutRunWrite_ReturnsNullAndDoesNotPersist()
    {
        _orgMembership
            .Setup(o => o.HasPermissionAsync(
                It.IsAny<string>(), _orgId, Permissions.RunWrite, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var dto = await _tools.Create(
            agentId: "x", mode: "Headless", environmentProfileId: Guid.NewGuid().ToString());

        dto.Should().BeNull();
        (await _db.Runs.AnyAsync()).Should().BeFalse(
            "permission denial must not produce a Pending row");
        _dispatcher.Verify(d => d.DispatchAsync(
            It.IsAny<Run>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Create_AdminBypassesOrgPermissionCheck()
    {
        _currentUser.Setup(u => u.IsAdmin()).Returns(true);
        _orgMembership
            .Setup(o => o.HasPermissionAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var dto = await _tools.Create(
            agentId: "x", mode: "Headless", environmentProfileId: Guid.NewGuid().ToString());

        dto.Should().NotBeNull("admin shortcut precedes the org permission lookup");
    }

    [Theory]
    [InlineData("not-a-mode")]
    [InlineData("")]
    public async Task Create_InvalidMode_ReturnsNull(string mode)
    {
        var dto = await _tools.Create(
            agentId: "x", mode: mode, environmentProfileId: Guid.NewGuid().ToString());

        dto.Should().BeNull();
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Create_InvalidEnvironmentProfileId_ReturnsNull(string profileId)
    {
        var dto = await _tools.Create(
            agentId: "x", mode: "Headless", environmentProfileId: profileId);

        dto.Should().BeNull();
    }

    [Fact]
    public async Task Create_ConfiguratorFails_StillReturnsPendingDto()
    {
        _configurator
            .Setup(c => c.ConfigureAsync(It.IsAny<Run>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunConfiguratorResult.Fail("simulated"));

        var dto = await _tools.Create(
            agentId: "x", mode: "Headless", environmentProfileId: Guid.NewGuid().ToString());

        dto.Should().NotBeNull();
        dto!.Status.Should().Be(RunStatus.Pending);
        _dispatcher.Verify(d => d.DispatchAsync(
            It.IsAny<Run>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "dispatcher must not be invoked when the configurator fails");
    }

    // run.get

    [Fact]
    public async Task Get_ExistingRun_ReturnsDto()
    {
        var run = SeedRun(RunStatus.Running);

        var dto = await _tools.Get(run.Id.ToString());

        dto.Should().NotBeNull();
        dto!.Id.Should().Be(run.Id);
        dto.Status.Should().Be(RunStatus.Running);
    }

    [Fact]
    public async Task Get_NonexistentRun_ReturnsNull()
    {
        var dto = await _tools.Get(Guid.NewGuid().ToString());

        dto.Should().BeNull();
    }

    [Fact]
    public async Task Get_InvalidGuid_ReturnsNull()
    {
        var dto = await _tools.Get("not-a-guid");

        dto.Should().BeNull();
    }

    [Fact]
    public async Task Get_NonAdminWithoutRunRead_ReturnsNull()
    {
        var run = SeedRun(RunStatus.Running);
        _orgMembership
            .Setup(o => o.HasPermissionAsync(
                It.IsAny<string>(), _orgId, Permissions.RunRead, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var dto = await _tools.Get(run.Id.ToString());

        dto.Should().BeNull();
    }

    // run.cancel

    [Fact]
    public async Task Cancel_PendingRun_ReturnsCancelledDto_EmitsEvent()
    {
        var run = SeedRun(RunStatus.Pending);

        var dto = await _tools.Cancel(run.Id.ToString());

        dto.Should().NotBeNull();
        dto!.Status.Should().Be(RunStatus.Cancelled);

        var entry = _db.OutboxEntries.Should().ContainSingle().Subject;
        entry.Subject.Should().EndWith($".{run.Id}.cancelled");
    }

    [Fact]
    public async Task Cancel_TerminalRun_ReturnsNull()
    {
        var run = SeedRun(RunStatus.Succeeded);

        var dto = await _tools.Cancel(run.Id.ToString());

        dto.Should().BeNull("MCP surfaces 'already terminal' as null per the tool docstring");
        _db.OutboxEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_NonexistentRun_ReturnsNull()
    {
        var dto = await _tools.Cancel(Guid.NewGuid().ToString());

        dto.Should().BeNull();
    }

    [Fact]
    public async Task Cancel_NonAdminWithoutRunExecute_ReturnsNull_DoesNotFlipRow()
    {
        var run = SeedRun(RunStatus.Pending);
        _orgMembership
            .Setup(o => o.HasPermissionAsync(
                It.IsAny<string>(), _orgId, Permissions.RunExecute, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var dto = await _tools.Cancel(run.Id.ToString());

        dto.Should().BeNull();
        var persisted = await _db.Runs.FindAsync(run.Id);
        persisted!.Status.Should().Be(RunStatus.Pending,
            "denied cancels must not mutate the run");
    }

    [Fact]
    public async Task Cancel_ActiveRunner_SignalsRegistry_AwaitsTerminal()
    {
        var run = SeedRun(RunStatus.Running);
        var registration = _cancellation.Register(run.Id, CancellationToken.None);

        // Simulate the runner's own terminate-on-cancel after a beat.
        _ = Task.Run(async () =>
        {
            await Task.Delay(10);
            run.TransitionTo(RunStatus.Cancelled);
            _db.AppendAgentRunEvent(run, RunEventKind.Cancelled);
            await _db.SaveChangesAsync();
            registration.Dispose();
        });

        var dto = await _tools.Cancel(run.Id.ToString());

        dto.Should().NotBeNull();
        dto!.Status.Should().Be(RunStatus.Cancelled);
        _db.OutboxEntries.Should().ContainSingle(
            "the runner's TerminateAsync emits the event; the MCP tool doesn't double-emit");
    }

    // run.events

    [Fact]
    public async Task Events_TerminalRunWithBackfill_YieldsAllEventsThenStops()
    {
        var run = SeedRun(RunStatus.Succeeded);
        // Two backfilled outbox rows, one a wrong-run noise row that must
        // be filtered out by the subject prefix check.
        _db.AppendAgentRunEvent(run, RunEventKind.Finished, exitCode: 0, durationSeconds: 1.5);
        var unrelated = SeedRun(RunStatus.Failed);
        _db.AppendAgentRunEvent(unrelated, RunEventKind.Failed);
        await _db.SaveChangesAsync();

        var collected = new List<RunEventDto>();
        await foreach (var evt in _tools.Events(run.Id.ToString()))
        {
            collected.Add(evt);
        }

        collected.Should().ContainSingle()
            .Which.Subject.Should().Be($"andy.containers.events.run.{run.Id}.finished");
        collected[0].Kind.Should().Be("finished");
        collected[0].ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task Events_StopsOnTerminalAfterDrain()
    {
        var run = SeedRun(RunStatus.Running);

        // Start the stream, then transition + emit + flip terminal.
        var streamCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var streamTask = Task.Run(async () =>
        {
            var collected = new List<RunEventDto>();
            await foreach (var evt in _tools.Events(run.Id.ToString(), streamCts.Token))
            {
                collected.Add(evt);
            }
            return collected;
        });

        // Wait one poll cycle so the consumer is in the loop.
        await Task.Delay(50);

        run.TransitionTo(RunStatus.Cancelled);
        _db.AppendAgentRunEvent(run, RunEventKind.Cancelled);
        await _db.SaveChangesAsync();

        var collected = await streamTask;

        collected.Should().ContainSingle().Which.Kind.Should().Be("cancelled");
        streamCts.IsCancellationRequested.Should().BeFalse(
            "the stream must close on terminal observation rather than the timeout");
    }

    [Fact]
    public async Task Events_NonAdminWithoutRunRead_YieldsNothing()
    {
        var run = SeedRun(RunStatus.Pending);
        _orgMembership
            .Setup(o => o.HasPermissionAsync(
                It.IsAny<string>(), _orgId, Permissions.RunRead, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var collected = new List<RunEventDto>();
        await foreach (var evt in _tools.Events(run.Id.ToString()))
        {
            collected.Add(evt);
        }

        collected.Should().BeEmpty();
    }

    [Fact]
    public async Task Events_InvalidGuid_YieldsNothing()
    {
        var collected = new List<RunEventDto>();
        await foreach (var evt in _tools.Events("not-a-guid"))
        {
            collected.Add(evt);
        }

        collected.Should().BeEmpty();
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
        _db.Runs.Add(run);
        _db.SaveChanges();
        return run;
    }
}
