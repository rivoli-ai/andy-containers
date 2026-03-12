using System.Text.Json;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class ImageManifestServiceTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly ImageManifestService _service;

    public ImageManifestServiceTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _service = new ImageManifestService(_db);
    }

    public void Dispose() => _db.Dispose();

    private async Task<ContainerImage> SeedImageWithManifest(ImageToolManifest? manifest = null)
    {
        var template = new ContainerTemplate
        {
            Code = "test", Name = "Test", Version = "1.0", BaseImage = "img"
        };
        _db.Templates.Add(template);

        var manifestJson = manifest is not null
            ? JsonSerializer.Serialize(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            : "{}";

        var image = new ContainerImage
        {
            TemplateId = template.Id,
            ContentHash = "sha256:original",
            Tag = "test:1",
            ImageReference = "andy/test:1",
            BaseImageDigest = "sha256:base",
            DependencyManifest = manifestJson,
            DependencyLock = "{}",
            BuildNumber = 1,
            BuildStatus = ImageBuildStatus.Succeeded
        };
        _db.Images.Add(image);
        await _db.SaveChangesAsync();
        return image;
    }

    [Fact]
    public async Task GetManifestAsync_EmptyManifest_ReturnsNull()
    {
        var image = await SeedImageWithManifest();

        var result = await _service.GetManifestAsync(image.Id);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetManifestAsync_NonExistentImage_ReturnsNull()
    {
        var result = await _service.GetManifestAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetManifestAsync_ValidManifest_ReturnsDeserialized()
    {
        var manifest = new ImageToolManifest
        {
            ImageContentHash = "sha256:abc",
            BaseImage = "ubuntu:24.04",
            BaseImageDigest = "sha256:base",
            Architecture = "amd64",
            OperatingSystem = new OsInfo { Name = "Ubuntu", Version = "24.04" },
            Tools = [new InstalledTool { Name = "dotnet-sdk", Version = "8.0.404", Type = DependencyType.Sdk }],
            IntrospectedAt = DateTime.UtcNow
        };
        var image = await SeedImageWithManifest(manifest);

        var result = await _service.GetManifestAsync(image.Id);

        result.Should().NotBeNull();
        result!.Architecture.Should().Be("amd64");
        result.Tools.Should().HaveCount(1);
        result.Tools[0].Name.Should().Be("dotnet-sdk");
    }

    [Fact]
    public async Task StoreManifestAsync_UpdatesImageManifestAndHash()
    {
        var image = await SeedImageWithManifest();
        var originalHash = image.ContentHash;

        var manifest = new ImageToolManifest
        {
            ImageContentHash = "sha256:new",
            BaseImage = "ubuntu:24.04",
            BaseImageDigest = "sha256:basedigest",
            Architecture = "arm64",
            OperatingSystem = new OsInfo { Name = "Ubuntu", Version = "24.04" },
            Tools =
            [
                new InstalledTool { Name = "python", Version = "3.12.8", Type = DependencyType.Runtime },
                new InstalledTool { Name = "node", Version = "20.18.1", Type = DependencyType.Runtime }
            ],
            IntrospectedAt = DateTime.UtcNow
        };

        var result = await _service.StoreManifestAsync(image.Id, manifest);

        result.Should().NotBeNull();
        var updated = await _db.Images.FindAsync(image.Id);
        updated!.ContentHash.Should().NotBe(originalHash);
        updated.ContentHash.Should().StartWith("sha256:");
        updated.DependencyManifest.Should().Contain("python");
    }

    [Fact]
    public async Task StoreManifestAsync_NonExistentImage_ThrowsKeyNotFound()
    {
        var manifest = new ImageToolManifest
        {
            ImageContentHash = "sha256:x",
            BaseImage = "img",
            BaseImageDigest = "sha256:b",
            Architecture = "amd64",
            OperatingSystem = new OsInfo { Name = "Ubuntu", Version = "24.04" },
            Tools = [],
            IntrospectedAt = DateTime.UtcNow
        };

        var act = () => _service.StoreManifestAsync(Guid.NewGuid(), manifest);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task DiffImagesAsync_ToolAdded_ReportsAddChange()
    {
        var manifest1 = new ImageToolManifest
        {
            ImageContentHash = "sha256:1",
            BaseImage = "img",
            BaseImageDigest = "sha256:base1",
            Architecture = "amd64",
            OperatingSystem = new OsInfo { Name = "Ubuntu", Version = "24.04" },
            Tools = [new InstalledTool { Name = "dotnet-sdk", Version = "8.0.404", Type = DependencyType.Sdk }],
            IntrospectedAt = DateTime.UtcNow
        };
        var manifest2 = new ImageToolManifest
        {
            ImageContentHash = "sha256:2",
            BaseImage = "img",
            BaseImageDigest = "sha256:base1",
            Architecture = "amd64",
            OperatingSystem = new OsInfo { Name = "Ubuntu", Version = "24.04" },
            Tools =
            [
                new InstalledTool { Name = "dotnet-sdk", Version = "8.0.404", Type = DependencyType.Sdk },
                new InstalledTool { Name = "python", Version = "3.12.8", Type = DependencyType.Runtime }
            ],
            IntrospectedAt = DateTime.UtcNow
        };

        var image1 = await SeedImageWithManifest(manifest1);
        var image2 = await SeedImageWithManifest(manifest2);

        var diff = await _service.DiffImagesAsync(image1.Id, image2.Id);

        diff.ToolChanges.Should().HaveCount(1);
        diff.ToolChanges[0].ChangeType.Should().Be("Added");
        diff.ToolChanges[0].Name.Should().Be("python");
        diff.ToolChanges[0].NewVersion.Should().Be("3.12.8");
    }

    [Fact]
    public async Task DiffImagesAsync_ToolVersionChanged_ReportsVersionChange()
    {
        var manifest1 = new ImageToolManifest
        {
            ImageContentHash = "sha256:1",
            BaseImage = "img",
            BaseImageDigest = "sha256:base",
            Architecture = "amd64",
            OperatingSystem = new OsInfo { Name = "Ubuntu", Version = "24.04" },
            Tools = [new InstalledTool { Name = "node", Version = "18.0.0", Type = DependencyType.Runtime }],
            IntrospectedAt = DateTime.UtcNow
        };
        var manifest2 = new ImageToolManifest
        {
            ImageContentHash = "sha256:2",
            BaseImage = "img",
            BaseImageDigest = "sha256:base",
            Architecture = "amd64",
            OperatingSystem = new OsInfo { Name = "Ubuntu", Version = "24.04" },
            Tools = [new InstalledTool { Name = "node", Version = "20.18.1", Type = DependencyType.Runtime }],
            IntrospectedAt = DateTime.UtcNow
        };

        var image1 = await SeedImageWithManifest(manifest1);
        var image2 = await SeedImageWithManifest(manifest2);

        var diff = await _service.DiffImagesAsync(image1.Id, image2.Id);

        diff.ToolChanges.Should().HaveCount(1);
        diff.ToolChanges[0].ChangeType.Should().Be("VersionChanged");
        diff.ToolChanges[0].PreviousVersion.Should().Be("18.0.0");
        diff.ToolChanges[0].NewVersion.Should().Be("20.18.1");
        diff.ToolChanges[0].Severity.Should().Be("Major");
    }

    [Fact]
    public async Task DiffImagesAsync_ToolRemoved_ReportsRemoveChange()
    {
        var manifest1 = new ImageToolManifest
        {
            ImageContentHash = "sha256:1",
            BaseImage = "img",
            BaseImageDigest = "sha256:base",
            Architecture = "amd64",
            OperatingSystem = new OsInfo { Name = "Ubuntu", Version = "24.04" },
            Tools =
            [
                new InstalledTool { Name = "dotnet-sdk", Version = "8.0.404", Type = DependencyType.Sdk },
                new InstalledTool { Name = "go", Version = "1.22.5", Type = DependencyType.Runtime }
            ],
            IntrospectedAt = DateTime.UtcNow
        };
        var manifest2 = new ImageToolManifest
        {
            ImageContentHash = "sha256:2",
            BaseImage = "img",
            BaseImageDigest = "sha256:base",
            Architecture = "amd64",
            OperatingSystem = new OsInfo { Name = "Ubuntu", Version = "24.04" },
            Tools = [new InstalledTool { Name = "dotnet-sdk", Version = "8.0.404", Type = DependencyType.Sdk }],
            IntrospectedAt = DateTime.UtcNow
        };

        var image1 = await SeedImageWithManifest(manifest1);
        var image2 = await SeedImageWithManifest(manifest2);

        var diff = await _service.DiffImagesAsync(image1.Id, image2.Id);

        diff.ToolChanges.Should().HaveCount(1);
        diff.ToolChanges[0].ChangeType.Should().Be("Removed");
        diff.ToolChanges[0].Name.Should().Be("go");
    }

    [Fact]
    public async Task FindImagesByToolAsync_FindsMatchingImages()
    {
        var manifest = new ImageToolManifest
        {
            ImageContentHash = "sha256:find",
            BaseImage = "img",
            BaseImageDigest = "sha256:base",
            Architecture = "amd64",
            OperatingSystem = new OsInfo { Name = "Ubuntu", Version = "24.04" },
            Tools = [new InstalledTool { Name = "python", Version = "3.12.8", Type = DependencyType.Runtime }],
            IntrospectedAt = DateTime.UtcNow
        };

        var image = await SeedImageWithManifest(manifest);

        var results = await _service.FindImagesByToolAsync("python");

        results.Should().HaveCount(1);
        results[0].Id.Should().Be(image.Id);
    }

    [Fact]
    public async Task FindImagesByToolAsync_WithMinVersion_FiltersCorrectly()
    {
        var manifest = new ImageToolManifest
        {
            ImageContentHash = "sha256:find",
            BaseImage = "img",
            BaseImageDigest = "sha256:base",
            Architecture = "amd64",
            OperatingSystem = new OsInfo { Name = "Ubuntu", Version = "24.04" },
            Tools = [new InstalledTool { Name = "python", Version = "3.10.0", Type = DependencyType.Runtime }],
            IntrospectedAt = DateTime.UtcNow
        };

        await SeedImageWithManifest(manifest);

        var results = await _service.FindImagesByToolAsync("python", minVersion: "3.12.0");

        results.Should().BeEmpty();
    }
}
