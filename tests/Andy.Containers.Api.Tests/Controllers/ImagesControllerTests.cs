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

public class ImagesControllerTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IImageManifestService> _mockManifest;
    private readonly ImagesController _controller;

    public ImagesControllerTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockManifest = new Mock<IImageManifestService>();
        _controller = new ImagesController(_db, _mockManifest.Object);
    }

    public void Dispose() => _db.Dispose();

    private async Task<ContainerTemplate> SeedTemplate(string code = "test-template")
    {
        var template = new ContainerTemplate
        {
            Code = code,
            Name = "Test Template",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();
        return template;
    }

    private async Task<ContainerImage> SeedImage(Guid templateId, int buildNumber = 1,
        ImageBuildStatus status = ImageBuildStatus.Succeeded)
    {
        var image = new ContainerImage
        {
            TemplateId = templateId,
            ContentHash = $"sha256:{Guid.NewGuid():N}",
            Tag = $"test:{buildNumber}",
            ImageReference = $"andy/test:{buildNumber}",
            BaseImageDigest = $"sha256:{Guid.NewGuid():N}",
            DependencyManifest = "{}",
            DependencyLock = "{}",
            BuildNumber = buildNumber,
            BuildStatus = status,
            BuildStartedAt = DateTime.UtcNow,
            BuildCompletedAt = DateTime.UtcNow
        };
        _db.Images.Add(image);
        await _db.SaveChangesAsync();
        return image;
    }

    [Fact]
    public async Task List_ReturnsImagesForTemplate()
    {
        var template = await SeedTemplate();
        await SeedImage(template.Id, 1);
        await SeedImage(template.Id, 2);

        var result = await _controller.List(template.Id, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var images = ok.Value.Should().BeAssignableTo<List<ContainerImage>>().Subject;
        images.Should().HaveCount(2);
    }

    [Fact]
    public async Task List_ReturnsEmptyForUnknownTemplate()
    {
        var result = await _controller.List(Guid.NewGuid(), CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var images = ok.Value.Should().BeAssignableTo<List<ContainerImage>>().Subject;
        images.Should().BeEmpty();
    }

    [Fact]
    public async Task GetLatest_ReturnsLatestSucceededImage()
    {
        var template = await SeedTemplate();
        await SeedImage(template.Id, 1);
        var latest = await SeedImage(template.Id, 2);
        await SeedImage(template.Id, 3, ImageBuildStatus.Failed);

        var result = await _controller.GetLatest(template.Id, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var image = ok.Value.Should().BeOfType<ContainerImage>().Subject;
        image.BuildNumber.Should().Be(2);
    }

    [Fact]
    public async Task GetLatest_NoSucceededImages_ReturnsNotFound()
    {
        var template = await SeedTemplate();
        await SeedImage(template.Id, 1, ImageBuildStatus.Failed);

        var result = await _controller.GetLatest(template.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Build_ValidTemplate_ReturnsAccepted()
    {
        var template = await SeedTemplate();

        var result = await _controller.Build(template.Id, null, CancellationToken.None);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        var image = accepted.Value.Should().BeOfType<ContainerImage>().Subject;
        image.TemplateId.Should().Be(template.Id);
        image.BuildNumber.Should().Be(1);
        image.BuildStatus.Should().Be(ImageBuildStatus.Succeeded);
    }

    [Fact]
    public async Task Build_NonExistentTemplate_ReturnsNotFound()
    {
        var result = await _controller.Build(Guid.NewGuid(), null, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetManifest_ExistingImageWithManifest_ReturnsManifest()
    {
        var template = await SeedTemplate();
        var image = await SeedImage(template.Id);

        var manifest = new ImageToolManifest
        {
            ImageContentHash = image.ContentHash,
            BaseImage = image.ImageReference,
            BaseImageDigest = image.BaseImageDigest,
            Architecture = "amd64",
            OperatingSystem = new OsInfo { Name = "Ubuntu", Version = "24.04" },
            Tools = [new InstalledTool { Name = "dotnet-sdk", Version = "8.0.404", Type = DependencyType.Sdk }],
            IntrospectedAt = DateTime.UtcNow
        };
        _mockManifest.Setup(m => m.GetManifestAsync(image.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(manifest);

        var result = await _controller.GetManifest(image.Id, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<ImageToolManifest>();
    }

    [Fact]
    public async Task GetManifest_NoManifest_ReturnsMessageObject()
    {
        var template = await SeedTemplate();
        var image = await SeedImage(template.Id);
        _mockManifest.Setup(m => m.GetManifestAsync(image.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ImageToolManifest?)null);

        var result = await _controller.GetManifest(image.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetManifest_NonExistentImage_ReturnsNotFound()
    {
        var result = await _controller.GetManifest(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetTools_WithManifest_ReturnsToolList()
    {
        var template = await SeedTemplate();
        var image = await SeedImage(template.Id);

        var manifest = new ImageToolManifest
        {
            ImageContentHash = image.ContentHash,
            BaseImage = image.ImageReference,
            BaseImageDigest = image.BaseImageDigest,
            Architecture = "amd64",
            OperatingSystem = new OsInfo { Name = "Ubuntu", Version = "24.04" },
            Tools =
            [
                new InstalledTool { Name = "dotnet-sdk", Version = "8.0.404", Type = DependencyType.Sdk },
                new InstalledTool { Name = "python", Version = "3.12.8", Type = DependencyType.Runtime }
            ],
            IntrospectedAt = DateTime.UtcNow
        };
        _mockManifest.Setup(m => m.GetManifestAsync(image.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(manifest);

        var result = await _controller.GetTools(image.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetTools_NonExistentImage_ReturnsNotFound()
    {
        var result = await _controller.GetTools(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetPackages_WithManifest_ReturnsPaginatedPackages()
    {
        var template = await SeedTemplate();
        var image = await SeedImage(template.Id);

        var packages = Enumerable.Range(1, 150).Select(i =>
            new InstalledPackage { Name = $"pkg-{i}", Version = $"1.0.{i}" }).ToList();

        var manifest = new ImageToolManifest
        {
            ImageContentHash = image.ContentHash,
            BaseImage = image.ImageReference,
            BaseImageDigest = image.BaseImageDigest,
            Architecture = "amd64",
            OperatingSystem = new OsInfo { Name = "Ubuntu", Version = "24.04" },
            Tools = [],
            OsPackages = packages,
            IntrospectedAt = DateTime.UtcNow
        };
        _mockManifest.Setup(m => m.GetManifestAsync(image.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(manifest);

        var result = await _controller.GetPackages(image.Id, page: 1, pageSize: 100);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Diff_BothImagesExist_ReturnsDiff()
    {
        var template = await SeedTemplate();
        var from = await SeedImage(template.Id, 1);
        var to = await SeedImage(template.Id, 2);

        var diffResult = new ImageDiffResult
        {
            FromImageId = from.Id,
            ToImageId = to.Id,
            ToolChanges = []
        };
        _mockManifest.Setup(m => m.DiffImagesAsync(from.Id, to.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(diffResult);

        var result = await _controller.Diff(from.Id, to.Id, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<ImageDiffResult>();
    }

    [Fact]
    public async Task Diff_MissingImage_ReturnsNotFound()
    {
        var template = await SeedTemplate();
        var image = await SeedImage(template.Id);

        var result = await _controller.Diff(image.Id, Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Introspect_ExistingImage_StoresManifest()
    {
        var template = await SeedTemplate();
        var image = await SeedImage(template.Id);

        var storedManifest = new ImageToolManifest
        {
            ImageContentHash = image.ContentHash,
            BaseImage = image.ImageReference,
            BaseImageDigest = image.BaseImageDigest,
            Architecture = "amd64",
            OperatingSystem = new OsInfo { Name = "Ubuntu", Version = "24.04" },
            Tools = [],
            IntrospectedAt = DateTime.UtcNow
        };
        _mockManifest.Setup(m => m.StoreManifestAsync(image.Id, It.IsAny<ImageToolManifest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(storedManifest);

        var result = await _controller.Introspect(image.Id, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<ImageToolManifest>();
        _mockManifest.Verify(m => m.StoreManifestAsync(image.Id, It.IsAny<ImageToolManifest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Introspect_NonExistentImage_ReturnsNotFound()
    {
        var result = await _controller.Introspect(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }
}
