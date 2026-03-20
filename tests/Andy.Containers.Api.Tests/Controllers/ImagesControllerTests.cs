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
    private readonly Mock<IImageManifestService> _mockManifestService;
    private readonly Mock<IImageDiffService> _mockDiffService;
    private readonly ImagesController _controller;

    public ImagesControllerTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockManifestService = new Mock<IImageManifestService>();
        _mockDiffService = new Mock<IImageDiffService>();
        _controller = new ImagesController(_db, _mockManifestService.Object, _mockDiffService.Object);
    }

    public void Dispose() => _db.Dispose();

    private ContainerTemplate SeedTemplate(string code = "full-stack")
    {
        var template = new ContainerTemplate
        {
            Code = code,
            Name = "Full Stack",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };
        _db.Templates.Add(template);
        _db.SaveChanges();
        return template;
    }

    private ContainerImage SeedImage(Guid templateId, int buildNumber = 1, ImageBuildStatus status = ImageBuildStatus.Succeeded)
    {
        var image = new ContainerImage
        {
            TemplateId = templateId,
            ContentHash = $"sha256:{Guid.NewGuid():N}",
            Tag = $"test:{buildNumber}",
            ImageReference = $"registry/test:{buildNumber}",
            BaseImageDigest = "sha256:base",
            DependencyManifest = "{}",
            DependencyLock = "{}",
            BuildNumber = buildNumber,
            BuildStatus = status
        };
        _db.Images.Add(image);
        _db.SaveChanges();
        return image;
    }

    private static ImageToolManifest CreateTestManifest()
    {
        return new ImageToolManifest
        {
            ImageContentHash = "sha256:test",
            BaseImage = "ubuntu:24.04",
            BaseImageDigest = "sha256:base",
            Architecture = "amd64",
            OperatingSystem = new OsInfo { Name = "Ubuntu", Version = "24.04", Codename = "noble", KernelVersion = "6.5.0" },
            Tools = [new InstalledTool { Name = "python", Version = "3.12.8", Type = DependencyType.Runtime }],
            OsPackages = [new InstalledPackage { Name = "curl", Version = "8.5.0" }]
        };
    }

    // --- List ---

    [Fact]
    public async Task List_ShouldReturnImagesForTemplate()
    {
        var template = SeedTemplate();
        SeedImage(template.Id, 1);
        SeedImage(template.Id, 2);

        var result = await _controller.List(template.Id, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var images = ok.Value.Should().BeAssignableTo<List<ContainerImage>>().Subject;
        images.Should().HaveCount(2);
        images[0].BuildNumber.Should().Be(2); // ordered desc
    }

    [Fact]
    public async Task List_NoImages_ShouldReturnEmptyList()
    {
        var result = await _controller.List(Guid.NewGuid(), CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var images = ok.Value.Should().BeAssignableTo<List<ContainerImage>>().Subject;
        images.Should().BeEmpty();
    }

    // --- GetLatest ---

    [Fact]
    public async Task GetLatest_ShouldReturnLatestSucceeded()
    {
        var template = SeedTemplate();
        SeedImage(template.Id, 1, ImageBuildStatus.Succeeded);
        var latest = SeedImage(template.Id, 2, ImageBuildStatus.Succeeded);
        SeedImage(template.Id, 3, ImageBuildStatus.Building);

        var result = await _controller.GetLatest(template.Id, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var image = ok.Value.Should().BeOfType<ContainerImage>().Subject;
        image.BuildNumber.Should().Be(2);
    }

    [Fact]
    public async Task GetLatest_NoSucceededImage_ShouldReturnNotFound()
    {
        var template = SeedTemplate();
        SeedImage(template.Id, 1, ImageBuildStatus.Building);

        var result = await _controller.GetLatest(template.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    // --- Build ---

    [Fact]
    public async Task Build_NonExistentTemplate_ShouldReturnNotFound()
    {
        var result = await _controller.Build(Guid.NewGuid(), null, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Build_WithIntrospection_ShouldReturnAccepted()
    {
        var template = SeedTemplate();
        var manifest = CreateTestManifest();

        _mockManifestService
            .Setup(s => s.GenerateManifestAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid imageId, CancellationToken _) =>
            {
                var img = _db.Images.Find(imageId)!;
                return (manifest, img);
            });

        var result = await _controller.Build(template.Id, new BuildRequest(Offline: false), CancellationToken.None);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        var image = accepted.Value.Should().BeOfType<ContainerImage>().Subject;
        image.BuildStatus.Should().Be(ImageBuildStatus.Succeeded);
        image.Changelog.Should().Be("Build with introspection");
    }

    [Fact]
    public async Task Build_IntrospectionFails_ShouldStillReturnAccepted()
    {
        var template = SeedTemplate();

        _mockManifestService
            .Setup(s => s.GenerateManifestAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Introspection failed"));

        var result = await _controller.Build(template.Id, null, CancellationToken.None);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        var image = accepted.Value.Should().BeOfType<ContainerImage>().Subject;
        image.BuildStatus.Should().Be(ImageBuildStatus.Succeeded);
        image.Changelog.Should().Contain("introspection unavailable");
    }

    [Fact]
    public async Task Build_ShouldIncrementBuildNumber()
    {
        var template = SeedTemplate();
        SeedImage(template.Id, 1);

        _mockManifestService
            .Setup(s => s.GenerateManifestAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("skip"));

        var result = await _controller.Build(template.Id, null, CancellationToken.None);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        var image = accepted.Value.Should().BeOfType<ContainerImage>().Subject;
        image.BuildNumber.Should().Be(2);
    }

    [Fact]
    public async Task Build_OfflineFlag_ShouldBePreserved()
    {
        var template = SeedTemplate();

        _mockManifestService
            .Setup(s => s.GenerateManifestAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("skip"));

        var result = await _controller.Build(template.Id, new BuildRequest(Offline: true), CancellationToken.None);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        var image = accepted.Value.Should().BeOfType<ContainerImage>().Subject;
        image.BuiltOffline.Should().BeTrue();
    }

    // --- Diff ---

    [Fact]
    public async Task Diff_ShouldReturnOkWithDiffResult()
    {
        var fromId = Guid.NewGuid();
        var toId = Guid.NewGuid();
        var diff = new ImageDiffResponse(fromId, toId, false, null, false, [], new PackageChangeSummary(0, 0, 0, 0), null);
        _mockDiffService.Setup(s => s.DiffAsync(fromId, toId, It.IsAny<CancellationToken>())).ReturnsAsync(diff);

        var result = await _controller.Diff(fromId, toId, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(diff);
    }

    [Fact]
    public async Task Diff_NonExistentImage_ShouldReturnNotFound()
    {
        _mockDiffService.Setup(s => s.DiffAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var result = await _controller.Diff(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    // --- GetManifest ---

    [Fact]
    public async Task GetManifest_ExistingImage_ShouldReturnManifest()
    {
        var template = SeedTemplate();
        var image = SeedImage(template.Id);
        var manifest = CreateTestManifest();
        _mockManifestService.Setup(s => s.GetManifestAsync(image.Id, It.IsAny<CancellationToken>())).ReturnsAsync(manifest);

        var result = await _controller.GetManifest(image.Id, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(manifest);
    }

    [Fact]
    public async Task GetManifest_NonExistentImage_ShouldReturnNotFound()
    {
        var result = await _controller.GetManifest(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetManifest_NoManifest_ShouldReturnNotFoundWithMessage()
    {
        var template = SeedTemplate();
        var image = SeedImage(template.Id);
        _mockManifestService.Setup(s => s.GetManifestAsync(image.Id, It.IsAny<CancellationToken>())).ReturnsAsync((ImageToolManifest?)null);

        var result = await _controller.GetManifest(image.Id, CancellationToken.None);

        var notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.Value.Should().Be("Image has not been introspected");
    }

    // --- GetTools ---

    [Fact]
    public async Task GetTools_ShouldReturnToolsList()
    {
        var imageId = Guid.NewGuid();
        var manifest = CreateTestManifest();
        _mockManifestService.Setup(s => s.GetManifestAsync(imageId, It.IsAny<CancellationToken>())).ReturnsAsync(manifest);

        var result = await _controller.GetTools(imageId, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var tools = ok.Value.Should().BeAssignableTo<IReadOnlyList<InstalledTool>>().Subject;
        tools.Should().HaveCount(1);
        tools[0].Name.Should().Be("python");
    }

    [Fact]
    public async Task GetTools_NoManifest_ShouldReturnNotFound()
    {
        _mockManifestService.Setup(s => s.GetManifestAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((ImageToolManifest?)null);

        var result = await _controller.GetTools(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    // --- GetPackages ---

    [Fact]
    public async Task GetPackages_ShouldReturnPackagesList()
    {
        var imageId = Guid.NewGuid();
        var manifest = CreateTestManifest();
        _mockManifestService.Setup(s => s.GetManifestAsync(imageId, It.IsAny<CancellationToken>())).ReturnsAsync(manifest);

        var result = await _controller.GetPackages(imageId, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var packages = ok.Value.Should().BeAssignableTo<IReadOnlyList<InstalledPackage>>().Subject;
        packages.Should().HaveCount(1);
        packages[0].Name.Should().Be("curl");
    }

    [Fact]
    public async Task GetPackages_NoManifest_ShouldReturnNotFound()
    {
        _mockManifestService.Setup(s => s.GetManifestAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((ImageToolManifest?)null);

        var result = await _controller.GetPackages(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    // --- Introspect ---

    [Fact]
    public async Task Introspect_ExistingImage_ShouldReturnOk()
    {
        var template = SeedTemplate();
        var image = SeedImage(template.Id);
        var manifest = CreateTestManifest();
        _mockManifestService.Setup(s => s.RefreshManifestAsync(image.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((manifest, image));

        var result = await _controller.Introspect(image.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Introspect_NonExistentImage_ShouldReturnNotFound()
    {
        var result = await _controller.Introspect(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Introspect_ServiceThrows_ShouldReturn500()
    {
        var template = SeedTemplate();
        var image = SeedImage(template.Id);
        _mockManifestService.Setup(s => s.RefreshManifestAsync(image.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Provider unavailable"));

        var result = await _controller.Introspect(image.Id, CancellationToken.None);

        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(500);
    }
}
