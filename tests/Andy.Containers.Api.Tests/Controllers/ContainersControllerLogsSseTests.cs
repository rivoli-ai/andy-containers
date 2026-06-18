using System.Text;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Runs.Events;
using Andy.Containers.Models;
using Andy.Containers.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

// rivoli-ai/conductor#2236. GET /api/containers/{id}/logs is the
// CONTAINER-scoped counterpart of GET /api/runs/{id}/output: Conductor's
// live agent feed (TX F4.2, #1935) is keyed by the goal's workspace
// container id (decision #21), not run id, so it connects HERE. The
// container-logs route was documented + promised by IRunOutputBus /
// RunOutputSse but never wired, so the feed connected to a non-existent
// route (404) and rendered nothing during plan execution.
//
// This suite proves the endpoint: it resolves the container's most-recent
// run, delegates to the shared RunOutputSse serialiser (byte-identical
// wire format to the run-scoped endpoint), and degrades cleanly — empty
// SSE stream for a container with no run yet, 404 for an unknown
// container, 403 for a non-owner. MemoryStream-backed DefaultHttpContext,
// mirroring RunsControllerOutputSseTests.
public class ContainersControllerLogsSseTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly InMemoryRunOutputBus _bus = new();
    private readonly Mock<IContainerService> _mockService = new();
    private readonly Mock<ICurrentUserService> _mockCurrentUser = new();
    private readonly ContainersController _controller;

    public ContainersControllerLogsSseTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);

        var orgMembership = new Mock<IOrganizationMembershipService>();
        orgMembership.Setup(o => o.IsMemberAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        _controller = new ContainersController(
            _mockService.Object,
            _mockCurrentUser.Object,
            _db,
            new Mock<IGitCloneService>().Object,
            new Mock<IGitCredentialService>().Object,
            new Mock<IGitRepositoryProbeService>().Object,
            orgMembership.Object,
            new Mock<IGitDiffService>().Object,
            new Mock<IPortDiscoveryService>().Object,
            new Mock<IContainerLifecycleBus>().Object,
            _bus);
    }

    public void Dispose()
    {
        _db.Dispose();
        _bus.Dispose();
    }

    [Fact]
    public async Task Logs_StreamsActiveRunOutputInWireFormat()
    {
        var container = CreateContainer();
        var run = SeedRun(container.Id, RunStatus.Running);
        var responseStream = SetupResponse();

        var now = DateTimeOffset.UtcNow;
        _bus.Publish(run.Id, new RunOutputLine(RunOutputStream.Stdout, "hello from the agent", now));
        _bus.Publish(run.Id, new RunOutputLine(RunOutputStream.Stderr, "a warning", now));
        _bus.Complete(run.Id);

        await _controller.Logs(container.Id, CancellationToken.None);

        var body = ReadBody(responseStream);

        _controller.Response.Headers.ContentType.ToString().Should().Be("text/event-stream");
        body.Should().Contain("id: 1\nevent: log\ndata: ");
        body.Should().Contain("\"stream\":\"stdout\"");
        body.Should().Contain("\"stream\":\"stderr\"");
        body.Should().Contain("\"line\":\"hello from the agent\"");
    }

    [Fact]
    public async Task Logs_PrefersLiveRunOverFinishedRun()
    {
        var container = CreateContainer();
        // An older finished run plus a newer running run on the same
        // container — the live run's output is what the feed must stream.
        var finished = SeedRun(container.Id, RunStatus.Succeeded, createdAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var running = SeedRun(container.Id, RunStatus.Running, createdAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var responseStream = SetupResponse();

        _bus.Publish(finished.Id, new RunOutputLine(RunOutputStream.Stdout, "OLD", DateTimeOffset.UtcNow));
        _bus.Complete(finished.Id);
        _bus.Publish(running.Id, new RunOutputLine(RunOutputStream.Stdout, "NEW", DateTimeOffset.UtcNow));
        _bus.Complete(running.Id);

        await _controller.Logs(container.Id, CancellationToken.None);

        var body = ReadBody(responseStream);
        body.Should().Contain("\"line\":\"NEW\"");
        body.Should().NotContain("\"line\":\"OLD\"");
    }

    [Fact]
    public async Task Logs_NoRunYet_EmptySseStreamNot404()
    {
        var container = CreateContainer();
        var responseStream = SetupResponse();

        await _controller.Logs(container.Id, CancellationToken.None);

        // A healthy container with no dispatched run is NOT an error — the
        // feed renders its empty-state, not a hang and not a 404.
        _controller.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        _controller.Response.Headers.ContentType.ToString().Should().Be("text/event-stream");
        ReadBody(responseStream).Should().BeEmpty();
    }

    [Fact]
    public async Task Logs_UnknownContainer_Returns404()
    {
        SetupResponse();
        var unknown = Guid.NewGuid();
        _mockService.Setup(s => s.GetContainerAsync(unknown, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        await _controller.Logs(unknown, CancellationToken.None);

        _controller.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Logs_NonOwnerNonAdmin_Returns403()
    {
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(false);
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("attacker");
        var container = CreateContainer(ownerId: "victim");
        SeedRun(container.Id, RunStatus.Running);
        SetupResponse();

        await _controller.Logs(container.Id, CancellationToken.None);

        _controller.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    private MemoryStream SetupResponse(string? lastEventId = null)
    {
        var responseStream = new MemoryStream();
        var context = new DefaultHttpContext { Response = { Body = responseStream } };
        if (lastEventId is not null)
        {
            context.Request.Headers["Last-Event-ID"] = lastEventId;
        }
        _controller.ControllerContext = new ControllerContext { HttpContext = context };
        return responseStream;
    }

    private static string ReadBody(MemoryStream stream)
    {
        stream.Position = 0;
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private Container CreateContainer(string ownerId = "test-user")
    {
        var container = new Container
        {
            Id = Guid.NewGuid(),
            Name = "ws-container",
            OwnerId = ownerId,
            Status = ContainerStatus.Running,
        };
        _mockService.Setup(s => s.GetContainerAsync(container.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);
        return container;
    }

    private Run SeedRun(Guid containerId, RunStatus status, DateTimeOffset? createdAt = null)
    {
        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = "seed-agent",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            ContainerId = containerId,
            Status = status,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };
        _db.Runs.Add(run);
        _db.SaveChanges();
        return run;
    }
}
