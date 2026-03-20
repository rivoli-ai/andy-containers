using System.Text.Json;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class ImageDiffServiceTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IImageManifestService> _mockManifestService;
    private readonly ImageDiffService _service;

    public ImageDiffServiceTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockManifestService = new Mock<IImageManifestService>();
        _service = new ImageDiffService(_db, _mockManifestService.Object);
    }

    public void Dispose() => _db.Dispose();

    private async Task<ContainerImage> SeedImage(string baseImageDigest = "sha256:base1", long? sizeBytes = null)
    {
        var image = new ContainerImage
        {
            ContentHash = $"sha256:{Guid.NewGuid():N}",
            Tag = "test:1.0.0",
            ImageReference = "andy/test:1.0.0",
            BaseImageDigest = baseImageDigest,
            DependencyManifest = "{}",
            DependencyLock = "{}",
            ImageSizeBytes = sizeBytes
        };
        _db.Images.Add(image);
        await _db.SaveChangesAsync();
        return image;
    }

    private static ImageToolManifest CreateManifest(
        string arch = "amd64",
        string osVersion = "24.04",
        (string Name, string Version, DependencyType Type)[]? tools = null,
        (string Name, string Version)[]? packages = null)
    {
        return new ImageToolManifest
        {
            ImageContentHash = "sha256:test",
            BaseImage = "ubuntu:24.04",
            BaseImageDigest = "sha256:base1",
            Architecture = arch,
            OperatingSystem = new OsInfo
            {
                Name = "Ubuntu",
                Version = osVersion,
                Codename = "noble",
                KernelVersion = "6.5.0"
            },
            Tools = (tools ?? []).Select(t => new InstalledTool
            {
                Name = t.Name, Version = t.Version, Type = t.Type
            }).ToList(),
            OsPackages = (packages ?? []).Select(p => new InstalledPackage
            {
                Name = p.Name, Version = p.Version
            }).ToList()
        };
    }

    [Fact]
    public async Task DiffAsync_IdenticalManifests_ShouldReturnNoChanges()
    {
        var from = await SeedImage();
        var to = await SeedImage();
        var manifest = CreateManifest(tools: [("python", "3.12.8", DependencyType.Runtime)]);
        _mockManifestService.Setup(s => s.GetManifestAsync(from.Id, It.IsAny<CancellationToken>())).ReturnsAsync(manifest);
        _mockManifestService.Setup(s => s.GetManifestAsync(to.Id, It.IsAny<CancellationToken>())).ReturnsAsync(manifest);

        var diff = await _service.DiffAsync(from.Id, to.Id);

        diff.ToolChanges.Should().BeEmpty();
        diff.BaseImageChanged.Should().BeFalse();
        diff.ArchitectureChanged.Should().BeFalse();
        diff.OsVersionChanged.Should().BeNull();
    }

    [Fact]
    public async Task DiffAsync_ToolAdded_ShouldDetect()
    {
        var from = await SeedImage();
        var to = await SeedImage();
        var fromManifest = CreateManifest(tools: [("python", "3.12.8", DependencyType.Runtime)]);
        var toManifest = CreateManifest(tools: [("python", "3.12.8", DependencyType.Runtime), ("node", "20.18.1", DependencyType.Runtime)]);
        _mockManifestService.Setup(s => s.GetManifestAsync(from.Id, It.IsAny<CancellationToken>())).ReturnsAsync(fromManifest);
        _mockManifestService.Setup(s => s.GetManifestAsync(to.Id, It.IsAny<CancellationToken>())).ReturnsAsync(toManifest);

        var diff = await _service.DiffAsync(from.Id, to.Id);

        diff.ToolChanges.Should().ContainSingle(c => c.Name == "node" && c.ChangeType == "Added");
        var added = diff.ToolChanges.Single(c => c.Name == "node");
        added.NewVersion.Should().Be("20.18.1");
        added.PreviousVersion.Should().BeNull();
        added.Severity.Should().BeNull();
    }

    [Fact]
    public async Task DiffAsync_ToolRemoved_ShouldDetect()
    {
        var from = await SeedImage();
        var to = await SeedImage();
        var fromManifest = CreateManifest(tools: [("python", "3.12.8", DependencyType.Runtime), ("node", "20.18.1", DependencyType.Runtime)]);
        var toManifest = CreateManifest(tools: [("python", "3.12.8", DependencyType.Runtime)]);
        _mockManifestService.Setup(s => s.GetManifestAsync(from.Id, It.IsAny<CancellationToken>())).ReturnsAsync(fromManifest);
        _mockManifestService.Setup(s => s.GetManifestAsync(to.Id, It.IsAny<CancellationToken>())).ReturnsAsync(toManifest);

        var diff = await _service.DiffAsync(from.Id, to.Id);

        diff.ToolChanges.Should().ContainSingle(c => c.Name == "node" && c.ChangeType == "Removed");
        var removed = diff.ToolChanges.Single(c => c.Name == "node");
        removed.PreviousVersion.Should().Be("20.18.1");
        removed.NewVersion.Should().BeNull();
        removed.Severity.Should().BeNull();
    }

    [Fact]
    public async Task DiffAsync_ToolVersionChanged_ShouldDetectWithSeverity()
    {
        var from = await SeedImage();
        var to = await SeedImage();
        var fromManifest = CreateManifest(tools: [("python", "3.12.8", DependencyType.Runtime)]);
        var toManifest = CreateManifest(tools: [("python", "3.13.0", DependencyType.Runtime)]);
        _mockManifestService.Setup(s => s.GetManifestAsync(from.Id, It.IsAny<CancellationToken>())).ReturnsAsync(fromManifest);
        _mockManifestService.Setup(s => s.GetManifestAsync(to.Id, It.IsAny<CancellationToken>())).ReturnsAsync(toManifest);

        var diff = await _service.DiffAsync(from.Id, to.Id);

        var change = diff.ToolChanges.Should().ContainSingle().Subject;
        change.Name.Should().Be("python");
        change.ChangeType.Should().Be("VersionChanged");
        change.PreviousVersion.Should().Be("3.12.8");
        change.NewVersion.Should().Be("3.13.0");
        change.Severity.Should().Be("Minor");
    }

    [Fact]
    public async Task DiffAsync_MajorVersionChange_ShouldClassifyAsMajor()
    {
        var from = await SeedImage();
        var to = await SeedImage();
        var fromManifest = CreateManifest(tools: [("node", "18.20.0", DependencyType.Runtime)]);
        var toManifest = CreateManifest(tools: [("node", "20.18.1", DependencyType.Runtime)]);
        _mockManifestService.Setup(s => s.GetManifestAsync(from.Id, It.IsAny<CancellationToken>())).ReturnsAsync(fromManifest);
        _mockManifestService.Setup(s => s.GetManifestAsync(to.Id, It.IsAny<CancellationToken>())).ReturnsAsync(toManifest);

        var diff = await _service.DiffAsync(from.Id, to.Id);

        diff.ToolChanges.Single().Severity.Should().Be("Major");
    }

    [Fact]
    public async Task DiffAsync_PatchVersionChange_ShouldClassifyAsPatch()
    {
        var from = await SeedImage();
        var to = await SeedImage();
        var fromManifest = CreateManifest(tools: [("dotnet-sdk", "8.0.400", DependencyType.Sdk)]);
        var toManifest = CreateManifest(tools: [("dotnet-sdk", "8.0.404", DependencyType.Sdk)]);
        _mockManifestService.Setup(s => s.GetManifestAsync(from.Id, It.IsAny<CancellationToken>())).ReturnsAsync(fromManifest);
        _mockManifestService.Setup(s => s.GetManifestAsync(to.Id, It.IsAny<CancellationToken>())).ReturnsAsync(toManifest);

        var diff = await _service.DiffAsync(from.Id, to.Id);

        diff.ToolChanges.Single().Severity.Should().Be("Patch");
    }

    [Fact]
    public async Task DiffAsync_MultipleChanges_ShouldDetectAll()
    {
        var from = await SeedImage();
        var to = await SeedImage();
        var fromManifest = CreateManifest(tools:
        [
            ("python", "3.12.8", DependencyType.Runtime),
            ("node", "20.18.1", DependencyType.Runtime),
            ("go", "1.22.5", DependencyType.Compiler)
        ]);
        var toManifest = CreateManifest(tools:
        [
            ("python", "3.13.0", DependencyType.Runtime),
            ("rust", "1.82.0", DependencyType.Compiler)
        ]);
        _mockManifestService.Setup(s => s.GetManifestAsync(from.Id, It.IsAny<CancellationToken>())).ReturnsAsync(fromManifest);
        _mockManifestService.Setup(s => s.GetManifestAsync(to.Id, It.IsAny<CancellationToken>())).ReturnsAsync(toManifest);

        var diff = await _service.DiffAsync(from.Id, to.Id);

        diff.ToolChanges.Should().HaveCount(4);
        diff.ToolChanges.Should().Contain(c => c.Name == "python" && c.ChangeType == "VersionChanged");
        diff.ToolChanges.Should().Contain(c => c.Name == "node" && c.ChangeType == "Removed");
        diff.ToolChanges.Should().Contain(c => c.Name == "go" && c.ChangeType == "Removed");
        diff.ToolChanges.Should().Contain(c => c.Name == "rust" && c.ChangeType == "Added");
    }

    [Fact]
    public async Task DiffAsync_BaseImageChanged_ShouldDetect()
    {
        var from = await SeedImage("sha256:old");
        var to = await SeedImage("sha256:new");
        var manifest = CreateManifest();
        _mockManifestService.Setup(s => s.GetManifestAsync(from.Id, It.IsAny<CancellationToken>())).ReturnsAsync(manifest);
        _mockManifestService.Setup(s => s.GetManifestAsync(to.Id, It.IsAny<CancellationToken>())).ReturnsAsync(manifest);

        var diff = await _service.DiffAsync(from.Id, to.Id);

        diff.BaseImageChanged.Should().BeTrue();
    }

    [Fact]
    public async Task DiffAsync_OsVersionChanged_ShouldDetect()
    {
        var from = await SeedImage();
        var to = await SeedImage();
        var fromManifest = CreateManifest(osVersion: "23.10");
        var toManifest = CreateManifest(osVersion: "24.04");
        _mockManifestService.Setup(s => s.GetManifestAsync(from.Id, It.IsAny<CancellationToken>())).ReturnsAsync(fromManifest);
        _mockManifestService.Setup(s => s.GetManifestAsync(to.Id, It.IsAny<CancellationToken>())).ReturnsAsync(toManifest);

        var diff = await _service.DiffAsync(from.Id, to.Id);

        diff.OsVersionChanged.Should().Be("23.10 → 24.04");
    }

    [Fact]
    public async Task DiffAsync_ArchitectureChanged_ShouldDetect()
    {
        var from = await SeedImage();
        var to = await SeedImage();
        var fromManifest = CreateManifest(arch: "amd64");
        var toManifest = CreateManifest(arch: "arm64");
        _mockManifestService.Setup(s => s.GetManifestAsync(from.Id, It.IsAny<CancellationToken>())).ReturnsAsync(fromManifest);
        _mockManifestService.Setup(s => s.GetManifestAsync(to.Id, It.IsAny<CancellationToken>())).ReturnsAsync(toManifest);

        var diff = await _service.DiffAsync(from.Id, to.Id);

        diff.ArchitectureChanged.Should().BeTrue();
    }

    [Fact]
    public async Task DiffAsync_PackageChanges_ShouldCountCorrectly()
    {
        var from = await SeedImage();
        var to = await SeedImage();
        var fromManifest = CreateManifest(packages:
        [
            ("libssl3", "3.0.13"),
            ("curl", "8.5.0"),
            ("removed-pkg", "1.0.0")
        ]);
        var toManifest = CreateManifest(packages:
        [
            ("libssl3", "3.0.14"),
            ("curl", "8.4.0"),
            ("new-pkg", "1.0.0")
        ]);
        _mockManifestService.Setup(s => s.GetManifestAsync(from.Id, It.IsAny<CancellationToken>())).ReturnsAsync(fromManifest);
        _mockManifestService.Setup(s => s.GetManifestAsync(to.Id, It.IsAny<CancellationToken>())).ReturnsAsync(toManifest);

        var diff = await _service.DiffAsync(from.Id, to.Id);

        diff.PackageChanges.Added.Should().Be(1);      // new-pkg
        diff.PackageChanges.Removed.Should().Be(1);     // removed-pkg
        diff.PackageChanges.Upgraded.Should().Be(1);     // libssl3 3.0.13 → 3.0.14
        diff.PackageChanges.Downgraded.Should().Be(1);   // curl 8.5.0 → 8.4.0
    }

    [Fact]
    public async Task DiffAsync_SizeChange_ShouldCompute()
    {
        var from = await SeedImage(sizeBytes: 500 * 1024 * 1024L);
        var to = await SeedImage(sizeBytes: 620 * 1024 * 1024L);
        var manifest = CreateManifest();
        _mockManifestService.Setup(s => s.GetManifestAsync(from.Id, It.IsAny<CancellationToken>())).ReturnsAsync(manifest);
        _mockManifestService.Setup(s => s.GetManifestAsync(to.Id, It.IsAny<CancellationToken>())).ReturnsAsync(manifest);

        var diff = await _service.DiffAsync(from.Id, to.Id);

        diff.SizeChange.Should().StartWith("+");
        diff.SizeChange.Should().Contain("MB");
    }

    [Fact]
    public async Task DiffAsync_MissingManifest_ShouldReturnWarning()
    {
        var from = await SeedImage();
        var to = await SeedImage();
        _mockManifestService.Setup(s => s.GetManifestAsync(from.Id, It.IsAny<CancellationToken>())).ReturnsAsync((ImageToolManifest?)null);
        _mockManifestService.Setup(s => s.GetManifestAsync(to.Id, It.IsAny<CancellationToken>())).ReturnsAsync((ImageToolManifest?)null);

        var diff = await _service.DiffAsync(from.Id, to.Id);

        diff.Warning.Should().Contain("not been introspected");
        diff.ToolChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task DiffAsync_NonExistentImage_ShouldThrow()
    {
        var act = () => _service.DiffAsync(Guid.NewGuid(), Guid.NewGuid());

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task DiffAsync_ToolChanges_ShouldBeSortedByName()
    {
        var from = await SeedImage();
        var to = await SeedImage();
        var fromManifest = CreateManifest(tools: [("python", "3.12.8", DependencyType.Runtime)]);
        var toManifest = CreateManifest(tools:
        [
            ("python", "3.13.0", DependencyType.Runtime),
            ("curl", "8.6.0", DependencyType.Tool),
            ("git", "2.44.0", DependencyType.Tool)
        ]);
        _mockManifestService.Setup(s => s.GetManifestAsync(from.Id, It.IsAny<CancellationToken>())).ReturnsAsync(fromManifest);
        _mockManifestService.Setup(s => s.GetManifestAsync(to.Id, It.IsAny<CancellationToken>())).ReturnsAsync(toManifest);

        var diff = await _service.DiffAsync(from.Id, to.Id);

        diff.ToolChanges.Select(c => c.Name).Should().BeInAscendingOrder();
    }
}
