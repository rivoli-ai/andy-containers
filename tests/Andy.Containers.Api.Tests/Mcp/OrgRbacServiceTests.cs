using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Mcp;

/// <summary>
/// Unit tests for OrganizationMembershipService and ContainerAuthorizationService.
/// Placed in Mcp namespace alongside existing MCP tests for the org RBAC feature.
/// </summary>
public class OrganizationMembershipServiceTests : IDisposable
{
    private readonly Mock<ICurrentUserService> _mockCurrentUser;
    private readonly IMemoryCache _cache;
    private readonly OrganizationMembershipService _service;
    private readonly Guid _orgId = Guid.NewGuid();

    public OrganizationMembershipServiceTests()
    {
        _mockCurrentUser = new Mock<ICurrentUserService>();
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("user-1");
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(false);
        _cache = new MemoryCache(new MemoryCacheOptions());
        _service = new OrganizationMembershipService(_mockCurrentUser.Object, _cache);
    }

    public void Dispose() => _cache.Dispose();

    [Fact]
    public async Task IsMemberAsync_WhenUserBelongsToOrg_ReturnsTrue()
    {
        _mockCurrentUser.Setup(u => u.GetOrganizationId()).Returns(_orgId);
        var result = await _service.IsMemberAsync("user-1", _orgId);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsMemberAsync_WhenUserDoesNotBelongToOrg_ReturnsFalse()
    {
        _mockCurrentUser.Setup(u => u.GetOrganizationId()).Returns(Guid.NewGuid());
        var result = await _service.IsMemberAsync("user-1", _orgId);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsMemberAsync_WhenAdmin_AlwaysReturnsTrue()
    {
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        _mockCurrentUser.Setup(u => u.GetOrganizationId()).Returns((Guid?)null);
        var result = await _service.IsMemberAsync("admin-user", _orgId);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsMemberAsync_CachesResult_OnlyCallsGetOrganizationIdOnce()
    {
        _mockCurrentUser.Setup(u => u.GetOrganizationId()).Returns(_orgId);
        await _service.IsMemberAsync("user-1", _orgId);
        var result = await _service.IsMemberAsync("user-1", _orgId);
        result.Should().BeTrue();
        _mockCurrentUser.Verify(u => u.GetOrganizationId(), Times.Once);
    }

    [Fact]
    public async Task GetRoleAsync_WhenMember_ReturnsEditorByDefault()
    {
        _mockCurrentUser.Setup(u => u.GetOrganizationId()).Returns(_orgId);
        var role = await _service.GetRoleAsync("user-1", _orgId);
        role.Should().Be("org:editor");
    }

    [Fact]
    public async Task GetRoleAsync_WhenNotMember_ReturnsNull()
    {
        _mockCurrentUser.Setup(u => u.GetOrganizationId()).Returns(Guid.NewGuid());
        var role = await _service.GetRoleAsync("user-1", _orgId);
        role.Should().BeNull();
    }

    [Fact]
    public async Task GetRoleAsync_WhenAdmin_ReturnsOrgAdmin()
    {
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        var role = await _service.GetRoleAsync("admin-user", _orgId);
        role.Should().Be("org:admin");
    }

    [Fact]
    public async Task HasPermissionAsync_AdminCanDoAnything()
    {
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        var result = await _service.HasPermissionAsync("admin", _orgId, "image:delete");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasPermissionAsync_EditorCanCreateImages()
    {
        _mockCurrentUser.Setup(u => u.GetOrganizationId()).Returns(_orgId);
        var result = await _service.HasPermissionAsync("user-1", _orgId, "image:create");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasPermissionAsync_EditorCannotPublishImages()
    {
        _mockCurrentUser.Setup(u => u.GetOrganizationId()).Returns(_orgId);
        var result = await _service.HasPermissionAsync("user-1", _orgId, "image:publish");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasPermissionAsync_EditorCannotDeleteImages()
    {
        _mockCurrentUser.Setup(u => u.GetOrganizationId()).Returns(_orgId);
        var result = await _service.HasPermissionAsync("user-1", _orgId, "image:delete");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasPermissionAsync_CrossOrgDenied()
    {
        _mockCurrentUser.Setup(u => u.GetOrganizationId()).Returns(Guid.NewGuid());
        var result = await _service.HasPermissionAsync("user-1", _orgId, "image:read");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasPermissionAsync_CachesPermissionResult()
    {
        _mockCurrentUser.Setup(u => u.GetOrganizationId()).Returns(_orgId);
        await _service.HasPermissionAsync("user-1", _orgId, "image:read");
        var result = await _service.HasPermissionAsync("user-1", _orgId, "image:read");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task GetUserOrganizationsAsync_WhenHasOrg_ReturnsIt()
    {
        _mockCurrentUser.Setup(u => u.GetOrganizationId()).Returns(_orgId);
        var orgs = await _service.GetUserOrganizationsAsync("user-1");
        orgs.Should().ContainSingle().Which.Should().Be(_orgId);
    }

    [Fact]
    public async Task GetUserOrganizationsAsync_WhenNoOrg_ReturnsEmpty()
    {
        _mockCurrentUser.Setup(u => u.GetOrganizationId()).Returns((Guid?)null);
        var orgs = await _service.GetUserOrganizationsAsync("user-1");
        orgs.Should().BeEmpty();
    }
}

public class ContainerAuthorizationServiceTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<ICurrentUserService> _mockCurrentUser;
    private readonly Mock<IOrganizationMembershipService> _mockOrgMembership;
    private readonly ContainerAuthorizationService _service;
    private readonly Guid _orgId = Guid.NewGuid();

    public ContainerAuthorizationServiceTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockCurrentUser = new Mock<ICurrentUserService>();
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("user-1");
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(false);
        _mockOrgMembership = new Mock<IOrganizationMembershipService>();
        _service = new ContainerAuthorizationService(_mockCurrentUser.Object, _mockOrgMembership.Object, _db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CanAccessContainerAsync_AdminAlwaysTrue()
    {
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        var container = new Container { Name = "c1", OwnerId = "other-user" };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var result = await _service.CanAccessContainerAsync("admin", container.Id, "read");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanAccessContainerAsync_OwnerCanAccess()
    {
        var container = new Container { Name = "c1", OwnerId = "user-1" };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var result = await _service.CanAccessContainerAsync("user-1", container.Id, "read");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanAccessContainerAsync_NonOwnerDeniedWithoutOrg()
    {
        var container = new Container { Name = "c1", OwnerId = "other-user" };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var result = await _service.CanAccessContainerAsync("user-1", container.Id, "read");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanAccessContainerAsync_OrgMemberWithPermissionCanAccess()
    {
        var container = new Container { Name = "c1", OwnerId = "other-user", OrganizationId = _orgId };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();
        _mockOrgMembership.Setup(m => m.HasPermissionAsync("user-1", _orgId, "container:read", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await _service.CanAccessContainerAsync("user-1", container.Id, "read");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanAccessContainerAsync_NonExistentContainerReturnsFalse()
    {
        var result = await _service.CanAccessContainerAsync("user-1", Guid.NewGuid(), "read");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanAccessImageAsync_GlobalImageReadAllowed()
    {
        var template = new ContainerTemplate { Code = "t1", Name = "T1", Version = "1.0", BaseImage = "img" };
        _db.Templates.Add(template);
        var image = new ContainerImage
        {
            TemplateId = template.Id, OrganizationId = null,
            ContentHash = "sha256:g1", Tag = "t:1", ImageReference = "r", BaseImageDigest = "sha256:b",
            DependencyManifest = "{}", DependencyLock = "{}"
        };
        _db.Images.Add(image);
        await _db.SaveChangesAsync();

        var result = await _service.CanAccessImageAsync("user-1", image.Id, "read");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanAccessImageAsync_GlobalImageWriteDenied()
    {
        var template = new ContainerTemplate { Code = "t2", Name = "T2", Version = "1.0", BaseImage = "img" };
        _db.Templates.Add(template);
        var image = new ContainerImage
        {
            TemplateId = template.Id, OrganizationId = null,
            ContentHash = "sha256:g2", Tag = "t:2", ImageReference = "r", BaseImageDigest = "sha256:b",
            DependencyManifest = "{}", DependencyLock = "{}"
        };
        _db.Images.Add(image);
        await _db.SaveChangesAsync();

        var result = await _service.CanAccessImageAsync("user-1", image.Id, "write");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanAccessImageAsync_OrgImageWithPermission()
    {
        var template = new ContainerTemplate { Code = "t3", Name = "T3", Version = "1.0", BaseImage = "img" };
        _db.Templates.Add(template);
        var image = new ContainerImage
        {
            TemplateId = template.Id, OrganizationId = _orgId,
            ContentHash = "sha256:o1", Tag = "t:o1", ImageReference = "r", BaseImageDigest = "sha256:b",
            DependencyManifest = "{}", DependencyLock = "{}"
        };
        _db.Images.Add(image);
        await _db.SaveChangesAsync();
        _mockOrgMembership.Setup(m => m.HasPermissionAsync("user-1", _orgId, "image:read", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await _service.CanAccessImageAsync("user-1", image.Id, "read");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanManageOrgResourceAsync_AdminAlwaysTrue()
    {
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        var result = await _service.CanManageOrgResourceAsync("admin", _orgId, "template", "manage");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanManageOrgResourceAsync_DelegatesToOrgMembership()
    {
        _mockOrgMembership.Setup(m => m.HasPermissionAsync("user-1", _orgId, "template:manage", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var result = await _service.CanManageOrgResourceAsync("user-1", _orgId, "template", "manage");
        result.Should().BeTrue();
    }
}
