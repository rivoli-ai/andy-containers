using Andy.Containers.Abstractions.Images;
using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models.ImageManagement;
using Andy.Containers.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

/// <summary>
/// rivoli-ai/conductor#1014 (M1.9.6). Controller-level coverage for
/// <c>POST /api/images/ensure-pull</c>. The puller itself
/// (<see cref="Infrastructure.Images.DockerCliImagePullService"/>)
/// shells out to docker — covered by its own unit/integration
/// suites; this file pins the controller's request/response shape
/// and error mapping.
/// </summary>
public class ImagesControllerEnsurePullTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly ImagesController _controller;

    public ImagesControllerEnsurePullTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        var mockCurrentUser = new Mock<ICurrentUserService>();
        mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        mockCurrentUser.Setup(u => u.IsAuthenticated()).Returns(true);

        _controller = new ImagesController(
            _db,
            new Mock<IImageManifestService>().Object,
            new Mock<IImageDiffService>().Object,
            mockCurrentUser.Object,
            new Mock<IOrganizationMembershipService>().Object,
            new Mock<IImageBuildOrchestrator>().Object,
            new Mock<IAsyncBuildExecutor>().Object,
            new Mock<IBuildEventBus>().Object,
            new Mock<IBuildExecutionRegistry>().Object,
            new Mock<IBuildArtifactStore>().Object);
    }

    public void Dispose() => _db.Dispose();

    // ---- Happy path ----

    [Fact]
    public async Task EnsurePull_ReturnsOk_WithPullerResponse()
    {
        var puller = new StubPuller
        {
            Response = new EnsurePullResponse
            {
                AlreadyPresent = false,
                RegistryId = "local-zot",
                RepoPath = "conductor-terminal-claude-code",
                Tag = "v1",
                Digest = "sha256:abc123",
                SizeBytes = 524_288_000,
            },
        };

        var result = await _controller.EnsurePull(
            new EnsurePullRequest
            {
                SourceRegistry = "ghcr.io",
                SourceRepository = "rivoli-ai/conductor-terminal-claude-code",
                SourceTag = "v1",
                DestinationRegistryId = "local-zot",
            },
            puller,
            CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(200);
        var body = ok.Value.Should().BeOfType<EnsurePullResponse>().Subject;
        body.AlreadyPresent.Should().BeFalse();
        body.Digest.Should().Be("sha256:abc123");
        puller.LastRequest!.SourceRegistry.Should().Be("ghcr.io");
    }

    // ---- Idempotency-path passthrough ----

    [Fact]
    public async Task EnsurePull_AlreadyPresent_ReturnsOk_WithFlagSet()
    {
        var puller = new StubPuller
        {
            Response = new EnsurePullResponse
            {
                AlreadyPresent = true,
                RegistryId = "local-zot",
                RepoPath = "conductor-terminal-opencode",
                Tag = "v1",
                Digest = "sha256:def",
                SizeBytes = 0,
            },
        };

        var result = await _controller.EnsurePull(
            new EnsurePullRequest
            {
                SourceRegistry = "ghcr.io",
                SourceRepository = "rivoli-ai/conductor-terminal-opencode",
                SourceTag = "v1",
                DestinationRegistryId = "local-zot",
            },
            puller,
            CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<EnsurePullResponse>().Subject;
        body.AlreadyPresent.Should().BeTrue();
    }

    // ---- Missing body ----

    [Fact]
    public async Task EnsurePull_NullBody_Returns400()
    {
        var result = await _controller.EnsurePull(
            request: null!,
            puller: new StubPuller(),
            ct: CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.StatusCode.Should().Be(400);
        var body = bad.Value.Should().BeOfType<ImageManagementErrorBody>().Subject;
        body.Code.Should().Be("ensure_pull_missing_body");
    }

    // ---- Validation failure from puller → 400 ----

    [Fact]
    public async Task EnsurePull_InvalidRequest_Maps400()
    {
        var puller = new StubPuller
        {
            Throw = new ImagePullException(
                code: "ensure_pull_invalid_source_registry",
                message: "SourceRegistry is required"),
        };

        var result = await _controller.EnsurePull(
            new EnsurePullRequest
            {
                SourceRegistry = "",
                SourceRepository = "x",
                SourceTag = "y",
                DestinationRegistryId = "local-zot",
            },
            puller,
            CancellationToken.None);

        var oj = result.Should().BeOfType<ObjectResult>().Subject;
        oj.StatusCode.Should().Be(400);
        var body = oj.Value.Should().BeOfType<ImageManagementErrorBody>().Subject;
        body.Code.Should().StartWith("ensure_pull_invalid_");
    }

    // ---- Unknown destination → 400 ----

    [Fact]
    public async Task EnsurePull_UnknownDestinationRegistry_Maps400()
    {
        var puller = new StubPuller
        {
            Throw = new ImagePullException(
                code: "ensure_pull_unknown_destination_registry",
                message: "no IRegistryAdapter for 'made-up'"),
        };

        var result = await _controller.EnsurePull(
            new EnsurePullRequest
            {
                SourceRegistry = "ghcr.io",
                SourceRepository = "x",
                SourceTag = "y",
                DestinationRegistryId = "made-up",
            },
            puller,
            CancellationToken.None);

        var oj = result.Should().BeOfType<ObjectResult>().Subject;
        oj.StatusCode.Should().Be(400);
    }

    // ---- Upstream failure → 503 ----

    [Fact]
    public async Task EnsurePull_DockerLaunchFails_Maps503()
    {
        var puller = new StubPuller
        {
            Throw = new ImagePullException(
                code: "ensure_pull_docker_launch_failed.Pull",
                message: "failed to launch 'docker' — is the Docker CLI installed and on PATH?"),
        };

        var result = await _controller.EnsurePull(
            new EnsurePullRequest
            {
                SourceRegistry = "ghcr.io",
                SourceRepository = "x",
                SourceTag = "y",
                DestinationRegistryId = "local-zot",
            },
            puller,
            CancellationToken.None);

        var oj = result.Should().BeOfType<ObjectResult>().Subject;
        oj.StatusCode.Should().Be(503);
        var body = oj.Value.Should().BeOfType<ImageManagementErrorBody>().Subject;
        body.Code.Should().Contain("docker_launch_failed");
    }

    [Fact]
    public async Task EnsurePull_DockerPushNonZeroExit_Maps503_WithCapturedOutput()
    {
        var puller = new StubPuller
        {
            Throw = new ImagePullException(
                code: "ensure_pull_docker_nonzero_exit_1.Push",
                message: "docker exited with code 1 during push",
                capturedOutput: "denied: requested access to the resource is denied"),
        };

        var result = await _controller.EnsurePull(
            new EnsurePullRequest
            {
                SourceRegistry = "ghcr.io",
                SourceRepository = "x",
                SourceTag = "y",
                DestinationRegistryId = "local-zot",
            },
            puller,
            CancellationToken.None);

        var oj = result.Should().BeOfType<ObjectResult>().Subject;
        oj.StatusCode.Should().Be(503);
        var body = oj.Value.Should().BeOfType<ImageManagementErrorBody>().Subject;
        body.BuildLog.Should().Contain("denied: requested access to the resource is denied");
    }

    // ---- Test double ----

    private sealed class StubPuller : IImagePullService
    {
        public EnsurePullResponse? Response { get; set; }
        public ImagePullException? Throw { get; set; }
        public EnsurePullRequest? LastRequest { get; private set; }

        public Task<EnsurePullResponse> EnsurePullAsync(EnsurePullRequest request, CancellationToken ct)
        {
            LastRequest = request;
            if (Throw is not null) throw Throw;
            return Task.FromResult(Response
                ?? throw new InvalidOperationException("test setup error — Response not configured"));
        }
    }
}
