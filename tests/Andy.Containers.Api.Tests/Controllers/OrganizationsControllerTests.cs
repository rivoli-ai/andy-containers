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

public class OrganizationsControllerTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<ICurrentUserService> _mockCurrentUser;
    private readonly Mock<IOrganizationMembershipService> _mockOrgMembership;
    private readonly OrganizationsController _controller;

    private static readonly Guid TestOrgId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public OrganizationsControllerTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockCurrentUser = new Mock<ICurrentUserService>();
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(false);
        _mockOrgMembership = new Mock<IOrganizationMembershipService>();
        _controller = new OrganizationsController(_db, _mockCurrentUser.Object, _mockOrgMembership.Object);
    }

    public void Dispose() => _db.Dispose();

    private ContainerImage SeedOrgImage(Guid orgId, int buildNumber = 1)
    {
        var image = new ContainerImage
        {
            ContentHash = $"sha256:{Guid.NewGuid():N}",
            Tag = $"test:{buildNumber}",
            ImageReference = $"registry/test:{buildNumber}",
            BaseImageDigest = "sha256:base",
            DependencyManifest = "{}",
            DependencyLock = "{}",
            BuildNumber = buildNumber,
            BuildStatus = ImageBuildStatus.Succeeded,
            OrganizationId = orgId,
            OwnerId = "test-user",
            Visibility = ImageVisibility.Organization
        };
        _db.Images.Add(image);
        _db.SaveChanges();
        return image;
    }

    // --- ListImages ---

    [Fact]
    public async Task ListImages_NonMember_ShouldReturnForbid()
    {
        _mockOrgMembership.Setup(o => o.HasPermissionAsync("test-user", TestOrgId, Permissions.ImageRead, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await _controller.ListImages(TestOrgId, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task ListImages_MemberWithPermission_ShouldReturnImages()
    {
        _mockOrgMembership.Setup(o => o.HasPermissionAsync("test-user", TestOrgId, Permissions.ImageRead, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        SeedOrgImage(TestOrgId, 1);
        SeedOrgImage(TestOrgId, 2);

        var result = await _controller.ListImages(TestOrgId, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var images = ok.Value.Should().BeAssignableTo<List<ContainerImage>>().Subject;
        images.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListImages_Admin_ShouldBypassPermissionCheck()
    {
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        SeedOrgImage(TestOrgId);

        var result = await _controller.ListImages(TestOrgId, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var images = ok.Value.Should().BeAssignableTo<List<ContainerImage>>().Subject;
        images.Should().HaveCount(1);
        // HasPermissionAsync should not be called for admin
        _mockOrgMembership.Verify(o => o.HasPermissionAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- PublishImage ---

    [Fact]
    public async Task PublishImage_WithoutPermission_ShouldReturnForbid()
    {
        _mockOrgMembership.Setup(o => o.HasPermissionAsync("test-user", TestOrgId, Permissions.ImagePublish, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var image = SeedOrgImage(TestOrgId);

        var result = await _controller.PublishImage(TestOrgId, image.Id, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task PublishImage_WithPermission_ShouldUpdateAndReturnOk()
    {
        _mockOrgMembership.Setup(o => o.HasPermissionAsync("test-user", TestOrgId, Permissions.ImagePublish, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var image = SeedOrgImage(TestOrgId);

        var result = await _controller.PublishImage(TestOrgId, image.Id, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var updated = ok.Value.Should().BeOfType<ContainerImage>().Subject;
        updated.Visibility.Should().Be(ImageVisibility.Organization);
    }

    [Fact]
    public async Task PublishImage_NonExistentImage_ShouldReturnNotFound()
    {
        _mockOrgMembership.Setup(o => o.HasPermissionAsync("test-user", TestOrgId, Permissions.ImagePublish, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await _controller.PublishImage(TestOrgId, Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    // --- DeleteImage ---

    [Fact]
    public async Task DeleteImage_WithoutPermission_ShouldReturnForbid()
    {
        _mockOrgMembership.Setup(o => o.HasPermissionAsync("test-user", TestOrgId, Permissions.ImageDelete, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var image = SeedOrgImage(TestOrgId);

        var result = await _controller.DeleteImage(TestOrgId, image.Id, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task DeleteImage_WithPermission_ShouldRemoveAndReturnNoContent()
    {
        _mockOrgMembership.Setup(o => o.HasPermissionAsync("test-user", TestOrgId, Permissions.ImageDelete, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var image = SeedOrgImage(TestOrgId);

        var result = await _controller.DeleteImage(TestOrgId, image.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        var found = await _db.Images.FindAsync(image.Id);
        found.Should().BeNull();
    }

    // --- ListTemplates ---

    [Fact]
    public async Task ListTemplates_MemberWithPermission_ShouldReturnOrgAndGlobalTemplates()
    {
        _mockOrgMembership.Setup(o => o.HasPermissionAsync("test-user", TestOrgId, Permissions.TemplateRead, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _db.Templates.AddRange(
            new ContainerTemplate { Code = "org-tmpl", Name = "Org Template", Version = "1.0", BaseImage = "img", OrganizationId = TestOrgId, CatalogScope = CatalogScope.Organization, IsPublished = true },
            new ContainerTemplate { Code = "global-tmpl", Name = "Global Template", Version = "1.0", BaseImage = "img", CatalogScope = CatalogScope.Global, IsPublished = true },
            new ContainerTemplate { Code = "other-org-tmpl", Name = "Other Org Template", Version = "1.0", BaseImage = "img", OrganizationId = Guid.NewGuid(), CatalogScope = CatalogScope.Organization, IsPublished = true }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.ListTemplates(TestOrgId, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var templates = ok.Value.Should().BeAssignableTo<List<ContainerTemplate>>().Subject;
        templates.Should().HaveCount(2); // org + global, not other org
    }

    [Fact]
    public async Task ListTemplates_NonMember_ShouldReturnForbid()
    {
        _mockOrgMembership.Setup(o => o.HasPermissionAsync("test-user", TestOrgId, Permissions.TemplateRead, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await _controller.ListTemplates(TestOrgId, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    // --- ListProviders ---

    [Fact]
    public async Task ListProviders_MemberWithPermission_ShouldReturnOrgAndGlobalProviders()
    {
        _mockOrgMembership.Setup(o => o.HasPermissionAsync("test-user", TestOrgId, Permissions.ProviderRead, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _db.Providers.AddRange(
            new InfrastructureProvider { Code = "org-docker", Name = "Org Docker", Type = ProviderType.Docker, OrganizationId = TestOrgId },
            new InfrastructureProvider { Code = "global-docker", Name = "Global Docker", Type = ProviderType.Docker },
            new InfrastructureProvider { Code = "other-docker", Name = "Other Docker", Type = ProviderType.Docker, OrganizationId = Guid.NewGuid() }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.ListProviders(TestOrgId, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var providers = ok.Value.Should().BeAssignableTo<List<InfrastructureProvider>>().Subject;
        providers.Should().HaveCount(2); // org + global, not other org
    }

    [Fact]
    public async Task ListProviders_NonMember_ShouldReturnForbid()
    {
        _mockOrgMembership.Setup(o => o.HasPermissionAsync("test-user", TestOrgId, Permissions.ProviderRead, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await _controller.ListProviders(TestOrgId, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }
}
