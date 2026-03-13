using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class GitCloneServiceTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly GitCloneService _service;

    public GitCloneServiceTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _service = new GitCloneService(_db);
    }

    public void Dispose() => _db.Dispose();

    private async Task<Container> SeedContainer()
    {
        var template = new ContainerTemplate { Code = "t1", Name = "T1", Version = "1.0", BaseImage = "img" };
        var provider = new InfrastructureProvider { Code = "p1", Name = "P1", Type = ProviderType.Docker };
        _db.Templates.Add(template);
        _db.Providers.Add(provider);
        var container = new Container { Name = "c1", OwnerId = "user1", TemplateId = template.Id, ProviderId = provider.Id };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();
        return container;
    }

    [Fact]
    public async Task AddRepositoryAsync_ValidRequest_CreatesRepo()
    {
        var container = await SeedContainer();
        var request = new GitCloneRequest
        {
            Url = "https://github.com/org/repo.git",
            Branch = "main"
        };

        var result = await _service.AddRepositoryAsync(container.Id, request);

        result.Should().NotBeNull();
        result.ContainerId.Should().Be(container.Id);
        result.Url.Should().Be("https://github.com/org/repo.git");
        result.Branch.Should().Be("main");
        result.TargetPath.Should().Be("repo");
        result.Status.Should().Be(CloneStatus.Pending);
    }

    [Fact]
    public async Task AddRepositoryAsync_InvalidUrl_Throws()
    {
        var container = await SeedContainer();
        var request = new GitCloneRequest { Url = "ftp://bad-url", Branch = "main" };

        var act = () => _service.AddRepositoryAsync(container.Id, request);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Invalid git repository URL*");
    }

    [Fact]
    public async Task AddRepositoryAsync_EmbeddedCredentials_Throws()
    {
        var container = await SeedContainer();
        var request = new GitCloneRequest { Url = "https://user:pass@github.com/org/repo.git", Branch = "main" };

        var act = () => _service.AddRepositoryAsync(container.Id, request);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*embedded credentials*");
    }

    [Fact]
    public async Task AddRepositoryAsync_DuplicateTargetPath_ThrowsInvalidOperation()
    {
        var container = await SeedContainer();
        _db.ContainerGitRepositories.Add(new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "https://github.com/org/repo.git",
            Branch = "main",
            TargetPath = "repo"
        });
        await _db.SaveChangesAsync();

        var request = new GitCloneRequest { Url = "https://github.com/other/repo.git", Branch = "main" };

        var act = () => _service.AddRepositoryAsync(container.Id, request);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already in use*");
    }

    [Fact]
    public async Task AddRepositoryAsync_CustomTargetPath_UsesIt()
    {
        var container = await SeedContainer();
        var request = new GitCloneRequest
        {
            Url = "https://github.com/org/repo.git",
            Branch = "develop",
            TargetPath = "custom-dir"
        };

        var result = await _service.AddRepositoryAsync(container.Id, request);

        result.TargetPath.Should().Be("custom-dir");
        result.Branch.Should().Be("develop");
    }

    [Fact]
    public async Task AddRepositoryAsync_MultipleRepos_IncrementsSortOrder()
    {
        var container = await SeedContainer();

        await _service.AddRepositoryAsync(container.Id, new GitCloneRequest
        {
            Url = "https://github.com/org/repo1.git", Branch = "main"
        });
        var second = await _service.AddRepositoryAsync(container.Id, new GitCloneRequest
        {
            Url = "https://github.com/org/repo2.git", Branch = "main"
        });

        second.SortOrder.Should().Be(1);
    }

    [Fact]
    public async Task ListRepositoriesAsync_ReturnsOrderedRepos()
    {
        var container = await SeedContainer();
        _db.ContainerGitRepositories.AddRange(
            new ContainerGitRepository { ContainerId = container.Id, Url = "https://github.com/org/b.git", Branch = "main", TargetPath = "b", SortOrder = 1 },
            new ContainerGitRepository { ContainerId = container.Id, Url = "https://github.com/org/a.git", Branch = "main", TargetPath = "a", SortOrder = 0 }
        );
        await _db.SaveChangesAsync();

        var repos = await _service.ListRepositoriesAsync(container.Id);

        repos.Should().HaveCount(2);
        repos[0].TargetPath.Should().Be("a");
        repos[1].TargetPath.Should().Be("b");
    }

    [Fact]
    public async Task GetRepositoryAsync_Exists_ReturnsRepo()
    {
        var container = await SeedContainer();
        var repo = new ContainerGitRepository { ContainerId = container.Id, Url = "https://github.com/org/repo.git", Branch = "main", TargetPath = "repo" };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        var result = await _service.GetRepositoryAsync(container.Id, repo.Id);

        result.Should().NotBeNull();
        result!.Url.Should().Be("https://github.com/org/repo.git");
    }

    [Fact]
    public async Task GetRepositoryAsync_WrongContainer_ReturnsNull()
    {
        var container = await SeedContainer();
        var repo = new ContainerGitRepository { ContainerId = container.Id, Url = "https://github.com/org/repo.git", Branch = "main", TargetPath = "repo" };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        var result = await _service.GetRepositoryAsync(Guid.NewGuid(), repo.Id);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("https://github.com/org/repo.git", "main", null, 0, false, "git clone --branch main https://github.com/org/repo.git")]
    [InlineData("https://github.com/org/repo.git", "dev", "my-dir", 1, true, "git clone --branch dev --depth 1 --recurse-submodules https://github.com/org/repo.git my-dir")]
    public void GenerateCloneCommand_ReturnsExpected(string url, string branch, string? targetPath, int depth, bool submodules, string expected)
    {
        var result = _service.GenerateCloneCommand(url, branch, targetPath, depth, submodules);

        result.Should().Be(expected);
    }

    [Fact]
    public void GeneratePullCommand_ReturnsExpected()
    {
        var result = _service.GeneratePullCommand("my-repo");

        result.Should().Be("cd my-repo && git pull");
    }
}
