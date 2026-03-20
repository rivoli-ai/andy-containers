using System.Text.Json;
using Andy.Containers.Abstractions;
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

public class ContainerOrchestrationServiceTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IInfrastructureRoutingService> _mockRouting;
    private readonly Mock<IInfrastructureProviderFactory> _mockFactory;
    private readonly Mock<IInfrastructureProvider> _mockInfraProvider;
    private readonly ContainerProvisioningQueue _queue;
    private readonly ContainerOrchestrationService _service;

    public ContainerOrchestrationServiceTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockRouting = new Mock<IInfrastructureRoutingService>();
        _mockFactory = new Mock<IInfrastructureProviderFactory>();
        _mockInfraProvider = new Mock<IInfrastructureProvider>();
        _queue = new ContainerProvisioningQueue();
        var logger = new Mock<ILogger<ContainerOrchestrationService>>();

        _mockFactory.Setup(f => f.GetProvider(It.IsAny<InfrastructureProvider>()))
            .Returns(_mockInfraProvider.Object);

        _service = new ContainerOrchestrationService(_db, _mockRouting.Object, _mockFactory.Object, _queue, logger.Object);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private async Task<(ContainerTemplate template, InfrastructureProvider provider)> SeedTemplateAndProvider()
    {
        var template = new ContainerTemplate
        {
            Code = "test-template",
            Name = "Test Template",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };
        var provider = new InfrastructureProvider
        {
            Code = "test-provider",
            Name = "Test Provider",
            Type = ProviderType.Docker,
            IsEnabled = true
        };
        _db.Templates.Add(template);
        _db.Providers.Add(provider);
        await _db.SaveChangesAsync();
        return (template, provider);
    }

    [Fact]
    public async Task CreateContainer_WithTemplateId_ShouldCreateContainerWithPendingStatus()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        _mockInfraProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        var request = new CreateContainerRequest
        {
            Name = "my-container",
            TemplateId = template.Id,
            ProviderId = provider.Id
        };

        var container = await _service.CreateContainerAsync(request, CancellationToken.None);

        container.Name.Should().Be("my-container");
        container.TemplateId.Should().Be(template.Id);
        container.ProviderId.Should().Be(provider.Id);
        container.Status.Should().Be(ContainerStatus.Pending);
        container.OwnerId.Should().Be("system");
    }

    [Fact]
    public async Task CreateContainer_WithInvalidTemplateId_ShouldThrowArgumentException()
    {
        var request = new CreateContainerRequest
        {
            Name = "bad-container",
            TemplateId = Guid.NewGuid(),
            ProviderId = Guid.NewGuid()
        };

        var act = () => _service.CreateContainerAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Template not found*");
    }

    [Fact]
    public async Task CreateContainer_WithInvalidProviderId_ShouldThrowArgumentException()
    {
        var template = new ContainerTemplate
        {
            Code = "t1",
            Name = "T1",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var request = new CreateContainerRequest
        {
            Name = "bad-container",
            TemplateId = template.Id,
            ProviderId = Guid.NewGuid()
        };

        var act = () => _service.CreateContainerAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Provider not found*");
    }

    [Fact]
    public async Task GetContainer_ExistingId_ShouldReturnContainer()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = new Container
        {
            Name = "existing",
            OwnerId = "user1",
            TemplateId = template.Id,
            ProviderId = provider.Id
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var result = await _service.GetContainerAsync(container.Id, CancellationToken.None);

        result.Name.Should().Be("existing");
        result.Id.Should().Be(container.Id);
    }

    [Fact]
    public async Task GetContainer_NonExistentId_ShouldThrowKeyNotFoundException()
    {
        var act = () => _service.GetContainerAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task StopContainer_NotRunning_ShouldThrowInvalidOperation()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = new Container
        {
            Name = "stopped-container",
            OwnerId = "user1",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            Status = ContainerStatus.Stopped
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var act = () => _service.StopContainerAsync(container.Id, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Stopped*cannot stop*");
    }

    [Fact]
    public async Task DestroyContainer_WithExternalId_ShouldCallProvider()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = new Container
        {
            Name = "to-destroy",
            OwnerId = "user1",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            ExternalId = "ext-destroy-1",
            Status = ContainerStatus.Running
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        _mockInfraProvider.Setup(p => p.DestroyContainerAsync("ext-destroy-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _service.DestroyContainerAsync(container.Id, CancellationToken.None);

        _mockInfraProvider.Verify(p => p.DestroyContainerAsync("ext-destroy-1", It.IsAny<CancellationToken>()), Times.Once);
        var updated = await _db.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Destroyed);
    }

    [Fact]
    public async Task DestroyContainer_WithoutExternalId_ShouldNotCallProviderButStillDestroy()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = new Container
        {
            Name = "pending-destroy",
            OwnerId = "user1",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            ExternalId = null,
            Status = ContainerStatus.Pending
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        await _service.DestroyContainerAsync(container.Id, CancellationToken.None);

        _mockInfraProvider.Verify(p => p.DestroyContainerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var updated = await _db.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Destroyed);
    }

    [Fact]
    public async Task CreateContainer_WithGitRepositories_ShouldCreateRepoEntities()
    {
        var (template, provider) = await SeedTemplateAndProvider();

        var request = new CreateContainerRequest
        {
            Name = "git-container",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            GitRepositories = new List<GitRepositoryConfig>
            {
                new() { Url = "https://github.com/owner/repo1.git", Branch = "main", TargetPath = "/workspace/repo1" },
                new() { Url = "https://github.com/owner/repo2.git", CloneDepth = 1, Submodules = true }
            }
        };

        var container = await _service.CreateContainerAsync(request, CancellationToken.None);

        var repos = await _db.ContainerGitRepositories
            .Where(r => r.ContainerId == container.Id)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();

        repos.Should().HaveCount(2);
        repos[0].Url.Should().Be("https://github.com/owner/repo1.git");
        repos[0].Branch.Should().Be("main");
        repos[0].TargetPath.Should().Be("/workspace/repo1");
        repos[0].CloneStatus.Should().Be(GitCloneStatus.Pending);
        repos[1].Url.Should().Be("https://github.com/owner/repo2.git");
        repos[1].CloneDepth.Should().Be(1);
        repos[1].Submodules.Should().BeTrue();
        repos[1].TargetPath.Should().Be("/workspace"); // default
    }

    [Fact]
    public async Task CreateContainer_WithInvalidGitRepositories_ShouldThrowArgumentException()
    {
        var (template, provider) = await SeedTemplateAndProvider();

        var request = new CreateContainerRequest
        {
            Name = "bad-git",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            GitRepositories = new List<GitRepositoryConfig>
            {
                new() { Url = "file:///etc/passwd" }
            }
        };

        var act = () => _service.CreateContainerAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*https:// and git@*");
    }

    [Fact]
    public async Task CreateContainer_WithInvalidSingleGitRepository_ShouldThrowArgumentException()
    {
        var (template, provider) = await SeedTemplateAndProvider();

        var request = new CreateContainerRequest
        {
            Name = "bad-git",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            GitRepository = new GitRepositoryConfig { Url = "https://user:pass@github.com/owner/repo.git" }
        };

        var act = () => _service.CreateContainerAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Embedded credentials*");
    }

    [Fact]
    public async Task CreateContainer_WithTemplateRepos_ShouldMergeTemplateRepos()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        template.GitRepositories = JsonSerializer.Serialize(new List<GitRepositoryConfig>
        {
            new() { Url = "https://github.com/template/repo.git", Branch = "main" }
        });
        await _db.SaveChangesAsync();

        var request = new CreateContainerRequest
        {
            Name = "merged-repos",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            GitRepositories = new List<GitRepositoryConfig>
            {
                new() { Url = "https://github.com/user/repo.git" }
            }
        };

        var container = await _service.CreateContainerAsync(request, CancellationToken.None);

        var repos = await _db.ContainerGitRepositories
            .Where(r => r.ContainerId == container.Id)
            .ToListAsync();

        repos.Should().HaveCount(2);
        repos.Should().Contain(r => r.Url == "https://github.com/user/repo.git");
        repos.Should().Contain(r => r.Url == "https://github.com/template/repo.git");
    }

    [Fact]
    public async Task CreateContainer_WithExcludeTemplateRepos_ShouldNotMerge()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        template.GitRepositories = JsonSerializer.Serialize(new List<GitRepositoryConfig>
        {
            new() { Url = "https://github.com/template/repo.git" }
        });
        await _db.SaveChangesAsync();

        var request = new CreateContainerRequest
        {
            Name = "no-template-repos",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            ExcludeTemplateRepos = true,
            GitRepositories = new List<GitRepositoryConfig>
            {
                new() { Url = "https://github.com/user/repo.git" }
            }
        };

        var container = await _service.CreateContainerAsync(request, CancellationToken.None);

        var repos = await _db.ContainerGitRepositories
            .Where(r => r.ContainerId == container.Id)
            .ToListAsync();

        repos.Should().HaveCount(1);
        repos[0].Url.Should().Be("https://github.com/user/repo.git");
    }

    [Fact]
    public async Task CreateContainer_TemplateRepos_ShouldBeMarkedAsFromTemplate()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        template.GitRepositories = JsonSerializer.Serialize(new List<GitRepositoryConfig>
        {
            new() { Url = "https://github.com/template/repo.git" }
        });
        await _db.SaveChangesAsync();

        var request = new CreateContainerRequest
        {
            Name = "template-flag-test",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            GitRepositories = new List<GitRepositoryConfig>
            {
                new() { Url = "https://github.com/user/repo.git" }
            }
        };

        var container = await _service.CreateContainerAsync(request, CancellationToken.None);

        var repos = await _db.ContainerGitRepositories
            .Where(r => r.ContainerId == container.Id)
            .ToListAsync();

        var userRepo = repos.Single(r => r.Url == "https://github.com/user/repo.git");
        userRepo.IsFromTemplate.Should().BeFalse();

        var templateRepo = repos.Single(r => r.Url == "https://github.com/template/repo.git");
        templateRepo.IsFromTemplate.Should().BeTrue();
    }

    [Fact]
    public async Task CreateContainer_SingleGitRepository_BackwardCompat_ShouldCreateRepoEntity()
    {
        var (template, provider) = await SeedTemplateAndProvider();

        var request = new CreateContainerRequest
        {
            Name = "backward-compat",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            GitRepository = new GitRepositoryConfig { Url = "https://github.com/owner/repo.git", Branch = "develop" }
            // GitRepositories is null — backward compat path
        };

        var container = await _service.CreateContainerAsync(request, CancellationToken.None);

        var repos = await _db.ContainerGitRepositories
            .Where(r => r.ContainerId == container.Id)
            .ToListAsync();

        repos.Should().HaveCount(1);
        repos[0].Url.Should().Be("https://github.com/owner/repo.git");
        repos[0].Branch.Should().Be("develop");
    }

    [Fact]
    public async Task CreateContainer_WithGitRepositories_ShouldEnqueueJobWithHasGitReposTrue()
    {
        var (template, provider) = await SeedTemplateAndProvider();

        var request = new CreateContainerRequest
        {
            Name = "git-job",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            GitRepositories = new List<GitRepositoryConfig>
            {
                new() { Url = "https://github.com/owner/repo.git" }
            }
        };

        await _service.CreateContainerAsync(request, CancellationToken.None);

        // Read the job from the queue
        var reader = _queue.Reader;
        reader.TryRead(out var job).Should().BeTrue();
        job!.HasGitRepositories.Should().BeTrue();
    }

    [Fact]
    public async Task CreateContainer_WithoutGitRepositories_ShouldEnqueueJobWithHasGitReposFalse()
    {
        var (template, provider) = await SeedTemplateAndProvider();

        var request = new CreateContainerRequest
        {
            Name = "no-git",
            TemplateId = template.Id,
            ProviderId = provider.Id
        };

        await _service.CreateContainerAsync(request, CancellationToken.None);

        var reader = _queue.Reader;
        reader.TryRead(out var job).Should().BeTrue();
        job!.HasGitRepositories.Should().BeFalse();
    }
}
