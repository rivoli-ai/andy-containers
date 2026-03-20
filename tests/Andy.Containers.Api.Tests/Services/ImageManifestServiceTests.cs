using System.Text.Json;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class ImageManifestServiceTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IToolVersionDetector> _mockDetector;
    private readonly ImageManifestService _service;

    public ImageManifestServiceTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockDetector = new Mock<IToolVersionDetector>();
        var logger = new Mock<ILogger<ImageManifestService>>();
        _service = new ImageManifestService(_db, _mockDetector.Object, logger.Object);
    }

    public void Dispose() => _db.Dispose();

    private async Task<(ContainerTemplate Template, ContainerImage Image)> SeedTemplateAndImage()
    {
        var template = new ContainerTemplate
        {
            Code = "test",
            Name = "Test Template",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var image = new ContainerImage
        {
            TemplateId = template.Id,
            ContentHash = $"sha256:{Guid.NewGuid():N}",
            Tag = "test:1.0.0-1",
            ImageReference = "andy-containers/test:1.0.0",
            BaseImageDigest = "sha256:baseabc123",
            DependencyManifest = "{}",
            DependencyLock = "{}",
            BuildNumber = 1,
            BuildStatus = ImageBuildStatus.Building
        };
        _db.Images.Add(image);
        await _db.SaveChangesAsync();

        return (template, image);
    }

    private ImageToolManifest CreateSampleManifest()
    {
        return new ImageToolManifest
        {
            ImageContentHash = "",
            BaseImage = "",
            BaseImageDigest = "",
            Architecture = "amd64",
            OperatingSystem = new OsInfo
            {
                Name = "Ubuntu",
                Version = "24.04",
                Codename = "noble",
                KernelVersion = "6.5.0"
            },
            Tools = new List<InstalledTool>
            {
                new() { Name = "python", Version = "3.12.8", Type = DependencyType.Runtime },
                new() { Name = "git", Version = "2.43.0", Type = DependencyType.Tool }
            }
        };
    }

    [Fact]
    public async Task GenerateManifestAsync_ShouldCallDetectorWithImageReference()
    {
        var (_, image) = await SeedTemplateAndImage();
        _mockDetector.Setup(d => d.IntrospectImageAsync(
                image.ImageReference, It.IsAny<IReadOnlyList<DependencySpec>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleManifest());

        await _service.GenerateManifestAsync(image.Id);

        _mockDetector.Verify(d => d.IntrospectImageAsync(
            "andy-containers/test:1.0.0",
            It.IsAny<IReadOnlyList<DependencySpec>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateManifestAsync_ShouldComputeAndStoreContentHash()
    {
        var (_, image) = await SeedTemplateAndImage();
        _mockDetector.Setup(d => d.IntrospectImageAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<DependencySpec>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleManifest());

        var (manifest, updatedImage) = await _service.GenerateManifestAsync(image.Id);

        updatedImage.ContentHash.Should().StartWith("sha256:");
        updatedImage.ContentHash.Should().HaveLength(7 + 64);
        manifest.ImageContentHash.Should().Be(updatedImage.ContentHash);
    }

    [Fact]
    public async Task GenerateManifestAsync_ShouldSerializeManifestToJson()
    {
        var (_, image) = await SeedTemplateAndImage();
        _mockDetector.Setup(d => d.IntrospectImageAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<DependencySpec>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleManifest());

        await _service.GenerateManifestAsync(image.Id);

        var saved = await _db.Images.FindAsync(image.Id);
        saved!.DependencyManifest.Should().NotBe("{}");
        var parsed = JsonDocument.Parse(saved.DependencyManifest);
        parsed.RootElement.GetProperty("tools").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task GenerateManifestAsync_ShouldCreateResolvedDependencies()
    {
        var (template, image) = await SeedTemplateAndImage();

        // Add a declared dependency
        _db.DependencySpecs.Add(new DependencySpec
        {
            TemplateId = template.Id,
            Name = "python",
            VersionConstraint = ">=3.12,<4.0",
            Type = DependencyType.Runtime
        });
        await _db.SaveChangesAsync();

        _mockDetector.Setup(d => d.IntrospectImageAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<DependencySpec>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleManifest());

        await _service.GenerateManifestAsync(image.Id);

        var resolved = await _db.ResolvedDependencies
            .Where(r => r.ImageId == image.Id)
            .ToListAsync();

        resolved.Should().HaveCount(2);
        resolved.Should().Contain(r => r.ResolvedVersion == "3.12.8");
        resolved.Should().Contain(r => r.ResolvedVersion == "2.43.0");
    }

    [Fact]
    public async Task GenerateManifestAsync_ShouldLinkResolvedDepToDependencySpec()
    {
        var (template, image) = await SeedTemplateAndImage();
        var spec = new DependencySpec
        {
            TemplateId = template.Id,
            Name = "python",
            VersionConstraint = ">=3.12,<4.0",
            Type = DependencyType.Runtime
        };
        _db.DependencySpecs.Add(spec);
        await _db.SaveChangesAsync();

        _mockDetector.Setup(d => d.IntrospectImageAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<DependencySpec>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleManifest());

        await _service.GenerateManifestAsync(image.Id);

        var pythonResolved = await _db.ResolvedDependencies
            .SingleAsync(r => r.ImageId == image.Id && r.ResolvedVersion == "3.12.8");
        pythonResolved.DependencySpecId.Should().Be(spec.Id);
    }

    [Fact]
    public async Task GenerateManifestAsync_ShouldGenerateDependencyLock()
    {
        var (_, image) = await SeedTemplateAndImage();
        _mockDetector.Setup(d => d.IntrospectImageAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<DependencySpec>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleManifest());

        await _service.GenerateManifestAsync(image.Id);

        var saved = await _db.Images.FindAsync(image.Id);
        saved!.DependencyLock.Should().NotBe("{}");
        var parsed = JsonDocument.Parse(saved.DependencyLock);
        parsed.RootElement.GetProperty("lockVersion").GetInt32().Should().Be(1);
        parsed.RootElement.GetProperty("dependencies").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task GenerateManifestAsync_Deduplication_ShouldReturnExistingImage()
    {
        var (template, image) = await SeedTemplateAndImage();

        var sampleManifest = CreateSampleManifest();
        sampleManifest.BaseImageDigest = "sha256:baseabc123";
        _mockDetector.Setup(d => d.IntrospectImageAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<DependencySpec>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sampleManifest);

        // Generate manifest for first image
        var (manifest1, image1) = await _service.GenerateManifestAsync(image.Id);

        // Create a second image
        var image2 = new ContainerImage
        {
            TemplateId = template.Id,
            ContentHash = $"sha256:{Guid.NewGuid():N}",
            Tag = "test:1.0.0-2",
            ImageReference = "andy-containers/test:1.0.0",
            BaseImageDigest = "sha256:baseabc123",
            DependencyManifest = "{}",
            DependencyLock = "{}",
            BuildNumber = 2
        };
        _db.Images.Add(image2);
        await _db.SaveChangesAsync();

        // Generate manifest — should find duplicate
        var (manifest2, returnedImage) = await _service.GenerateManifestAsync(image2.Id);

        returnedImage.Id.Should().Be(image1.Id);
    }

    [Fact]
    public async Task GenerateManifestAsync_NonExistentImage_ShouldThrowKeyNotFound()
    {
        var act = () => _service.GenerateManifestAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GetManifestAsync_PopulatedManifest_ShouldDeserialize()
    {
        var (_, image) = await SeedTemplateAndImage();
        _mockDetector.Setup(d => d.IntrospectImageAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<DependencySpec>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleManifest());

        await _service.GenerateManifestAsync(image.Id);

        var manifest = await _service.GetManifestAsync(image.Id);

        manifest.Should().NotBeNull();
        manifest!.Tools.Should().HaveCount(2);
        manifest.Architecture.Should().Be("amd64");
    }

    [Fact]
    public async Task GetManifestAsync_EmptyManifest_ShouldReturnNull()
    {
        var (_, image) = await SeedTemplateAndImage();
        // image.DependencyManifest is "{}" by default

        var manifest = await _service.GetManifestAsync(image.Id);

        manifest.Should().BeNull();
    }

    [Fact]
    public async Task GetManifestAsync_NonExistentImage_ShouldReturnNull()
    {
        var manifest = await _service.GetManifestAsync(Guid.NewGuid());
        manifest.Should().BeNull();
    }

    [Fact]
    public async Task RefreshManifestAsync_ShouldRemoveOldAndCreateNewResolvedDeps()
    {
        var (_, image) = await SeedTemplateAndImage();
        _mockDetector.Setup(d => d.IntrospectImageAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<DependencySpec>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSampleManifest());

        // First generation
        await _service.GenerateManifestAsync(image.Id);
        var firstCount = await _db.ResolvedDependencies.CountAsync(r => r.ImageId == image.Id);
        firstCount.Should().Be(2);

        // Refresh with updated manifest (one tool removed)
        var updatedManifest = CreateSampleManifest();
        updatedManifest.Tools = new List<InstalledTool>
        {
            new() { Name = "python", Version = "3.13.0", Type = DependencyType.Runtime }
        };
        _mockDetector.Setup(d => d.IntrospectImageAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<DependencySpec>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedManifest);

        await _service.RefreshManifestAsync(image.Id);

        var refreshedCount = await _db.ResolvedDependencies.CountAsync(r => r.ImageId == image.Id);
        refreshedCount.Should().Be(1);

        var dep = await _db.ResolvedDependencies.SingleAsync(r => r.ImageId == image.Id);
        dep.ResolvedVersion.Should().Be("3.13.0");
    }
}
