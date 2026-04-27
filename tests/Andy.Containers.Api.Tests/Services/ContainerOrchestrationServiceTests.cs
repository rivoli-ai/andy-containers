using System.Text.Json;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    private readonly Mock<IGitRepositoryProbeService> _mockProbeService;
    private readonly ContainerOrchestrationService _service;

    private readonly IConfiguration _configuration;

    public ContainerOrchestrationServiceTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockRouting = new Mock<IInfrastructureRoutingService>();
        _mockFactory = new Mock<IInfrastructureProviderFactory>();
        _mockInfraProvider = new Mock<IInfrastructureProvider>();
        _queue = new ContainerProvisioningQueue();
        _mockProbeService = new Mock<IGitRepositoryProbeService>();
        var logger = new Mock<ILogger<ContainerOrchestrationService>>();

        _mockFactory.Setup(f => f.GetProvider(It.IsAny<InfrastructureProvider>()))
            .Returns(_mockInfraProvider.Object);

        // Default: probe passes
        _mockProbeService.Setup(p => p.ProbeRepositoriesAsync(It.IsAny<IReadOnlyList<GitRepositoryConfig>>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());

        // Conductor #878. Default test config — empty so the
        // service falls back to DefaultPerUserSimultaneousLimit.
        // Quota tests build their own with a tighter cap.
        _configuration = new ConfigurationBuilder().Build();

        _service = new ContainerOrchestrationService(_db, _mockRouting.Object, _mockFactory.Object, _queue, _mockProbeService.Object, new Mock<IApiKeyService>().Object, _configuration, logger.Object);
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
    public async Task StopContainer_EmitsRunFinishedOutboxRow()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var storyId = Guid.NewGuid();
        var container = new Container
        {
            Name = "to-stop",
            OwnerId = "user1",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            ExternalId = "ext-stop-1",
            Status = ContainerStatus.Running,
            StartedAt = DateTime.UtcNow.AddSeconds(-10),
            StoryId = storyId
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        _mockInfraProvider.Setup(p => p.StopContainerAsync("ext-stop-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _service.StopContainerAsync(container.Id, CancellationToken.None);

        var outbox = await _db.OutboxEntries.SingleAsync();
        outbox.Subject.Should().Be($"andy.containers.events.run.{container.Id}.finished");
        outbox.PublishedAt.Should().BeNull();
        outbox.CorrelationId.Should().Be(storyId);
        outbox.PayloadJson.Should().Contain("\"duration_seconds\"");
    }

    [Fact]
    public async Task DestroyContainer_EmitsRunCancelledOutboxRow()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = new Container
        {
            Name = "to-destroy-evt",
            OwnerId = "user1",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            ExternalId = "ext-destroy-evt",
            Status = ContainerStatus.Running
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        _mockInfraProvider.Setup(p => p.DestroyContainerAsync("ext-destroy-evt", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _service.DestroyContainerAsync(container.Id, CancellationToken.None);

        var outbox = await _db.OutboxEntries.SingleAsync();
        outbox.Subject.Should().Be($"andy.containers.events.run.{container.Id}.cancelled");
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

    [Fact]
    public async Task CreateContainer_WithGitRepos_CallsProbeService()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var request = new CreateContainerRequest
        {
            Name = "probe-test",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            GitRepositories = [new GitRepositoryConfig { Url = "https://github.com/owner/repo.git" }]
        };

        await _service.CreateContainerAsync(request, CancellationToken.None);

        _mockProbeService.Verify(
            p => p.ProbeRepositoriesAsync(
                It.Is<IReadOnlyList<GitRepositoryConfig>>(r => r.Count == 1),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateContainer_ProbeFailure_ThrowsAndDoesNotProvision()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        _mockProbeService
            .Setup(p => p.ProbeRepositoriesAsync(It.IsAny<IReadOnlyList<GitRepositoryConfig>>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "Repository not found or not accessible: https://github.com/owner/bad-repo.git" });

        var request = new CreateContainerRequest
        {
            Name = "probe-fail",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            GitRepositories = [new GitRepositoryConfig { Url = "https://github.com/owner/bad-repo.git" }]
        };

        var act = () => _service.CreateContainerAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*not found or not accessible*bad-repo*");

        // Should not have enqueued anything
        _queue.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task CreateContainer_SkipUrlValidation_BypassesProbe()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var request = new CreateContainerRequest
        {
            Name = "skip-probe",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            SkipUrlValidation = true,
            GitRepositories = [new GitRepositoryConfig { Url = "https://internal.corp/repo.git" }]
        };

        await _service.CreateContainerAsync(request, CancellationToken.None);

        _mockProbeService.Verify(
            p => p.ProbeRepositoriesAsync(It.IsAny<IReadOnlyList<GitRepositoryConfig>>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Container should still be created
        _queue.Reader.TryRead(out var job).Should().BeTrue();
        job!.HasGitRepositories.Should().BeTrue();
    }

    [Fact]
    public async Task CreateContainer_NoGitRepos_DoesNotCallProbe()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var request = new CreateContainerRequest
        {
            Name = "no-repos",
            TemplateId = template.Id,
            ProviderId = provider.Id
        };

        await _service.CreateContainerAsync(request, CancellationToken.None);

        _mockProbeService.Verify(
            p => p.ProbeRepositoriesAsync(It.IsAny<IReadOnlyList<GitRepositoryConfig>>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateContainer_ShouldPersistCreationSource()
    {
        var (template, provider) = await SeedTemplateAndProvider();

        var request = new CreateContainerRequest
        {
            Name = "cli-container",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            Source = CreationSource.Cli,
            ClientInfo = "andy-cli/1.2.0"
        };

        var container = await _service.CreateContainerAsync(request, CancellationToken.None);

        container.CreationSource.Should().Be(CreationSource.Cli);
        container.ClientInfo.Should().Be("andy-cli/1.2.0");

        var persisted = await _db.Containers.FindAsync(container.Id);
        persisted!.CreationSource.Should().Be(CreationSource.Cli);
        persisted.ClientInfo.Should().Be("andy-cli/1.2.0");
    }

    [Fact]
    public async Task CreateContainer_DefaultSource_ShouldBeUnknown()
    {
        var (template, provider) = await SeedTemplateAndProvider();

        var request = new CreateContainerRequest
        {
            Name = "no-source",
            TemplateId = template.Id,
            ProviderId = provider.Id
        };

        var container = await _service.CreateContainerAsync(request, CancellationToken.None);

        container.CreationSource.Should().Be(CreationSource.Unknown);
        container.ClientInfo.Should().BeNull();
    }

    [Fact]
    public async Task ListContainers_FilterBySource_ShouldReturnOnlyMatching()
    {
        var (template, provider) = await SeedTemplateAndProvider();

        _db.Containers.AddRange(
            new Container { Name = "web-1", TemplateId = template.Id, ProviderId = provider.Id, OwnerId = "u1", CreationSource = CreationSource.WebUi },
            new Container { Name = "cli-1", TemplateId = template.Id, ProviderId = provider.Id, OwnerId = "u1", CreationSource = CreationSource.Cli },
            new Container { Name = "api-1", TemplateId = template.Id, ProviderId = provider.Id, OwnerId = "u1", CreationSource = CreationSource.RestApi });
        await _db.SaveChangesAsync();

        var results = await _service.ListContainersAsync(new ContainerFilter { OwnerId = "u1", Source = CreationSource.Cli }, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("cli-1");
    }

    // Conductor #871. Friendly name is generated at create time;
    // OS label is filled by the provisioning worker post-create
    // (covered separately in OsReleaseParserTests).

    [Fact]
    public async Task CreateContainer_StampsAFriendlyName()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        _mockInfraProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        var request = new CreateContainerRequest
        {
            Name = "labeled",
            TemplateId = template.Id,
            ProviderId = provider.Id
        };

        var container = await _service.CreateContainerAsync(request, CancellationToken.None);

        container.FriendlyName.Should().NotBeNullOrEmpty();
        container.FriendlyName.Should().MatchRegex("^[a-z]+-[a-z]+(-\\d+)?$",
            "FriendlyName must be {adjective}-{animal} (with optional numeric suffix on collision)");
    }

    [Fact]
    public async Task CreateContainer_DoesNotReuseFriendlyNameOfLiveContainer()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        _mockInfraProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        // Pre-load the DB with one of every {adj}-{animal} pair.
        // The generator can't pick a fresh combination, so it
        // MUST land on the suffix fallback ("foo-bar-2"). This
        // proves the orchestrator threads `taken` correctly into
        // GenerateAvoiding — without that wiring the generator
        // would happily re-pick a colliding name.
        //
        // OwnerId on the seeds is intentionally NOT "system":
        // the friendly-name avoidance set is global (across all
        // owners) but the #878 quota is per-owner, so seeding
        // these with a different owner exhausts the namespace
        // without tripping the requesting user's quota.
        foreach (var adj in Andy.Containers.FriendlyNameGenerator.Adjectives)
        foreach (var animal in Andy.Containers.FriendlyNameGenerator.Animals)
        {
            _db.Containers.Add(new Container
            {
                Name = $"seed-{adj}-{animal}",
                TemplateId = template.Id,
                ProviderId = provider.Id,
                OwnerId = "namespace-occupier",
                Status = ContainerStatus.Running,
                FriendlyName = $"{adj}-{animal}"
            });
        }
        await _db.SaveChangesAsync();

        var request = new CreateContainerRequest
        {
            Name = "the-new-one",
            TemplateId = template.Id,
            ProviderId = provider.Id
        };

        var container = await _service.CreateContainerAsync(request, CancellationToken.None);

        container.FriendlyName.Should().EndWith("-2",
            "every base name was taken, so the orchestrator must surface the numeric-suffix fallback");
    }

    [Fact]
    public async Task CreateContainer_ReusesNamesOfDestroyedContainers()
    {
        // Destroyed containers don't show in the user's fleet, so
        // their friendly names are free to recycle. Keeps the
        // namespace from accumulating dead reservations.
        var (template, provider) = await SeedTemplateAndProvider();
        _mockInfraProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        // Same exhaustion as above, but everything is Destroyed.
        foreach (var adj in Andy.Containers.FriendlyNameGenerator.Adjectives)
        foreach (var animal in Andy.Containers.FriendlyNameGenerator.Animals)
        {
            _db.Containers.Add(new Container
            {
                Name = $"dead-{adj}-{animal}",
                TemplateId = template.Id,
                ProviderId = provider.Id,
                OwnerId = "system",
                Status = ContainerStatus.Destroyed,
                FriendlyName = $"{adj}-{animal}"
            });
        }
        await _db.SaveChangesAsync();

        var request = new CreateContainerRequest
        {
            Name = "rises-again",
            TemplateId = template.Id,
            ProviderId = provider.Id
        };

        var container = await _service.CreateContainerAsync(request, CancellationToken.None);

        // Suffix-free: the destroyed names didn't block the picker.
        container.FriendlyName.Should().NotEndWith("-2");
        container.FriendlyName.Should().MatchRegex("^[a-z]+-[a-z]+$");
    }

    // MARK: Conductor #878 — per-user simultaneous-container quota

    /// <summary>
    /// Builds a service that respects a custom per-user cap.
    /// Mirrors the production constructor exactly — the only
    /// difference is the IConfiguration we hand it.
    /// </summary>
    private ContainerOrchestrationService BuildServiceWithLimit(int limit)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Containers:PerUserSimultaneousLimit"] = limit.ToString()
            })
            .Build();
        return new ContainerOrchestrationService(
            _db, _mockRouting.Object, _mockFactory.Object, _queue,
            _mockProbeService.Object, new Mock<IApiKeyService>().Object,
            config, new Mock<ILogger<ContainerOrchestrationService>>().Object);
    }

    private async Task SeedContainersFor(string ownerId, int count, ContainerStatus status, ContainerTemplate template, InfrastructureProvider provider)
    {
        for (var i = 0; i < count; i++)
        {
            _db.Containers.Add(new Container
            {
                Name = $"seed-{ownerId}-{i}",
                TemplateId = template.Id,
                ProviderId = provider.Id,
                OwnerId = ownerId,
                Status = status
            });
        }
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task CreateContainer_AtLimit_ThrowsQuotaExceeded()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        _mockInfraProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext", Status = ContainerStatus.Running });

        var service = BuildServiceWithLimit(3);
        await SeedContainersFor("alice", 3, ContainerStatus.Running, template, provider);

        var request = new CreateContainerRequest
        {
            Name = "would-be-fourth",
            OwnerId = "alice",
            TemplateId = template.Id,
            ProviderId = provider.Id
        };

        var act = () => service.CreateContainerAsync(request, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<QuotaExceededException>();
        ex.Which.Limit.Should().Be(3);
        ex.Which.Current.Should().Be(3);
        ex.Which.OwnerId.Should().Be("alice");
    }

    [Fact]
    public async Task CreateContainer_OneBelowLimit_Succeeds()
    {
        // Boundary: at limit-1 you should be allowed to create
        // exactly one more. This pins the off-by-one — the check
        // is `>= limit`, not `> limit`.
        var (template, provider) = await SeedTemplateAndProvider();
        _mockInfraProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext", Status = ContainerStatus.Running });

        var service = BuildServiceWithLimit(3);
        await SeedContainersFor("alice", 2, ContainerStatus.Running, template, provider);

        var request = new CreateContainerRequest
        {
            Name = "the-third-one",
            OwnerId = "alice",
            TemplateId = template.Id,
            ProviderId = provider.Id
        };

        var container = await service.CreateContainerAsync(request, CancellationToken.None);
        container.Name.Should().Be("the-third-one");
    }

    [Fact]
    public async Task CreateContainer_DestroyedRowsDoNotCountTowardsLimit()
    {
        // Destroyed containers no longer hold provider resources
        // and aren't visible to the user, so their slots should
        // recycle. Otherwise a user who hits the cap once and
        // destroys everything is still locked out forever.
        var (template, provider) = await SeedTemplateAndProvider();
        _mockInfraProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext", Status = ContainerStatus.Running });

        var service = BuildServiceWithLimit(3);
        await SeedContainersFor("alice", 10, ContainerStatus.Destroyed, template, provider);

        var request = new CreateContainerRequest
        {
            Name = "rises-again",
            OwnerId = "alice",
            TemplateId = template.Id,
            ProviderId = provider.Id
        };

        var container = await service.CreateContainerAsync(request, CancellationToken.None);
        container.Name.Should().Be("rises-again");
    }

    [Fact]
    public async Task CreateContainer_PendingAndCreatingCountTowardsLimit()
    {
        // Pending / Creating rows are mid-provision but still
        // consume a friendly name and (for Creating) a provider
        // slot. They MUST count or a user could spam Create
        // faster than the worker drains and burst past the cap.
        var (template, provider) = await SeedTemplateAndProvider();

        var service = BuildServiceWithLimit(3);
        await SeedContainersFor("alice", 2, ContainerStatus.Pending, template, provider);
        await SeedContainersFor("alice", 1, ContainerStatus.Creating, template, provider);

        var request = new CreateContainerRequest
        {
            Name = "would-be-fourth",
            OwnerId = "alice",
            TemplateId = template.Id,
            ProviderId = provider.Id
        };

        var act = () => service.CreateContainerAsync(request, CancellationToken.None);
        await act.Should().ThrowAsync<QuotaExceededException>();
    }

    [Fact]
    public async Task CreateContainer_QuotaIsScopedPerOwnerId()
    {
        // Bob's containers must NOT count against Alice's cap.
        // Otherwise the limit becomes a global limit, which is a
        // separate concern (out of scope for #878).
        var (template, provider) = await SeedTemplateAndProvider();
        _mockInfraProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext", Status = ContainerStatus.Running });

        var service = BuildServiceWithLimit(3);
        await SeedContainersFor("bob", 100, ContainerStatus.Running, template, provider);

        var request = new CreateContainerRequest
        {
            Name = "alices-first",
            OwnerId = "alice",
            TemplateId = template.Id,
            ProviderId = provider.Id
        };

        var container = await service.CreateContainerAsync(request, CancellationToken.None);
        container.Name.Should().Be("alices-first");
    }

    [Fact]
    public async Task CreateContainer_DefaultLimitAppliesWhenConfigMissing()
    {
        // No config key set → service falls back to 32. Verify
        // by seeding 32 then asserting the 33rd create fails.
        var (template, provider) = await SeedTemplateAndProvider();

        // _service in the constructor uses an empty IConfiguration.
        await SeedContainersFor("alice", ContainerOrchestrationService.DefaultPerUserSimultaneousLimit,
            ContainerStatus.Running, template, provider);

        var request = new CreateContainerRequest
        {
            Name = "one-too-many",
            OwnerId = "alice",
            TemplateId = template.Id,
            ProviderId = provider.Id
        };

        var act = () => _service.CreateContainerAsync(request, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<QuotaExceededException>();
        ex.Which.Limit.Should().Be(ContainerOrchestrationService.DefaultPerUserSimultaneousLimit);
    }

    [Fact]
    public async Task CreateContainer_NegativeOrZeroConfigFallsBackToDefault()
    {
        // Defensive: if someone fat-fingers the config to 0 or
        // -1, that would mean "no creates allowed at all" which
        // is almost certainly not intended. Treat as default
        // rather than locking everyone out.
        var (template, provider) = await SeedTemplateAndProvider();
        _mockInfraProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext", Status = ContainerStatus.Running });

        var service = BuildServiceWithLimit(0);

        var request = new CreateContainerRequest
        {
            Name = "still-allowed",
            OwnerId = "alice",
            TemplateId = template.Id,
            ProviderId = provider.Id
        };

        var container = await service.CreateContainerAsync(request, CancellationToken.None);
        container.Name.Should().Be("still-allowed");
    }

    // X4 (rivoli-ai/andy-containers#93). EnvironmentProfile resolution.
    // Pin the contract: when a profile is bound, its BaseImageRef wins
    // over the template's image and Kind dictates GuiType. Without a
    // profile, behaviour matches the template (back-compat).

    [Fact]
    public async Task CreateContainer_HeadlessProfile_OverridesImage_AndSkipsVnc()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        // Template has a desktop GuiType so the profile override has
        // something to flip — proves profile beats template, not just
        // adds when the template is silent.
        template.GuiType = "vnc";
        var profile = await SeedProfile(
            "headless-container",
            EnvironmentKind.HeadlessContainer,
            "ghcr.io/rivoli-ai/andy-headless:2026.04");

        await _service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "h1",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            EnvironmentProfileId = profile.Id,
        }, CancellationToken.None);

        _queue.Reader.TryRead(out var job).Should().BeTrue();
        job!.TemplateBaseImage.Should().Be("ghcr.io/rivoli-ai/andy-headless:2026.04");
        job.GuiType.Should().Be("none",
            "headless profile must drop the VNC sidecar even when the template asked for one");
        job.EnvironmentProfileId.Should().Be(profile.Id);
        job.EnvironmentKind.Should().Be("HeadlessContainer");
    }

    [Fact]
    public async Task CreateContainer_TerminalProfile_KeepsNoVnc_UsesProfileImage()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        template.GuiType = "vnc";
        var profile = await SeedProfile(
            "terminal", EnvironmentKind.Terminal, "ghcr.io/rivoli-ai/andy-terminal:latest");

        await _service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "t1",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            EnvironmentProfileId = profile.Id,
        }, CancellationToken.None);

        _queue.Reader.TryRead(out var job).Should().BeTrue();
        job!.TemplateBaseImage.Should().Be("ghcr.io/rivoli-ai/andy-terminal:latest");
        job.GuiType.Should().Be("none");
        job.EnvironmentKind.Should().Be("Terminal");
    }

    [Fact]
    public async Task CreateContainer_DesktopProfile_SetsVncGuiType()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        // Template has no GUI on its own; profile is what flips on VNC.
        template.GuiType = "none";
        var profile = await SeedProfile(
            "desktop", EnvironmentKind.Desktop, "ghcr.io/rivoli-ai/andy-desktop:latest");

        await _service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "d1",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            EnvironmentProfileId = profile.Id,
        }, CancellationToken.None);

        _queue.Reader.TryRead(out var job).Should().BeTrue();
        job!.TemplateBaseImage.Should().Be("ghcr.io/rivoli-ai/andy-desktop:latest");
        job.GuiType.Should().Be("vnc",
            "desktop profile must enable the VNC sidecar even when the template was non-GUI");
    }

    [Fact]
    public async Task CreateContainer_NoProfile_KeepsTemplateImageAndGui()
    {
        // Back-compat: callers that don't bind a profile see exactly
        // the pre-X4 behaviour — template drives image + GUI.
        var (template, provider) = await SeedTemplateAndProvider();
        template.GuiType = "vnc";

        await _service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "t1",
            TemplateId = template.Id,
            ProviderId = provider.Id,
        }, CancellationToken.None);

        _queue.Reader.TryRead(out var job).Should().BeTrue();
        job!.TemplateBaseImage.Should().Be(template.BaseImage);
        job.GuiType.Should().Be("vnc");
        job.EnvironmentProfileId.Should().BeNull();
        job.EnvironmentKind.Should().BeNull();
    }

    [Fact]
    public async Task CreateContainer_UnknownProfileId_Throws()
    {
        var (template, provider) = await SeedTemplateAndProvider();

        var act = () => _service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "x",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            EnvironmentProfileId = Guid.NewGuid(),
        }, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(e => e.Message.Contains("EnvironmentProfile"));
        _queue.Reader.TryRead(out _).Should().BeFalse(
            "a missing profile must short-circuit before any provisioning is enqueued");
    }

    private async Task<EnvironmentProfile> SeedProfile(
        string code, EnvironmentKind kind, string baseImageRef)
    {
        var profile = new EnvironmentProfile
        {
            Id = Guid.NewGuid(),
            Name = code,
            DisplayName = code,
            Kind = kind,
            BaseImageRef = baseImageRef,
        };
        _db.EnvironmentProfiles.Add(profile);
        await _db.SaveChangesAsync();
        return profile;
    }
}
