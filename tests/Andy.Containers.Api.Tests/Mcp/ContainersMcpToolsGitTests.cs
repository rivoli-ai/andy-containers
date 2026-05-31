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

public class ContainersMcpToolsGitTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IGitCloneService> _mockGitCloneService;
    private readonly Mock<IGitCredentialService> _mockCredentialService;
    private readonly Mock<IContainerService> _mockContainerService;
    private readonly ContainersMcpTools _tools;

    public ContainersMcpToolsGitTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockGitCloneService = new Mock<IGitCloneService>();
        _mockCredentialService = new Mock<IGitCredentialService>();
        _mockContainerService = new Mock<IContainerService>();
        var mockCurrentUser = new Mock<ICurrentUserService>();
        mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        var mockOrgMembership = new Mock<IOrganizationMembershipService>();
        mockOrgMembership.Setup(o => o.IsMemberAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        mockOrgMembership.Setup(o => o.HasPermissionAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var mockProbeService = new Mock<IGitRepositoryProbeService>();
        mockProbeService.Setup(p => p.ProbeRepositoriesAsync(It.IsAny<IReadOnlyList<GitRepositoryConfig>>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());
        var gitDiffService = new GitDiffService(_db, _mockContainerService.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<GitDiffService>.Instance);
        _tools = new ContainersMcpTools(_db, _mockContainerService.Object, _mockGitCloneService.Object, _mockCredentialService.Object, mockProbeService.Object, new Mock<IImageManifestService>().Object, new Mock<IImageDiffService>().Object, gitDiffService, mockCurrentUser.Object, mockOrgMembership.Object);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task ListContainerRepositories_ShouldReturnReposWithCorrectFields()
    {
        var containerId = Guid.NewGuid();
        _db.ContainerGitRepositories.Add(new ContainerGitRepository
        {
            ContainerId = containerId,
            Url = "https://github.com/owner/repo.git",
            Branch = "develop",
            TargetPath = "/workspace/repo",
            CloneStatus = GitCloneStatus.Cloned,
            IsFromTemplate = true
        });
        await _db.SaveChangesAsync();

        var result = await _tools.ListContainerRepositories(containerId.ToString());

        result.Should().HaveCount(1);
        result[0].Url.Should().Be("https://github.com/owner/repo.git");
        result[0].Branch.Should().Be("develop");
        result[0].TargetPath.Should().Be("/workspace/repo");
        result[0].CloneStatus.Should().Be("Cloned");
        result[0].IsFromTemplate.Should().BeTrue();
    }

    [Fact]
    public async Task ListContainerRepositories_NoRepos_ShouldReturnEmptyList()
    {
        var containerId = Guid.NewGuid();
        var result = await _tools.ListContainerRepositories(containerId.ToString());
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListContainerRepositories_InvalidId_ShouldReturnEmpty()
    {
        var result = await _tools.ListContainerRepositories("not-a-guid");
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task CloneRepository_ShouldPersistAndCallService()
    {
        var containerId = Guid.NewGuid();
        _db.Containers.Add(new Container { Id = containerId, Name = "test", OwnerId = "test-user", Status = ContainerStatus.Running });
        await _db.SaveChangesAsync();
        var clonedRepo = new ContainerGitRepository
        {
            ContainerId = containerId,
            Url = "https://github.com/owner/repo.git",
            Branch = "main",
            TargetPath = "/workspace",
            CloneStatus = GitCloneStatus.Cloned
        };
        _mockGitCloneService.Setup(s => s.CloneRepositoryAsync(containerId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(clonedRepo);

        var result = await _tools.CloneRepository(containerId.ToString(), "https://github.com/owner/repo.git", branch: "main");

        result.Should().NotBeNull();
        result!.Url.Should().Be("https://github.com/owner/repo.git");
        result.Branch.Should().Be("main");
        result.CloneStatus.Should().Be("Cloned");

        // Verify the repo was persisted in DB
        var saved = _db.ContainerGitRepositories.SingleOrDefault(r => r.ContainerId == containerId);
        saved.Should().NotBeNull();
        saved!.Url.Should().Be("https://github.com/owner/repo.git");
    }

    [Fact]
    public async Task CloneRepository_InvalidUrl_ShouldReturnNull()
    {
        var containerId = Guid.NewGuid();

        var result = await _tools.CloneRepository(containerId.ToString(), "file:///etc/passwd");

        result.Should().BeNull();
        _mockGitCloneService.Verify(s => s.CloneRepositoryAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CloneRepository_InvalidContainerId_ShouldReturnNull()
    {
        var result = await _tools.CloneRepository("not-a-guid", "https://github.com/owner/repo.git");

        result.Should().BeNull();
    }

    [Fact]
    public async Task CloneRepository_WithAllOptions_ShouldPersistThem()
    {
        var containerId = Guid.NewGuid();
        _db.Containers.Add(new Container { Id = containerId, Name = "test", OwnerId = "test-user", Status = ContainerStatus.Running });
        await _db.SaveChangesAsync();
        _mockGitCloneService.Setup(s => s.CloneRepositoryAsync(containerId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid repoId, CancellationToken _) =>
            {
                var r = _db.ContainerGitRepositories.Find(repoId)!;
                r.CloneStatus = GitCloneStatus.Cloned;
                return r;
            });

        await _tools.CloneRepository(
            containerId.ToString(),
            "https://github.com/owner/repo.git",
            branch: "develop",
            targetPath: "/home/dev/project",
            credentialRef: "my-pat",
            cloneDepth: 1,
            submodules: true);

        var saved = _db.ContainerGitRepositories.SingleOrDefault(r => r.ContainerId == containerId);
        saved.Should().NotBeNull();
        saved!.Branch.Should().Be("develop");
        saved.TargetPath.Should().Be("/home/dev/project");
        saved.CredentialRef.Should().Be("my-pat");
        saved.CloneDepth.Should().Be(1);
        saved.Submodules.Should().BeTrue();
    }

    [Fact]
    public async Task PullRepository_ShouldCallServiceAndReturnResult()
    {
        var containerId = Guid.NewGuid();
        var repoId = Guid.NewGuid();
        var repo = new ContainerGitRepository
        {
            Id = repoId,
            ContainerId = containerId,
            Url = "https://github.com/owner/repo.git",
            TargetPath = "/workspace",
            CloneStatus = GitCloneStatus.Cloned,
            CloneCompletedAt = DateTime.UtcNow
        };
        _mockGitCloneService.Setup(s => s.PullRepositoryAsync(containerId, repoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repo);

        var result = await _tools.PullRepository(containerId.ToString(), repoId.ToString());

        result.Should().NotBeNull();
        result!.Id.Should().Be(repoId);
        result.CloneStatus.Should().Be("Cloned");
        result.CloneCompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task PullRepository_InvalidIds_ShouldReturnNull()
    {
        var result = await _tools.PullRepository("not-a-guid", "also-not-a-guid");
        result.Should().BeNull();
    }

    [Fact]
    public async Task ListGitCredentials_ShouldReturnFieldsWithoutToken()
    {
        var credentials = new List<GitCredential>
        {
            new() { Id = Guid.NewGuid(), OwnerId = "user1", Label = "github", EncryptedToken = "enc-secret", GitHost = "github.com", CredentialType = GitCredentialType.PersonalAccessToken }
        };
        _mockCredentialService.Setup(s => s.ListAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(credentials);

        var result = await _tools.ListGitCredentials("user1");

        result.Should().HaveCount(1);
        result[0].Label.Should().Be("github");
        result[0].GitHost.Should().Be("github.com");
        result[0].CredentialType.Should().Be("PersonalAccessToken");
        // McpGitCredentialInfo record should not expose token
        typeof(McpGitCredentialInfo).GetProperties().Select(p => p.Name).Should().NotContain("Token");
        typeof(McpGitCredentialInfo).GetProperties().Select(p => p.Name).Should().NotContain("EncryptedToken");
    }

    [Fact]
    public async Task ListGitCredentials_EmptyList_ShouldReturnEmpty()
    {
        _mockCredentialService.Setup(s => s.ListAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GitCredential>());

        var result = await _tools.ListGitCredentials("user1");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task StoreGitCredential_ShouldCallServiceAndReturnDto()
    {
        var id = Guid.NewGuid();
        var credential = new GitCredential
        {
            Id = id, OwnerId = "user1", Label = "my-pat", EncryptedToken = "enc", GitHost = "github.com"
        };
        _mockCredentialService.Setup(s => s.CreateAsync("user1", "my-pat", "token123", "github.com", GitCredentialType.PersonalAccessToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(credential);

        var result = await _tools.StoreGitCredential("user1", "my-pat", "token123", "github.com");

        result.Should().NotBeNull();
        result!.Id.Should().Be(id);
        result.Label.Should().Be("my-pat");
        result.GitHost.Should().Be("github.com");
    }

    // F6.1 (rivoli-ai/conductor#1940): MCP parity for the git-diff endpoint.
    [Fact]
    public async Task GetContainerGitDiff_ReturnsParsedDiff()
    {
        var containerId = Guid.NewGuid();
        _db.Containers.Add(new Container { Id = containerId, Name = "test", OwnerId = "test-user", Status = ContainerStatus.Running });
        _db.ContainerGitRepositories.Add(new ContainerGitRepository
        {
            Id = Guid.NewGuid(), ContainerId = containerId,
            Url = "https://github.com/owner/repo.git", Branch = "main",
            TargetPath = "/workspace", CloneStatus = GitCloneStatus.Cloned,
        });
        await _db.SaveChangesAsync();

        _mockContainerService
            .Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult
            {
                ExitCode = 0,
                StdOut = "---NUMSTAT---\n1\t0\tFoo.cs\n---PATCH---\ndiff --git a/Foo.cs b/Foo.cs\n+added\n"
            });

        var result = await _tools.GetContainerGitDiff(containerId.ToString());

        result.Should().NotBeNull();
        result!.Files.Should().ContainSingle();
        result.Files[0].Path.Should().Be("Foo.cs");
        result.Files[0].Additions.Should().Be(1);
        result.RawPatch.Should().Contain("+added");
    }

    [Fact]
    public async Task GetContainerGitDiff_InvalidContainerId_ReturnsNull()
    {
        var result = await _tools.GetContainerGitDiff("not-a-guid");
        result.Should().BeNull();
    }
}
