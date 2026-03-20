using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class ContainerPermissionServiceTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<ICurrentUserService> _mockCurrentUser;
    private readonly ContainerPermissionService _service;

    private static readonly Guid TeamA = Guid.NewGuid();
    private static readonly Guid TeamB = Guid.NewGuid();
    private static readonly Guid OrgA = Guid.NewGuid();

    public ContainerPermissionServiceTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockCurrentUser = new Mock<ICurrentUserService>();
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(false);
        _mockCurrentUser.Setup(u => u.GetTeamId()).Returns((Guid?)null);
        _mockCurrentUser.Setup(u => u.GetOrganizationId()).Returns((Guid?)null);
        _service = new ContainerPermissionService(_db, _mockCurrentUser.Object);
    }

    public void Dispose() => _db.Dispose();

    private async Task<Container> SeedContainer(string ownerId, Guid? teamId = null, Guid? orgId = null)
    {
        var container = new Container
        {
            Name = "test-container",
            OwnerId = ownerId,
            TeamId = teamId,
            OrganizationId = orgId,
            SshEnabled = true
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();
        return container;
    }

    [Fact]
    public async Task Admin_HasPermissionOnAnyContainer()
    {
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        var container = await SeedContainer("other-user", TeamB);

        var result = await _service.HasPermissionAsync("admin-user", container.Id, "container:connect");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Owner_HasPermissionOnOwnContainer()
    {
        var container = await SeedContainer("test-user");

        var result = await _service.HasPermissionAsync("test-user", container.Id, "container:connect");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task NonOwner_DifferentTeam_Denied()
    {
        var container = await SeedContainer("other-user", TeamA);
        _mockCurrentUser.Setup(u => u.GetTeamId()).Returns(TeamB);

        var result = await _service.HasPermissionAsync("test-user", container.Id, "container:connect");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TeamMember_HasConnectPermissionOnSameTeamContainer()
    {
        var container = await SeedContainer("other-user", TeamA);
        _mockCurrentUser.Setup(u => u.GetTeamId()).Returns(TeamA);

        var result = await _service.HasPermissionAsync("test-user", container.Id, "container:connect");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task UserFromDifferentTeam_Denied()
    {
        var container = await SeedContainer("other-user", TeamA);
        _mockCurrentUser.Setup(u => u.GetTeamId()).Returns(TeamB);

        var result = await _service.HasPermissionAsync("test-user", container.Id, "container:connect");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task UserWithNoTeam_DeniedOnTeamScopedContainer()
    {
        var container = await SeedContainer("other-user", TeamA);
        _mockCurrentUser.Setup(u => u.GetTeamId()).Returns((Guid?)null);

        var result = await _service.HasPermissionAsync("test-user", container.Id, "container:connect");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task NonexistentContainer_ReturnsFalse()
    {
        var result = await _service.HasPermissionAsync("test-user", Guid.NewGuid(), "container:connect");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task SameOrganization_HasConnectPermission()
    {
        var container = await SeedContainer("other-user", orgId: OrgA);
        _mockCurrentUser.Setup(u => u.GetOrganizationId()).Returns(OrgA);

        var result = await _service.HasPermissionAsync("test-user", container.Id, "container:connect");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task DifferentOrganization_Denied()
    {
        var container = await SeedContainer("other-user", orgId: OrgA);
        _mockCurrentUser.Setup(u => u.GetOrganizationId()).Returns(Guid.NewGuid());

        var result = await _service.HasPermissionAsync("test-user", container.Id, "container:connect");

        result.Should().BeFalse();
    }
}
