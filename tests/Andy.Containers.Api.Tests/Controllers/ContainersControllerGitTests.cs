using Andy.Containers.Abstractions;
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

public class ContainersControllerGitTests : IDisposable
{
    private readonly Mock<IContainerService> _mockService;
    private readonly Mock<ICurrentUserService> _mockCurrentUser;
    private readonly Mock<IGitCloneService> _mockGitCloneService;
    private readonly ContainersDbContext _db;
    private readonly ContainersController _controller;

    public ContainersControllerGitTests()
    {
        _mockService = new Mock<IContainerService>();
        _mockCurrentUser = new Mock<ICurrentUserService>();
        _mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        _mockGitCloneService = new Mock<IGitCloneService>();
        _db = InMemoryDbHelper.CreateContext();
        var mockOrgMembership = new Mock<IOrganizationMembershipService>();
        mockOrgMembership.Setup(o => o.IsMemberAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        mockOrgMembership.Setup(o => o.HasPermissionAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var mockCredentialService = new Mock<IGitCredentialService>();
        var mockProbeService = new Mock<IGitRepositoryProbeService>();
        mockProbeService.Setup(p => p.ProbeRepositoriesAsync(It.IsAny<IReadOnlyList<GitRepositoryConfig>>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());
        _controller = new ContainersController(_mockService.Object, _mockCurrentUser.Object, _db, _mockGitCloneService.Object, mockCredentialService.Object, mockProbeService.Object, mockOrgMembership.Object);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private Container CreateTestContainer(ContainerStatus status = ContainerStatus.Running, string ownerId = "test-user")
    {
        return new Container
        {
            Id = Guid.NewGuid(),
            Name = "test-container",
            OwnerId = ownerId,
            Status = status
        };
    }

    [Fact]
    public async Task ListRepositories_ShouldReturnReposWithCorrectFields()
    {
        var container = CreateTestContainer();
        _mockService.Setup(s => s.GetContainerAsync(container.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        _db.ContainerGitRepositories.Add(new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "https://github.com/owner/repo1.git",
            Branch = "main",
            TargetPath = "/workspace/repo1",
            CloneStatus = GitCloneStatus.Cloned,
            IsFromTemplate = true
        });
        _db.ContainerGitRepositories.Add(new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "https://github.com/owner/repo2.git",
            TargetPath = "/workspace/repo2",
            CloneStatus = GitCloneStatus.Pending,
            CloneDepth = 1,
            Submodules = true
        });
        await _db.SaveChangesAsync();

        var result = await _controller.ListRepositories(container.Id, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var repos = ok.Value.Should().BeAssignableTo<IEnumerable<ContainerGitRepositoryDto>>().Subject.ToList();
        repos.Should().HaveCount(2);

        var repo1 = repos.Single(r => r.Url == "https://github.com/owner/repo1.git");
        repo1.Branch.Should().Be("main");
        repo1.CloneStatus.Should().Be("Cloned");
        repo1.IsFromTemplate.Should().BeTrue();

        var repo2 = repos.Single(r => r.Url == "https://github.com/owner/repo2.git");
        repo2.CloneStatus.Should().Be("Pending");
        repo2.CloneDepth.Should().Be(1);
        repo2.Submodules.Should().BeTrue();
        repo2.IsFromTemplate.Should().BeFalse();
    }

    [Fact]
    public async Task ListRepositories_NoRepos_ShouldReturnEmptyList()
    {
        var container = CreateTestContainer();
        _mockService.Setup(s => s.GetContainerAsync(container.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        var result = await _controller.ListRepositories(container.Id, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var repos = ok.Value.Should().BeAssignableTo<IEnumerable<ContainerGitRepositoryDto>>().Subject.ToList();
        repos.Should().BeEmpty();
    }

    [Fact]
    public async Task AddRepository_RunningContainer_ShouldCloneAndReturnCreated()
    {
        var container = CreateTestContainer(ContainerStatus.Running);
        _mockService.Setup(s => s.GetContainerAsync(container.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        var clonedRepo = new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "https://github.com/owner/repo.git",
            Branch = "develop",
            TargetPath = "/workspace/repo",
            CloneStatus = GitCloneStatus.Cloned,
            CloneDepth = 1,
            Submodules = true
        };
        _mockGitCloneService.Setup(s => s.CloneRepositoryAsync(container.Id, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(clonedRepo);

        var dto = new AddRepositoryDto { Url = "https://github.com/owner/repo.git", TargetPath = "/workspace/repo", Branch = "develop", CloneDepth = 1, Submodules = true };
        var result = await _controller.AddRepository(container.Id, dto, CancellationToken.None);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(201);
        var repoDto = created.Value.Should().BeOfType<ContainerGitRepositoryDto>().Subject;
        repoDto.CloneStatus.Should().Be("Cloned");
        repoDto.Url.Should().Be("https://github.com/owner/repo.git");
        repoDto.Branch.Should().Be("develop");
    }

    [Fact]
    public async Task AddRepository_ShouldPersistRepoInDb()
    {
        var container = CreateTestContainer(ContainerStatus.Running);
        _mockService.Setup(s => s.GetContainerAsync(container.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        _mockGitCloneService.Setup(s => s.CloneRepositoryAsync(container.Id, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid repoId, CancellationToken _) =>
            {
                var r = _db.ContainerGitRepositories.Find(repoId)!;
                r.CloneStatus = GitCloneStatus.Cloned;
                return r;
            });

        var dto = new AddRepositoryDto { Url = "https://github.com/owner/repo.git", CredentialRef = "my-pat" };
        await _controller.AddRepository(container.Id, dto, CancellationToken.None);

        var saved = _db.ContainerGitRepositories.SingleOrDefault(r => r.ContainerId == container.Id);
        saved.Should().NotBeNull();
        saved!.Url.Should().Be("https://github.com/owner/repo.git");
        saved.CredentialRef.Should().Be("my-pat");
    }

    [Fact]
    public async Task AddRepository_StoppedContainer_ShouldReturnBadRequest()
    {
        var container = CreateTestContainer(ContainerStatus.Stopped);
        _mockService.Setup(s => s.GetContainerAsync(container.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        var dto = new AddRepositoryDto { Url = "https://github.com/owner/repo.git" };
        var result = await _controller.AddRepository(container.Id, dto, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        _mockGitCloneService.Verify(s => s.CloneRepositoryAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddRepository_PendingContainer_ShouldReturnBadRequest()
    {
        var container = CreateTestContainer(ContainerStatus.Pending);
        _mockService.Setup(s => s.GetContainerAsync(container.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        var dto = new AddRepositoryDto { Url = "https://github.com/owner/repo.git" };
        var result = await _controller.AddRepository(container.Id, dto, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task AddRepository_InvalidUrl_ShouldReturnBadRequest()
    {
        var container = CreateTestContainer(ContainerStatus.Running);
        _mockService.Setup(s => s.GetContainerAsync(container.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        var dto = new AddRepositoryDto { Url = "file:///etc/passwd" };
        var result = await _controller.AddRepository(container.Id, dto, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        _mockGitCloneService.Verify(s => s.CloneRepositoryAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddRepository_EmbeddedCredentialsUrl_ShouldReturnBadRequest()
    {
        var container = CreateTestContainer(ContainerStatus.Running);
        _mockService.Setup(s => s.GetContainerAsync(container.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        var dto = new AddRepositoryDto { Url = "https://user:pass@github.com/owner/repo.git" };
        var result = await _controller.AddRepository(container.Id, dto, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task AddRepository_PathTraversal_ShouldReturnBadRequest()
    {
        var container = CreateTestContainer(ContainerStatus.Running);
        _mockService.Setup(s => s.GetContainerAsync(container.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        var dto = new AddRepositoryDto { Url = "https://github.com/owner/repo.git", TargetPath = "/workspace/../../../etc" };
        var result = await _controller.AddRepository(container.Id, dto, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task AddRepository_UnauthorizedUser_ShouldReturnForbid()
    {
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(false);
        var container = CreateTestContainer(ownerId: "other-user");
        _mockService.Setup(s => s.GetContainerAsync(container.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        var dto = new AddRepositoryDto { Url = "https://github.com/owner/repo.git" };
        var result = await _controller.AddRepository(container.Id, dto, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task PullRepository_ShouldReturnUpdatedRepo()
    {
        var container = CreateTestContainer();
        var repoId = Guid.NewGuid();
        _mockService.Setup(s => s.GetContainerAsync(container.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        var pulledRepo = new ContainerGitRepository
        {
            Id = repoId,
            ContainerId = container.Id,
            Url = "https://github.com/owner/repo.git",
            TargetPath = "/workspace/repo",
            CloneStatus = GitCloneStatus.Cloned
        };
        _mockGitCloneService.Setup(s => s.PullRepositoryAsync(container.Id, repoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pulledRepo);

        var result = await _controller.PullRepository(container.Id, repoId, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ContainerGitRepositoryDto>().Subject;
        dto.CloneStatus.Should().Be("Cloned");
        dto.Url.Should().Be("https://github.com/owner/repo.git");
    }

    [Fact]
    public async Task PullRepository_StoppedContainer_ShouldReturnBadRequest()
    {
        var container = CreateTestContainer(ContainerStatus.Stopped);
        _mockService.Setup(s => s.GetContainerAsync(container.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        var result = await _controller.PullRepository(container.Id, Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        _mockGitCloneService.Verify(s => s.PullRepositoryAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PullRepository_NonExistentRepo_ShouldReturnNotFound()
    {
        var container = CreateTestContainer();
        var repoId = Guid.NewGuid();
        _mockService.Setup(s => s.GetContainerAsync(container.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        _mockGitCloneService.Setup(s => s.PullRepositoryAsync(container.Id, repoId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var result = await _controller.PullRepository(container.Id, repoId, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task PullRepository_UnauthorizedUser_ShouldReturnForbid()
    {
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(false);
        var container = CreateTestContainer(ownerId: "other-user");
        _mockService.Setup(s => s.GetContainerAsync(container.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        var result = await _controller.PullRepository(container.Id, Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task ListRepositories_UnauthorizedUser_ShouldReturnForbid()
    {
        _mockCurrentUser.Setup(u => u.IsAdmin()).Returns(false);
        var container = CreateTestContainer(ownerId: "other-user");
        _mockService.Setup(s => s.GetContainerAsync(container.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container);

        var result = await _controller.ListRepositories(container.Id, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task ListRepositories_OnlyReturnsReposForRequestedContainer()
    {
        var container1 = CreateTestContainer();
        var container2Id = Guid.NewGuid();
        _mockService.Setup(s => s.GetContainerAsync(container1.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(container1);

        _db.ContainerGitRepositories.Add(new ContainerGitRepository
        {
            ContainerId = container1.Id, Url = "https://github.com/owner/mine.git", TargetPath = "/workspace/mine"
        });
        _db.ContainerGitRepositories.Add(new ContainerGitRepository
        {
            ContainerId = container2Id, Url = "https://github.com/owner/theirs.git", TargetPath = "/workspace/theirs"
        });
        await _db.SaveChangesAsync();

        var result = await _controller.ListRepositories(container1.Id, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var repos = ok.Value.Should().BeAssignableTo<IEnumerable<ContainerGitRepositoryDto>>().Subject.ToList();
        repos.Should().HaveCount(1);
        repos[0].Url.Should().Be("https://github.com/owner/mine.git");
    }
}
