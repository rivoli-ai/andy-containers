using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Andy.Containers.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

public class ImagesControllerTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IImageManifestService> _mockManifestService;
    private readonly Mock<IImageDiffService> _mockDiffService;
    private readonly Mock<ICurrentUserService> _mockCurrentUser;
    private readonly Mock<IOrganizationMembershipService> _mockOrgMembership;
    private readonly Mock<IImageBuildOrchestrator> _mockOrchestrator;
    private readonly Mock<IAsyncBuildExecutor> _mockExecutor;
    private readonly Mock<IBuildEventBus> _mockEventBus;
    private readonly Mock<IBuildExecutionRegistry> _mockExecutionRegistry;
    private readonly ImagesController _controller;

    public ImagesControllerTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockManifestService = new Mock<IImageManifestService>();
        _mockDiffService = new Mock<IImageDiffService>();
        _mockCurrentUser = new Mock<ICurrentUserService>();
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        _mockCurrentUser.Setup(u => u.IsAuthenticated()).Returns(true);
        _mockOrgMembership = new Mock<IOrganizationMembershipService>();
        _mockOrgMembership.Setup(o => o.IsMemberAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockOrgMembership.Setup(o => o.HasPermissionAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockOrchestrator = new Mock<IImageBuildOrchestrator>();
        // IM8 (#262). Default: orchestrator returns a Succeeded result
        // for any build call. Tests that need different outcomes
        // (cache hit, failure) override this on a per-test basis.
        _mockOrchestrator.Setup(o => o.BuildAsync(
                It.IsAny<ImageBuildRequest>(),
                It.IsAny<IProgress<Andy.Containers.Abstractions.Images.BuildProgressEvent>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BuildResult
            {
                BuildId = Guid.NewGuid(),
                Status = BuildResultStatus.Succeeded,
                Digest = "sha256:test",
                References = [new BuildResultReference("local-zot", "test", "sha256-test", DateTimeOffset.UtcNow)],
            });
        _mockExecutor = new Mock<IAsyncBuildExecutor>();
        // IM9 (#263). Default: cache miss → queued. Tests that
        // need cache hit / failure outcomes override per-test.
        _mockExecutor.Setup(e => e.StartAsync(
                It.IsAny<ImageBuildRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ImageBuildRequest req, CancellationToken _) =>
                new AsyncBuildHandle(Guid.NewGuid(), AsyncBuildHandleStatus.Queued, null));
        _mockEventBus = new Mock<IBuildEventBus>();
        _mockExecutionRegistry = new Mock<IBuildExecutionRegistry>();

        _controller = new ImagesController(
            _db,
            _mockManifestService.Object,
            _mockDiffService.Object,
            _mockCurrentUser.Object,
            _mockOrgMembership.Object,
            _mockOrchestrator.Object,
            _mockExecutor.Object,
            _mockEventBus.Object,
            _mockExecutionRegistry.Object);
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

        var result = await _controller.List(template.Id, ct: CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var images = ok.Value.Should().BeAssignableTo<List<ContainerImage>>().Subject;
        images.Should().HaveCount(2);
        images[0].BuildNumber.Should().Be(2); // ordered desc
    }

    [Fact]
    public async Task List_NoImages_ShouldReturnEmptyList()
    {
        var result = await _controller.List(Guid.NewGuid(), ct: CancellationToken.None);

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

        var result = await _controller.GetLatest(template.Id, ct: CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var image = ok.Value.Should().BeOfType<ContainerImage>().Subject;
        image.BuildNumber.Should().Be(2);
    }

    [Fact]
    public async Task GetLatest_NoSucceededImage_ShouldReturnNotFound()
    {
        var template = SeedTemplate();
        SeedImage(template.Id, 1, ImageBuildStatus.Building);

        var result = await _controller.GetLatest(template.Id, ct: CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    // --- Build ---

    [Fact]
    public async Task Build_NonExistentTemplate_ShouldReturnNotFound()
    {
        var result = await _controller.Build(Guid.NewGuid(), null, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    // IM8 (#262). Replaced the four legacy-ContainerImage build tests
    // with the new orchestrator-delegated assertions. Old tests asserted
    // BuildNumber / BuildStatus / BuiltOffline on a ContainerImage row;
    // the new contract returns BuildHandle instead. ContainerImage
    // creation is no longer the responsibility of the build endpoint —
    // BuildArtifactEntity is the new digest-anchored row, and the
    // ImageBuildOrchestrator owns persistence.

    [Fact]
    public async Task Build_ExecutorReturnsQueued_Returns202()
    {
        var template = SeedTemplate();
        var buildId = Guid.NewGuid();
        _mockExecutor
            .Setup(e => e.StartAsync(
                It.Is<ImageBuildRequest>(r => r.TemplateId == template.Id),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AsyncBuildHandle(buildId, AsyncBuildHandleStatus.Queued, null));

        var result = await _controller.Build(template.Id, new BuildRequest(Offline: false), CancellationToken.None);

        var accepted = result.Should().BeOfType<AcceptedAtActionResult>().Subject;
        accepted.ActionName.Should().Be("GetBuildStatus");
        accepted.RouteValues!["buildId"].Should().Be(buildId);
    }

    [Fact]
    public async Task Build_ExecutorReturnsCached_Returns200()
    {
        var template = SeedTemplate();
        _mockExecutor
            .Setup(e => e.StartAsync(It.IsAny<ImageBuildRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AsyncBuildHandle(
                Guid.NewGuid(),
                AsyncBuildHandleStatus.Cached,
                new BuildResult
                {
                    BuildId = Guid.NewGuid(),
                    Status = BuildResultStatus.Cached,
                    Digest = "sha256:cached",
                    References = [
                        new BuildResultReference("local-zot", template.Code, "sha256-cached", DateTimeOffset.UtcNow),
                    ],
                }));

        var result = await _controller.Build(template.Id, new BuildRequest(), CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var status = (string)ok.Value!.GetType().GetProperty("Status")!.GetValue(ok.Value)!;
        status.Should().Be("cached",
            "IM5 contract: cache hit returns 200 OK with status='cached', no rebuild.");
    }

    [Fact]
    public async Task Build_ExecutorReturnsFailedEngine_Returns503()
    {
        var template = SeedTemplate();
        _mockExecutor
            .Setup(e => e.StartAsync(It.IsAny<ImageBuildRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AsyncBuildHandle(
                Guid.NewGuid(),
                AsyncBuildHandleStatus.Failed,
                new BuildResult
                {
                    BuildId = Guid.NewGuid(),
                    Status = BuildResultStatus.Failed,
                    ErrorCode = "build.engine-detect",
                    ErrorMessage = "no container build engine is available",
                }));

        var result = await _controller.Build(template.Id, null, CancellationToken.None);

        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Build_ExecutorReturnsFailedBuild_Returns422()
    {
        var template = SeedTemplate();
        _mockExecutor
            .Setup(e => e.StartAsync(It.IsAny<ImageBuildRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AsyncBuildHandle(
                Guid.NewGuid(),
                AsyncBuildHandleStatus.Failed,
                new BuildResult
                {
                    BuildId = Guid.NewGuid(),
                    Status = BuildResultStatus.Failed,
                    ErrorCode = "build.failed",
                    ErrorMessage = "Step 5/12 failed",
                    FailureLog = "stderr from the build engine here",
                }));

        var result = await _controller.Build(template.Id, null, CancellationToken.None);

        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
    }

    [Fact]
    public async Task Build_ForceFlag_FlowsThroughToOrchestrator()
    {
        var template = SeedTemplate();

        await _controller.Build(template.Id, new BuildRequest(Force: true), CancellationToken.None);

        // IM9 (#263). Verify against the executor — the controller
        // delegates to it, not directly to the orchestrator.
        _mockExecutor.Verify(e => e.StartAsync(
            It.Is<ImageBuildRequest>(r => r.Force == true),
            It.IsAny<CancellationToken>()),
            Times.Once,
            "the controller's force flag must reach the executor unchanged so the cache short-circuit is bypassed.");
    }

    [Fact]
    public async Task Build_RegistryIdOverride_FlowsThroughToOrchestrator()
    {
        var template = SeedTemplate();

        await _controller.Build(template.Id, new BuildRequest(RegistryId: "team-zot"), CancellationToken.None);

        _mockExecutor.Verify(e => e.StartAsync(
            It.Is<ImageBuildRequest>(r => r.RegistryId == "team-zot"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // IM9 (#263). Status snapshot endpoint.

    [Fact]
    public async Task GetBuildStatus_UnknownBuildId_Returns404()
    {
        _mockExecutionRegistry.Setup(r => r.TryGet(It.IsAny<Guid>())).Returns((BuildExecutionState?)null);

        var result = await _controller.GetBuildStatus(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetBuildStatus_KnownBuildId_ReturnsState()
    {
        var buildId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        _mockExecutionRegistry.Setup(r => r.TryGet(buildId)).Returns(new BuildExecutionState
        {
            BuildId = buildId,
            TemplateId = templateId,
            Status = BuildExecutionStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
        });

        var result = await _controller.GetBuildStatus(buildId, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var status = (string)ok.Value!.GetType().GetProperty("status")!.GetValue(ok.Value)!;
        status.Should().Be("running");
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
