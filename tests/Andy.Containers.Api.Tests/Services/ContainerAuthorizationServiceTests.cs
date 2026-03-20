using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class ContainerAuthorizationServiceTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<ICurrentUserService> _mockCurrentUser;
    private readonly Mock<IOrganizationMembershipService> _mockOrgMembership;
    private readonly ContainerAuthorizationService _service;

    private static readonly Guid TestOrgId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherOrgId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public ContainerAuthorizationServiceTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockCurrentUser = new Mock<ICurrentUserService>();
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(false);
        _mockOrgMembership = new Mock<IOrganizationMembershipService>();
        var logger = new Mock<ILogger<ContainerAuthorizationService>>();
        _service = new ContainerAuthorizationService(_db, _mockCurrentUser.Object, _mockOrgMembership.Object, logger.Object);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CanAccessContainerAsync_AdminUser_ShouldAlwaysReturnTrue()
    {
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);

        var result = await _service.CanAccessContainerAsync("test-user", Guid.NewGuid(), "read");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanAccessContainerAsync_OwnerUser_ShouldReturnTrue()
    {
        var container = new Container { Name = "test", OwnerId = "test-user", OrganizationId = TestOrgId };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var result = await _service.CanAccessContainerAsync("test-user", container.Id, "read");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanAccessContainerAsync_NonOwnerNonMember_ShouldReturnFalse()
    {
        var container = new Container { Name = "test", OwnerId = "other-user", OrganizationId = TestOrgId };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();
        _mockOrgMembership.Setup(o => o.IsMemberAsync("test-user", TestOrgId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await _service.CanAccessContainerAsync("test-user", container.Id, "read");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanAccessContainerAsync_OrgMember_ShouldReturnTrue()
    {
        var container = new Container { Name = "test", OwnerId = "other-user", OrganizationId = TestOrgId };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();
        _mockOrgMembership.Setup(o => o.IsMemberAsync("test-user", TestOrgId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await _service.CanAccessContainerAsync("test-user", container.Id, "read");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanAccessContainerAsync_NonExistentContainer_ShouldReturnFalse()
    {
        var result = await _service.CanAccessContainerAsync("test-user", Guid.NewGuid(), "read");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanAccessTemplateAsync_GlobalTemplate_ReadAction_ShouldReturnTrue()
    {
        var template = new ContainerTemplate
        {
            Code = "global", Name = "Global", Version = "1.0", BaseImage = "img",
            CatalogScope = CatalogScope.Global, OwnerId = "other-user"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var result = await _service.CanAccessTemplateAsync("test-user", template.Id, "read");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanAccessTemplateAsync_GlobalTemplate_ManageAction_ShouldReturnFalse()
    {
        var template = new ContainerTemplate
        {
            Code = "global", Name = "Global", Version = "1.0", BaseImage = "img",
            CatalogScope = CatalogScope.Global, OwnerId = "other-user"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var result = await _service.CanAccessTemplateAsync("test-user", template.Id, "manage");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanAccessTemplateAsync_OrgTemplate_WithPermission_ShouldReturnTrue()
    {
        var template = new ContainerTemplate
        {
            Code = "org-tmpl", Name = "Org Template", Version = "1.0", BaseImage = "img",
            CatalogScope = CatalogScope.Organization, OrganizationId = TestOrgId, OwnerId = "other-user"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();
        _mockOrgMembership.Setup(o => o.HasPermissionAsync("test-user", TestOrgId, "template:read", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await _service.CanAccessTemplateAsync("test-user", template.Id, "read");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanAccessImageAsync_GlobalImage_ReadAction_ShouldReturnTrue()
    {
        var image = new ContainerImage
        {
            ContentHash = "sha256:test", Tag = "test:1", ImageReference = "test:1",
            BaseImageDigest = "sha256:base", DependencyManifest = "{}", DependencyLock = "{}",
            Visibility = ImageVisibility.Global, OwnerId = "other-user"
        };
        _db.Images.Add(image);
        await _db.SaveChangesAsync();

        var result = await _service.CanAccessImageAsync("test-user", image.Id, "read");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanAccessImageAsync_OrgImage_NonMember_ShouldReturnFalse()
    {
        var image = new ContainerImage
        {
            ContentHash = "sha256:test", Tag = "test:1", ImageReference = "test:1",
            BaseImageDigest = "sha256:base", DependencyManifest = "{}", DependencyLock = "{}",
            Visibility = ImageVisibility.Organization, OrganizationId = TestOrgId, OwnerId = "other-user"
        };
        _db.Images.Add(image);
        await _db.SaveChangesAsync();
        _mockOrgMembership.Setup(o => o.HasPermissionAsync("test-user", TestOrgId, "image:read", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await _service.CanAccessImageAsync("test-user", image.Id, "read");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanAccessImageAsync_OwnerImage_ShouldReturnTrue()
    {
        var image = new ContainerImage
        {
            ContentHash = "sha256:test", Tag = "test:1", ImageReference = "test:1",
            BaseImageDigest = "sha256:base", DependencyManifest = "{}", DependencyLock = "{}",
            Visibility = ImageVisibility.Organization, OrganizationId = TestOrgId, OwnerId = "test-user"
        };
        _db.Images.Add(image);
        await _db.SaveChangesAsync();

        var result = await _service.CanAccessImageAsync("test-user", image.Id, "read");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanManageOrgResourceAsync_Admin_ShouldReturnTrue()
    {
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);

        var result = await _service.CanManageOrgResourceAsync("test-user", TestOrgId, "image", "delete");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanManageOrgResourceAsync_WithPermission_ShouldReturnTrue()
    {
        _mockOrgMembership.Setup(o => o.HasPermissionAsync("test-user", TestOrgId, "image:delete", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await _service.CanManageOrgResourceAsync("test-user", TestOrgId, "image", "delete");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanManageOrgResourceAsync_WithoutPermission_ShouldReturnFalse()
    {
        _mockOrgMembership.Setup(o => o.HasPermissionAsync("test-user", TestOrgId, "image:delete", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await _service.CanManageOrgResourceAsync("test-user", TestOrgId, "image", "delete");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CrossOrgAccess_ShouldBeDenied()
    {
        var image = new ContainerImage
        {
            ContentHash = "sha256:test", Tag = "test:1", ImageReference = "test:1",
            BaseImageDigest = "sha256:base", DependencyManifest = "{}", DependencyLock = "{}",
            Visibility = ImageVisibility.Organization, OrganizationId = OtherOrgId, OwnerId = "other-user"
        };
        _db.Images.Add(image);
        await _db.SaveChangesAsync();
        // User is member of TestOrgId but not OtherOrgId
        _mockOrgMembership.Setup(o => o.HasPermissionAsync("test-user", OtherOrgId, "image:read", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await _service.CanAccessImageAsync("test-user", image.Id, "read");

        result.Should().BeFalse();
    }
}
