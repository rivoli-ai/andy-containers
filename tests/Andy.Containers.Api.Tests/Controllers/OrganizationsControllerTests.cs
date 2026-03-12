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
    private readonly Mock<IOrganizationMembershipService> _mockOrgMembership;
    private readonly Mock<IContainerAuthorizationService> _mockAuthService;
    private readonly Mock<ICurrentUserService> _mockCurrentUser;
    private readonly OrganizationsController _controller;
    private readonly Guid _orgId = Guid.NewGuid();

    public OrganizationsControllerTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockOrgMembership = new Mock<IOrganizationMembershipService>();
        _mockAuthService = new Mock<IContainerAuthorizationService>();
        _mockCurrentUser = new Mock<ICurrentUserService>();
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(false);
        _controller = new OrganizationsController(_db, _mockOrgMembership.Object, _mockAuthService.Object, _mockCurrentUser.Object);
    }

    public void Dispose() => _db.Dispose();

    // --- ListImages ---

    [Fact]
    public async Task ListImages_WhenMember_ReturnsOrgAndGlobalImages()
    {
        _mockOrgMembership.Setup(m => m.IsMemberAsync("test-user", _orgId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var template = new ContainerTemplate { Code = "t1", Name = "T1", Version = "1.0", BaseImage = "img" };
        _db.Templates.Add(template);
        _db.Images.AddRange(
            new ContainerImage { TemplateId = template.Id, OrganizationId = _orgId, ContentHash = "sha256:a", Tag = "t:1", ImageReference = "r1", BaseImageDigest = "sha256:b", DependencyManifest = "{}", DependencyLock = "{}" },
            new ContainerImage { TemplateId = template.Id, OrganizationId = null, ContentHash = "sha256:c", Tag = "t:2", ImageReference = "r2", BaseImageDigest = "sha256:d", DependencyManifest = "{}", DependencyLock = "{}" },
            new ContainerImage { TemplateId = template.Id, OrganizationId = Guid.NewGuid(), ContentHash = "sha256:e", Tag = "t:3", ImageReference = "r3", BaseImageDigest = "sha256:f", DependencyManifest = "{}", DependencyLock = "{}" }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.ListImages(_orgId, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var images = ok.Value.Should().BeAssignableTo<List<ContainerImage>>().Subject;
        images.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListImages_WhenNotMember_ReturnsForbid()
    {
        _mockOrgMembership.Setup(m => m.IsMemberAsync("test-user", _orgId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await _controller.ListImages(_orgId, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    // --- PublishImage ---

    [Fact]
    public async Task PublishImage_WhenAuthorized_SetsVisibility()
    {
        _mockOrgMembership.Setup(m => m.HasPermissionAsync("test-user", _orgId, "image:publish", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var template = new ContainerTemplate { Code = "t1", Name = "T1", Version = "1.0", BaseImage = "img" };
        _db.Templates.Add(template);
        var image = new ContainerImage
        {
            TemplateId = template.Id, OrganizationId = _orgId, Visibility = ImageVisibility.Global,
            ContentHash = "sha256:pub", Tag = "t:pub", ImageReference = "r", BaseImageDigest = "sha256:b",
            DependencyManifest = "{}", DependencyLock = "{}"
        };
        _db.Images.Add(image);
        await _db.SaveChangesAsync();

        var result = await _controller.PublishImage(_orgId, image.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var updated = await _db.Images.FindAsync(image.Id);
        updated!.Visibility.Should().Be(ImageVisibility.Organization);
    }

    [Fact]
    public async Task PublishImage_WhenNotAuthorized_ReturnsForbid()
    {
        _mockOrgMembership.Setup(m => m.HasPermissionAsync("test-user", _orgId, "image:publish", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await _controller.PublishImage(_orgId, Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task PublishImage_WhenImageNotFound_ReturnsNotFound()
    {
        _mockOrgMembership.Setup(m => m.HasPermissionAsync("test-user", _orgId, "image:publish", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await _controller.PublishImage(_orgId, Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    // --- DeleteImage ---

    [Fact]
    public async Task DeleteImage_WhenAuthorized_RemovesImage()
    {
        _mockOrgMembership.Setup(m => m.HasPermissionAsync("test-user", _orgId, "image:delete", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var template = new ContainerTemplate { Code = "t1", Name = "T1", Version = "1.0", BaseImage = "img" };
        _db.Templates.Add(template);
        var image = new ContainerImage
        {
            TemplateId = template.Id, OrganizationId = _orgId,
            ContentHash = "sha256:del", Tag = "t:del", ImageReference = "r", BaseImageDigest = "sha256:b",
            DependencyManifest = "{}", DependencyLock = "{}"
        };
        _db.Images.Add(image);
        await _db.SaveChangesAsync();

        var result = await _controller.DeleteImage(_orgId, image.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await _db.Images.FindAsync(image.Id)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteImage_WhenNotAuthorized_ReturnsForbid()
    {
        _mockOrgMembership.Setup(m => m.HasPermissionAsync("test-user", _orgId, "image:delete", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await _controller.DeleteImage(_orgId, Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    // --- ListTemplates ---

    [Fact]
    public async Task ListTemplates_WhenMember_ReturnsOrgAndGlobalTemplates()
    {
        _mockOrgMembership.Setup(m => m.IsMemberAsync("test-user", _orgId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        _db.Templates.AddRange(
            new ContainerTemplate { Code = "org-t", Name = "Org Template", Version = "1.0", BaseImage = "img", OrganizationId = _orgId, IsPublished = true, CatalogScope = CatalogScope.Organization },
            new ContainerTemplate { Code = "global-t", Name = "Global Template", Version = "1.0", BaseImage = "img", IsPublished = true, CatalogScope = CatalogScope.Global },
            new ContainerTemplate { Code = "other-org", Name = "Other Org", Version = "1.0", BaseImage = "img", OrganizationId = Guid.NewGuid(), IsPublished = true, CatalogScope = CatalogScope.Organization }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.ListTemplates(_orgId, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var templates = ok.Value.Should().BeAssignableTo<List<ContainerTemplate>>().Subject;
        templates.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListTemplates_WhenNotMember_ReturnsForbid()
    {
        _mockOrgMembership.Setup(m => m.IsMemberAsync("test-user", _orgId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await _controller.ListTemplates(_orgId, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    // --- ListProviders ---

    [Fact]
    public async Task ListProviders_WhenMember_ReturnsOrgAndGlobalProviders()
    {
        _mockOrgMembership.Setup(m => m.IsMemberAsync("test-user", _orgId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        _db.Providers.AddRange(
            new InfrastructureProvider { Code = "org-p", Name = "Org Provider", Type = ProviderType.Docker, OrganizationId = _orgId },
            new InfrastructureProvider { Code = "global-p", Name = "Global Provider", Type = ProviderType.Docker, OrganizationId = null },
            new InfrastructureProvider { Code = "other-p", Name = "Other Org Provider", Type = ProviderType.Docker, OrganizationId = Guid.NewGuid() }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.ListProviders(_orgId, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var providers = ok.Value.Should().BeAssignableTo<List<InfrastructureProvider>>().Subject;
        providers.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListProviders_WhenNotMember_ReturnsForbid()
    {
        _mockOrgMembership.Setup(m => m.IsMemberAsync("test-user", _orgId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await _controller.ListProviders(_orgId, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }
}
