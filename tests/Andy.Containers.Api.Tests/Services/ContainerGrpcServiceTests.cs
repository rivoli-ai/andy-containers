using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Grpc;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Grpc.Core;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class ContainerGrpcServiceTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IGitCloneService> _mockGitCloneService;
    private readonly Mock<IImageManifestService> _mockManifestService;
    private readonly ContainerGrpcService _service;
    private readonly ServerCallContext _context;

    public ContainerGrpcServiceTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockGitCloneService = new Mock<IGitCloneService>();
        _mockManifestService = new Mock<IImageManifestService>();
        var mockCurrentUser = new Mock<ICurrentUserService>();
        mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        mockCurrentUser.Setup(u => u.IsAuthenticated()).Returns(true);
        var mockOrgMembership = new Mock<IOrganizationMembershipService>();
        mockOrgMembership.Setup(o => o.IsMemberAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        mockOrgMembership.Setup(o => o.HasPermissionAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _service = new ContainerGrpcService(_db, _mockGitCloneService.Object, _mockManifestService.Object, mockCurrentUser.Object, mockOrgMembership.Object);
        _context = CreateMockContext();
    }

    public void Dispose() => _db.Dispose();

    private static ServerCallContext CreateMockContext() => new TestServerCallContext();

    private class TestServerCallContext : ServerCallContext
    {
        protected override string MethodCore => "test";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "test-peer";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => new();
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new("test", new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotImplementedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
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
            Tools =
            [
                new InstalledTool { Name = "python", Version = "3.12.8", Type = DependencyType.Runtime },
                new InstalledTool { Name = "node", Version = "20.18.1", Type = DependencyType.Runtime, MatchesDeclared = false }
            ],
            OsPackages = [new InstalledPackage { Name = "curl", Version = "8.5.0" }]
        };
    }

    // --- ListContainerRepositories ---

    [Fact]
    public async Task ListContainerRepositories_ShouldReturnRepos()
    {
        var containerId = Guid.NewGuid();
        _db.ContainerGitRepositories.Add(new ContainerGitRepository
        {
            ContainerId = containerId,
            Url = "https://github.com/test/repo.git",
            TargetPath = "/workspace",
            CloneStatus = GitCloneStatus.Cloned
        });
        await _db.SaveChangesAsync();

        var result = await _service.ListContainerRepositories(
            new ContainerIdRequest { ContainerId = containerId.ToString() }, _context);

        result.Repositories.Should().HaveCount(1);
        result.Repositories[0].Url.Should().Be("https://github.com/test/repo.git");
        result.Repositories[0].CloneStatus.Should().Be("Cloned");
    }

    [Fact]
    public async Task ListContainerRepositories_InvalidId_ShouldThrow()
    {
        var act = () => _service.ListContainerRepositories(
            new ContainerIdRequest { ContainerId = "bad" }, _context);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // --- CloneRepository ---

    [Fact]
    public async Task CloneRepository_ShouldPersistAndCallService()
    {
        var containerId = Guid.NewGuid();
        _mockGitCloneService
            .Setup(s => s.CloneRepositoryAsync(containerId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerGitRepository
            {
                ContainerId = containerId,
                Url = "https://github.com/test/repo.git",
                TargetPath = "/workspace",
                CloneStatus = GitCloneStatus.Cloned
            });

        var result = await _service.CloneRepository(new CloneRepositoryRequest
        {
            ContainerId = containerId.ToString(),
            Url = "https://github.com/test/repo.git",
            Branch = "main",
            TargetPath = "/workspace"
        }, _context);

        result.Url.Should().Be("https://github.com/test/repo.git");
        result.CloneStatus.Should().Be("Cloned");
        _db.ContainerGitRepositories.Should().HaveCount(1);
    }

    [Fact]
    public async Task CloneRepository_InvalidId_ShouldThrow()
    {
        var act = () => _service.CloneRepository(new CloneRepositoryRequest
        {
            ContainerId = "bad",
            Url = "https://github.com/test/repo.git"
        }, _context);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task CloneRepository_EmptyBranch_ShouldSetNull()
    {
        var containerId = Guid.NewGuid();
        _mockGitCloneService
            .Setup(s => s.CloneRepositoryAsync(containerId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid repoId, CancellationToken _) =>
            {
                return _db.ContainerGitRepositories.Find(repoId)!;
            });

        await _service.CloneRepository(new CloneRepositoryRequest
        {
            ContainerId = containerId.ToString(),
            Url = "https://github.com/test/repo.git",
            Branch = "",
            TargetPath = ""
        }, _context);

        var saved = _db.ContainerGitRepositories.Single();
        saved.Branch.Should().BeNull();
        saved.TargetPath.Should().Be("/workspace"); // default
    }

    // --- PullRepository ---

    [Fact]
    public async Task PullRepository_ShouldCallService()
    {
        var containerId = Guid.NewGuid();
        var repoId = Guid.NewGuid();
        _mockGitCloneService
            .Setup(s => s.PullRepositoryAsync(containerId, repoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerGitRepository
            {
                Id = repoId,
                ContainerId = containerId,
                Url = "https://github.com/test/repo.git",
                TargetPath = "/workspace",
                CloneStatus = GitCloneStatus.Cloned
            });

        var result = await _service.PullRepository(new PullRepositoryRequest
        {
            ContainerId = containerId.ToString(),
            RepositoryId = repoId.ToString()
        }, _context);

        result.Id.Should().Be(repoId.ToString());
        result.CloneStatus.Should().Be("Cloned");
    }

    [Fact]
    public async Task PullRepository_InvalidIds_ShouldThrow()
    {
        var act = () => _service.PullRepository(new PullRepositoryRequest
        {
            ContainerId = "bad",
            RepositoryId = "bad"
        }, _context);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // --- GetImageManifest ---

    [Fact]
    public async Task GetImageManifest_ShouldReturnManifest()
    {
        var imageId = Guid.NewGuid();
        var manifest = CreateTestManifest();
        _mockManifestService.Setup(s => s.GetManifestAsync(imageId, It.IsAny<CancellationToken>())).ReturnsAsync(manifest);

        var result = await _service.GetImageManifest(
            new GetImageManifestRequest { ImageId = imageId.ToString() }, _context);

        result.ImageContentHash.Should().Be("sha256:test");
        result.BaseImage.Should().Be("ubuntu:24.04");
        result.Architecture.Should().Be("amd64");
        result.OsName.Should().Be("Ubuntu");
        result.OsVersion.Should().Be("24.04");
        result.Tools.Should().HaveCount(2);
        result.PackageCount.Should().Be(1);
    }

    [Fact]
    public async Task GetImageManifest_InvalidId_ShouldThrow()
    {
        var act = () => _service.GetImageManifest(
            new GetImageManifestRequest { ImageId = "bad" }, _context);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task GetImageManifest_NoManifest_ShouldThrowNotFound()
    {
        var imageId = Guid.NewGuid();
        _mockManifestService.Setup(s => s.GetManifestAsync(imageId, It.IsAny<CancellationToken>())).ReturnsAsync((ImageToolManifest?)null);

        var act = () => _service.GetImageManifest(
            new GetImageManifestRequest { ImageId = imageId.ToString() }, _context);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    // --- GetImageTools ---

    [Fact]
    public async Task GetImageTools_ShouldReturnTools()
    {
        var imageId = Guid.NewGuid();
        var manifest = CreateTestManifest();
        _mockManifestService.Setup(s => s.GetManifestAsync(imageId, It.IsAny<CancellationToken>())).ReturnsAsync(manifest);

        var result = await _service.GetImageTools(
            new GetImageManifestRequest { ImageId = imageId.ToString() }, _context);

        result.Tools.Should().HaveCount(2);
        result.Tools[0].Name.Should().Be("python");
        result.Tools[0].Version.Should().Be("3.12.8");
        result.Tools[0].Type.Should().Be("Runtime");
        result.Tools[0].MatchesDeclared.Should().BeTrue();
        result.Tools[1].MatchesDeclared.Should().BeFalse();
    }

    [Fact]
    public async Task GetImageTools_InvalidId_ShouldThrow()
    {
        var act = () => _service.GetImageTools(
            new GetImageManifestRequest { ImageId = "bad" }, _context);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task GetImageTools_NoManifest_ShouldThrowNotFound()
    {
        var imageId = Guid.NewGuid();
        _mockManifestService.Setup(s => s.GetManifestAsync(imageId, It.IsAny<CancellationToken>())).ReturnsAsync((ImageToolManifest?)null);

        var act = () => _service.GetImageTools(
            new GetImageManifestRequest { ImageId = imageId.ToString() }, _context);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    // --- IntrospectImage ---

    [Fact]
    public async Task IntrospectImage_ShouldCallRefreshAndReturnManifest()
    {
        var imageId = Guid.NewGuid();
        var manifest = CreateTestManifest();
        var image = new ContainerImage
        {
            Id = imageId,
            ContentHash = "sha256:test",
            Tag = "test:1",
            ImageReference = "test:1",
            BaseImageDigest = "sha256:base",
            DependencyManifest = "{}",
            DependencyLock = "{}"
        };
        _mockManifestService.Setup(s => s.RefreshManifestAsync(imageId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((manifest, image));

        var result = await _service.IntrospectImage(
            new GetImageManifestRequest { ImageId = imageId.ToString() }, _context);

        result.ImageContentHash.Should().Be("sha256:test");
        result.Tools.Should().HaveCount(2);
        _mockManifestService.Verify(s => s.RefreshManifestAsync(imageId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IntrospectImage_InvalidId_ShouldThrow()
    {
        var act = () => _service.IntrospectImage(
            new GetImageManifestRequest { ImageId = "bad" }, _context);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // --- Field mapping tests ---

    [Fact]
    public async Task CloneRepository_ShouldMapAllFieldsCorrectly()
    {
        var containerId = Guid.NewGuid();
        var clonedRepo = new ContainerGitRepository
        {
            ContainerId = containerId,
            Url = "https://github.com/test/repo.git",
            Branch = "develop",
            TargetPath = "/home/dev",
            CloneDepth = 1,
            Submodules = true,
            IsFromTemplate = true,
            CloneStatus = GitCloneStatus.Cloned,
            CloneError = null,
            CloneStartedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CloneCompletedAt = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc)
        };
        _mockGitCloneService
            .Setup(s => s.CloneRepositoryAsync(containerId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(clonedRepo);

        var result = await _service.CloneRepository(new CloneRepositoryRequest
        {
            ContainerId = containerId.ToString(),
            Url = "https://github.com/test/repo.git",
            Branch = "develop",
            TargetPath = "/home/dev",
            CloneDepth = 1,
            Submodules = true
        }, _context);

        result.Branch.Should().Be("develop");
        result.TargetPath.Should().Be("/home/dev");
        result.CloneDepth.Should().Be(1);
        result.Submodules.Should().BeTrue();
        result.IsFromTemplate.Should().BeTrue();
        result.CloneError.Should().BeEmpty();
        result.CloneStartedAt.Should().NotBeEmpty();
        result.CloneCompletedAt.Should().NotBeEmpty();
    }
}
