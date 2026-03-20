using Andy.Containers.Abstractions;
using Andy.Containers.Api.Mcp;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Mcp;

public class ContainersMcpToolsImageTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IImageManifestService> _mockManifestService;
    private readonly Mock<IImageDiffService> _mockDiffService;
    private readonly ContainersMcpTools _tools;

    public ContainersMcpToolsImageTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockManifestService = new Mock<IImageManifestService>();
        _mockDiffService = new Mock<IImageDiffService>();
        var mockCurrentUser = new Mock<ICurrentUserService>();
        mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        var mockOrgMembership = new Mock<IOrganizationMembershipService>();
        mockOrgMembership.Setup(o => o.IsMemberAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        mockOrgMembership.Setup(o => o.HasPermissionAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _tools = new ContainersMcpTools(
            _db,
            new Mock<IGitCloneService>().Object,
            new Mock<IGitCredentialService>().Object,
            _mockManifestService.Object,
            _mockDiffService.Object,
            mockCurrentUser.Object,
            mockOrgMembership.Object);
    }

    public void Dispose() => _db.Dispose();

    private static ImageToolManifest CreateTestManifest(int toolCount = 2, int packageCount = 3)
    {
        var tools = Enumerable.Range(1, toolCount).Select(i => new InstalledTool
        {
            Name = $"tool{i}",
            Version = $"{i}.0.0",
            Type = DependencyType.Runtime
        }).ToList();

        var packages = Enumerable.Range(1, packageCount).Select(i => new InstalledPackage
        {
            Name = $"pkg{i}",
            Version = $"1.{i}.0"
        }).ToList();

        return new ImageToolManifest
        {
            ImageContentHash = "sha256:test123",
            BaseImage = "ubuntu:24.04",
            BaseImageDigest = "sha256:base",
            Architecture = "amd64",
            OperatingSystem = new OsInfo
            {
                Name = "Ubuntu",
                Version = "24.04",
                Codename = "noble",
                KernelVersion = "6.5.0"
            },
            Tools = tools,
            OsPackages = packages
        };
    }

    [Fact]
    public async Task GetImageManifest_ValidImage_ShouldReturnManifestInfo()
    {
        var imageId = Guid.NewGuid();
        var manifest = CreateTestManifest();
        _mockManifestService.Setup(s => s.GetManifestAsync(imageId, It.IsAny<CancellationToken>())).ReturnsAsync(manifest);

        var result = await _tools.GetImageManifest(imageId.ToString());

        result.Should().NotBeNull();
        result!.ContentHash.Should().Be("sha256:test123");
        result.BaseImage.Should().Be("ubuntu:24.04");
        result.Architecture.Should().Be("amd64");
        result.OperatingSystem.Should().Be("Ubuntu 24.04");
        result.ToolCount.Should().Be(2);
        result.PackageCount.Should().Be(3);
    }

    [Fact]
    public async Task GetImageManifest_NoManifest_ShouldReturnNull()
    {
        var imageId = Guid.NewGuid();
        _mockManifestService.Setup(s => s.GetManifestAsync(imageId, It.IsAny<CancellationToken>())).ReturnsAsync((ImageToolManifest?)null);

        var result = await _tools.GetImageManifest(imageId.ToString());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetImageManifest_InvalidGuid_ShouldReturnNull()
    {
        var result = await _tools.GetImageManifest("not-a-guid");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetImageTools_ValidImage_ShouldReturnToolList()
    {
        var imageId = Guid.NewGuid();
        var manifest = CreateTestManifest(toolCount: 3);
        _mockManifestService.Setup(s => s.GetManifestAsync(imageId, It.IsAny<CancellationToken>())).ReturnsAsync(manifest);

        var result = await _tools.GetImageTools(imageId.ToString());

        result.Should().HaveCount(3);
        result[0].Name.Should().Be("tool1");
        result[0].Version.Should().Be("1.0.0");
        result[0].Type.Should().Be("Runtime");
    }

    [Fact]
    public async Task GetImageTools_NoManifest_ShouldReturnEmpty()
    {
        var imageId = Guid.NewGuid();
        _mockManifestService.Setup(s => s.GetManifestAsync(imageId, It.IsAny<CancellationToken>())).ReturnsAsync((ImageToolManifest?)null);

        var result = await _tools.GetImageTools(imageId.ToString());

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetImageTools_InvalidGuid_ShouldReturnEmpty()
    {
        var result = await _tools.GetImageTools("not-a-guid");
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task CompareImages_ShouldReturnDiffInfo()
    {
        var fromId = Guid.NewGuid();
        var toId = Guid.NewGuid();
        var diff = new ImageDiffResponse(
            fromId, toId,
            BaseImageChanged: true,
            OsVersionChanged: "23.10 → 24.04",
            ArchitectureChanged: false,
            ToolChanges:
            [
                new ToolChangeDto("python", "Runtime", "VersionChanged", "3.12.8", "3.13.0", "Minor"),
                new ToolChangeDto("node", "Runtime", "Added", null, "20.18.1", null)
            ],
            PackageChanges: new PackageChangeSummary(2, 1, 3, 0),
            SizeChange: "+120MB");
        _mockDiffService.Setup(s => s.DiffAsync(fromId, toId, It.IsAny<CancellationToken>())).ReturnsAsync(diff);

        var result = await _tools.CompareImages(fromId.ToString(), toId.ToString());

        result.Should().NotBeNull();
        result!.BaseImageChanged.Should().BeTrue();
        result.OsVersionChanged.Should().Be("23.10 → 24.04");
        result.ArchitectureChanged.Should().BeFalse();
        result.ToolChanges.Should().HaveCount(2);
        result.ToolChanges[0].Name.Should().Be("python");
        result.ToolChanges[0].ChangeType.Should().Be("VersionChanged");
        result.PackagesAdded.Should().Be(2);
        result.PackagesRemoved.Should().Be(1);
        result.PackagesUpgraded.Should().Be(3);
        result.SizeChange.Should().Be("+120MB");
    }

    [Fact]
    public async Task CompareImages_NonExistentImage_ShouldReturnNull()
    {
        var fromId = Guid.NewGuid();
        var toId = Guid.NewGuid();
        _mockDiffService.Setup(s => s.DiffAsync(fromId, toId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var result = await _tools.CompareImages(fromId.ToString(), toId.ToString());

        result.Should().BeNull();
    }

    [Fact]
    public async Task CompareImages_InvalidGuid_ShouldReturnNull()
    {
        var result = await _tools.CompareImages("not-a-guid", "also-not");
        result.Should().BeNull();
    }

    [Fact]
    public async Task FindImageByTool_ShouldFindImagesContainingTool()
    {
        var template = new ContainerTemplate
        {
            Code = "full-stack",
            Name = "Full Stack",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };
        _db.Templates.Add(template);
        _db.Images.Add(new ContainerImage
        {
            TemplateId = template.Id,
            ContentHash = "sha256:abc",
            Tag = "full-stack:1.0.0-1",
            ImageReference = "registry/full-stack:1.0.0-1",
            BaseImageDigest = "sha256:base",
            DependencyManifest = "{\"Tools\":[{\"Name\":\"python\",\"Version\":\"3.12.8\"}]}",
            DependencyLock = "{}",
            BuildNumber = 1,
            BuildStatus = ImageBuildStatus.Succeeded
        });
        _db.Images.Add(new ContainerImage
        {
            TemplateId = template.Id,
            ContentHash = "sha256:def",
            Tag = "full-stack:1.0.0-2",
            ImageReference = "registry/full-stack:1.0.0-2",
            BaseImageDigest = "sha256:base",
            DependencyManifest = "{\"Tools\":[{\"Name\":\"node\",\"Version\":\"20.18.1\"}]}",
            DependencyLock = "{}",
            BuildNumber = 2,
            BuildStatus = ImageBuildStatus.Succeeded
        });
        await _db.SaveChangesAsync();

        var result = await _tools.FindImageByTool("python", "full-stack");

        result.Should().HaveCount(1);
        result[0].Tag.Should().Be("full-stack:1.0.0-1");
    }

    [Fact]
    public async Task FindImageByTool_NonExistentTemplate_ShouldReturnEmpty()
    {
        var result = await _tools.FindImageByTool("python", "nonexistent");
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task FindImageByTool_NoTemplateFilter_ShouldSearchAll()
    {
        _db.Images.Add(new ContainerImage
        {
            ContentHash = "sha256:abc",
            Tag = "test:1",
            ImageReference = "test:1",
            BaseImageDigest = "sha256:base",
            DependencyManifest = "{\"Tools\":[{\"Name\":\"python\",\"Version\":\"3.12.8\"}]}",
            DependencyLock = "{}",
            BuildNumber = 1,
            BuildStatus = ImageBuildStatus.Succeeded
        });
        await _db.SaveChangesAsync();

        var result = await _tools.FindImageByTool("python");

        result.Should().HaveCount(1);
    }
}
