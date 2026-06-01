using Andy.Containers.Abstractions;
using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

// F6.4 (rivoli-ai/conductor#1943). GET /ports lists mapped + discovered
// listening ports; POST /ports/expose maps a container port to a host port,
// surfacing unsupported-provider as a 400. Both are owner/RBAC-scoped.
public class ContainersControllerPortsTests : IDisposable
{
    private readonly Mock<IContainerService> _mockService = new();
    private readonly Mock<ICurrentUserService> _mockCurrentUser = new();
    private readonly Mock<IPortDiscoveryService> _mockPorts = new();
    private readonly ContainersDbContext _db;
    private readonly ContainersController _controller;

    public ContainersControllerPortsTests()
    {
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(false);
        _db = InMemoryDbHelper.CreateContext();

        var orgMembership = new Mock<IOrganizationMembershipService>();
        _controller = new ContainersController(
            _mockService.Object, _mockCurrentUser.Object, _db,
            new Mock<IGitCloneService>().Object, new Mock<IGitCredentialService>().Object,
            new Mock<IGitRepositoryProbeService>().Object, orgMembership.Object,
            new Mock<IGitDiffService>().Object, _mockPorts.Object);
    }

    public void Dispose() => _db.Dispose();

    private Container Owned(string ownerId = "test-user")
    {
        var c = new Container { Id = Guid.NewGuid(), Name = "c", OwnerId = ownerId, Status = ContainerStatus.Running };
        _mockService.Setup(s => s.GetContainerAsync(c.Id, It.IsAny<CancellationToken>())).ReturnsAsync(c);
        return c;
    }

    [Fact]
    public async Task GetPorts_OwnedContainer_ReturnsMappedAndDiscovered()
    {
        var c = Owned();
        _mockPorts.Setup(p => p.GetPortsAsync(c.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerPortsResult
            {
                Mapped = new List<MappedPort> { new() { ContainerPort = 3000, HostPort = 49001, Listening = true } },
                DiscoveredUnmapped = new List<int> { 5173 },
                SuggestedAppPort = 3000,
            });

        var result = await _controller.GetPorts(c.Id, CancellationToken.None);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<ContainerPortsDto>().Subject;
        dto.SuggestedAppPort.Should().Be(3000);
        dto.Mapped.Should().ContainSingle();
        dto.Mapped[0].WebEndpoint.Should().Be("http://localhost:49001");
        dto.Mapped[0].Listening.Should().BeTrue();
        dto.DiscoveredUnmapped.Should().Equal(5173);
    }

    [Fact]
    public async Task GetPorts_NotOwner_NotAdmin_Forbidden()
    {
        var c = Owned(ownerId: "someone-else");

        var result = await _controller.GetPorts(c.Id, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        _mockPorts.Verify(p => p.GetPortsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExposePort_Owned_ReturnsMapping()
    {
        var c = Owned();
        _mockPorts.Setup(p => p.ExposePortAsync(c.Id, 3000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MappedPort { ContainerPort = 3000, HostPort = 49100, Listening = false });

        var result = await _controller.ExposePort(c.Id, new ExposePortRequest { ContainerPort = 3000 }, CancellationToken.None);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<MappedPortDto>().Subject;
        dto.HostPort.Should().Be(49100);
        dto.WebEndpoint.Should().Be("http://localhost:49100");
    }

    [Fact]
    public async Task ExposePort_UnsupportedProvider_Returns400()
    {
        var c = Owned();
        _mockPorts.Setup(p => p.ExposePortAsync(c.Id, 3000, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("Docker cannot publish on a running container."));

        var result = await _controller.ExposePort(c.Id, new ExposePortRequest { ContainerPort = 3000 }, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.ToString().Should().Contain("Docker cannot publish");
    }

    [Fact]
    public async Task ExposePort_InvalidPort_Returns400_NoServiceCall()
    {
        var result = await _controller.ExposePort(Guid.NewGuid(), new ExposePortRequest { ContainerPort = 0 }, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        _mockPorts.Verify(p => p.ExposePortAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExposePort_NotOwner_Forbidden()
    {
        var c = Owned(ownerId: "someone-else");

        var result = await _controller.ExposePort(c.Id, new ExposePortRequest { ContainerPort = 3000 }, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        _mockPorts.Verify(p => p.ExposePortAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
