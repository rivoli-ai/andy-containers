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

public class GitCloneServiceTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IContainerService> _mockContainerService;
    private readonly Mock<IGitCredentialService> _mockCredentialService;
    private readonly IGitCloneService _service;

    public GitCloneServiceTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockContainerService = new Mock<IContainerService>();
        _mockCredentialService = new Mock<IGitCredentialService>();
        var logger = new Mock<ILogger<GitCloneService>>();
        _service = new GitCloneService(_db, _mockContainerService.Object, _mockCredentialService.Object, logger.Object);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private async Task<Container> SeedContainer()
    {
        var template = new ContainerTemplate { Code = "t1", Name = "T1", Version = "1.0", BaseImage = "ubuntu:24.04" };
        var provider = new InfrastructureProvider { Code = "docker", Name = "Docker", Type = ProviderType.Docker, IsEnabled = true };
        _db.Templates.Add(template);
        _db.Providers.Add(provider);
        var container = new Container
        {
            Name = "test-container", OwnerId = "user1",
            TemplateId = template.Id, ProviderId = provider.Id,
            Status = ContainerStatus.Running, ExternalId = "ext-1"
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();
        return container;
    }

    [Fact]
    public async Task CloneRepository_SuccessfulClone_ShouldUpdateStatus()
    {
        var container = await SeedContainer();
        var repo = new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "https://github.com/owner/repo.git",
            TargetPath = "/workspace/repo"
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "Cloning..." });

        var result = await _service.CloneRepositoryAsync(container.Id, repo.Id);

        result.CloneStatus.Should().Be(GitCloneStatus.Cloned);
        result.CloneError.Should().BeNull();
        result.CloneStartedAt.Should().NotBeNull();
        result.CloneCompletedAt.Should().NotBeNull();
        result.CloneCompletedAt.Should().BeOnOrAfter(result.CloneStartedAt!.Value);
    }

    [Fact]
    public async Task CloneRepository_ShouldCreateGitCloneStartedEvent()
    {
        var container = await SeedContainer();
        var repo = new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "https://github.com/owner/repo.git",
            TargetPath = "/workspace/repo"
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        await _service.CloneRepositoryAsync(container.Id, repo.Id);

        var events = await _db.Events.Where(e => e.ContainerId == container.Id).ToListAsync();
        events.Should().ContainSingle(e => e.EventType == ContainerEventType.GitCloneStarted);
        var startedEvent = events.Single(e => e.EventType == ContainerEventType.GitCloneStarted);
        startedEvent.Details.Should().Contain(repo.Url);
        startedEvent.Details.Should().Contain(repo.TargetPath);
    }

    [Fact]
    public async Task CloneRepository_GitCloneStartedEvent_ShouldAppearBeforeClonedEvent()
    {
        var container = await SeedContainer();
        var repo = new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "https://github.com/owner/repo.git",
            TargetPath = "/workspace/repo"
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        await _service.CloneRepositoryAsync(container.Id, repo.Id);

        var events = await _db.Events
            .Where(e => e.ContainerId == container.Id)
            .OrderBy(e => e.Timestamp)
            .ToListAsync();

        var startedIndex = events.FindIndex(e => e.EventType == ContainerEventType.GitCloneStarted);
        var clonedIndex = events.FindIndex(e => e.EventType == ContainerEventType.GitCloned);
        startedIndex.Should().BeLessThan(clonedIndex, "GitCloneStarted should appear before GitCloned");
    }

    [Fact]
    public async Task CloneRepository_SuccessfulClone_ShouldCreateGitClonedEvent()
    {
        var container = await SeedContainer();
        var repo = new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "https://github.com/owner/repo.git",
            TargetPath = "/workspace/repo"
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        await _service.CloneRepositoryAsync(container.Id, repo.Id);

        var events = await _db.Events.Where(e => e.ContainerId == container.Id).ToListAsync();
        events.Should().ContainSingle(e => e.EventType == ContainerEventType.GitCloned);
        events.Single(e => e.EventType == ContainerEventType.GitCloned).Details.Should().Contain(repo.Url);
    }

    [Fact]
    public async Task CloneRepository_FailedClone_ShouldTrackError()
    {
        var container = await SeedContainer();
        var repo = new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "https://github.com/owner/private-repo.git",
            TargetPath = "/workspace/repo"
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 128, StdErr = "fatal: Authentication failed" });

        var result = await _service.CloneRepositoryAsync(container.Id, repo.Id);

        result.CloneStatus.Should().Be(GitCloneStatus.Failed);
        result.CloneError.Should().Contain("Authentication failed");
        result.CloneCompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CloneRepository_FailedClone_ShouldCreateGitCloneFailedEvent()
    {
        var container = await SeedContainer();
        var repo = new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "https://github.com/owner/repo.git",
            TargetPath = "/workspace/repo"
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 1, StdErr = "permission denied" });

        await _service.CloneRepositoryAsync(container.Id, repo.Id);

        var events = await _db.Events.Where(e => e.ContainerId == container.Id).ToListAsync();
        events.Should().ContainSingle(e => e.EventType == ContainerEventType.GitCloneFailed);
        events.Single(e => e.EventType == ContainerEventType.GitCloneFailed).Details.Should().Contain("permission denied");
    }

    [Fact]
    public async Task CloneRepository_WithCredential_ShouldInjectTokenIntoHttpsUrl()
    {
        var container = await SeedContainer();
        var repo = new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "https://github.com/owner/private-repo.git",
            TargetPath = "/workspace/repo",
            CredentialRef = "my-github"
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        _mockCredentialService.Setup(s => s.ResolveTokenAsync("user1", "my-github", "github.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_mytoken123");

        string? cloneCommand = null;
        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.StartsWith("git clone")), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, CancellationToken>((_, cmd, _) => cloneCommand = cmd)
            .ReturnsAsync(new ExecResult { ExitCode = 0 });
        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.Contains("---FILES---")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "" });

        await _service.CloneRepositoryAsync(container.Id, repo.Id);

        cloneCommand.Should().Contain("ghp_mytoken123@github.com");
        // Should NOT contain the original URL without token
        cloneCommand.Should().NotContain("'https://github.com/owner/private-repo.git'");
    }

    [Fact]
    public async Task CloneRepository_SshUrl_ShouldNotInjectCredentialIntoUrl()
    {
        var container = await SeedContainer();
        var repo = new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "git@github.com:owner/repo.git",
            TargetPath = "/workspace/repo",
            CredentialRef = "my-github"
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        _mockCredentialService.Setup(s => s.ResolveTokenAsync("user1", "my-github", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghp_mytoken123");

        string? cloneCommand = null;
        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.StartsWith("git clone")), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, CancellationToken>((_, cmd, _) => cloneCommand = cmd)
            .ReturnsAsync(new ExecResult { ExitCode = 0 });
        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.Contains("---FILES---")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "" });

        await _service.CloneRepositoryAsync(container.Id, repo.Id);

        // SSH URL should remain unchanged — no token injection
        cloneCommand.Should().Contain("git@github.com:owner/repo.git");
        cloneCommand.Should().NotContain("ghp_mytoken123");
    }

    [Fact]
    public async Task CloneRepository_NoCredentialMatch_ShouldCloneWithOriginalUrl()
    {
        var container = await SeedContainer();
        var repo = new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "https://github.com/owner/public-repo.git",
            TargetPath = "/workspace/repo"
            // No CredentialRef set
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        _mockCredentialService.Setup(s => s.ResolveTokenAsync("user1", null, "github.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        string? cloneCommand = null;
        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.StartsWith("git clone")), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, CancellationToken>((_, cmd, _) => cloneCommand = cmd)
            .ReturnsAsync(new ExecResult { ExitCode = 0 });
        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.Contains("---FILES---")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "" });

        await _service.CloneRepositoryAsync(container.Id, repo.Id);

        cloneCommand.Should().Contain("https://github.com/owner/public-repo.git");
    }

    [Fact]
    public async Task CloneRepository_ShallowClone_ShouldIncludeDepthFlag()
    {
        var container = await SeedContainer();
        var repo = new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "https://github.com/owner/repo.git",
            TargetPath = "/workspace/repo",
            CloneDepth = 1
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        string? cloneCommand = null;
        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.StartsWith("git clone")), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, CancellationToken>((_, cmd, _) => cloneCommand = cmd)
            .ReturnsAsync(new ExecResult { ExitCode = 0 });
        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.Contains("---FILES---")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "" });

        await _service.CloneRepositoryAsync(container.Id, repo.Id);

        cloneCommand.Should().Contain("--depth 1");
    }

    [Fact]
    public async Task CloneRepository_WithSubmodules_ShouldIncludeFlag()
    {
        var container = await SeedContainer();
        var repo = new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "https://github.com/owner/repo.git",
            TargetPath = "/workspace/repo",
            Submodules = true
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        string? cloneCommand = null;
        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.StartsWith("git clone")), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, CancellationToken>((_, cmd, _) => cloneCommand = cmd)
            .ReturnsAsync(new ExecResult { ExitCode = 0 });
        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.Contains("---FILES---")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "" });

        await _service.CloneRepositoryAsync(container.Id, repo.Id);

        cloneCommand.Should().Contain("--recurse-submodules");
    }

    [Fact]
    public async Task CloneRepository_WithBranch_ShouldIncludeBranchFlag()
    {
        var container = await SeedContainer();
        var repo = new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "https://github.com/owner/repo.git",
            TargetPath = "/workspace/repo",
            Branch = "develop"
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        string? cloneCommand = null;
        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.StartsWith("git clone")), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, CancellationToken>((_, cmd, _) => cloneCommand = cmd)
            .ReturnsAsync(new ExecResult { ExitCode = 0 });
        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.Contains("---FILES---")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "" });

        await _service.CloneRepositoryAsync(container.Id, repo.Id);

        // Branch is POSIX-quoted (defense-in-depth — also rejected by validator)
        cloneCommand.Should().Contain("--branch 'develop'");
    }

    [Fact]
    public async Task CloneRepository_NoOptionalFlags_ShouldBuildMinimalCommand()
    {
        var container = await SeedContainer();
        var repo = new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "https://github.com/owner/repo.git",
            TargetPath = "/workspace/repo"
            // No branch, no depth, no submodules
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        string? cloneCommand = null;
        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.StartsWith("git clone")), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, CancellationToken>((_, cmd, _) => cloneCommand = cmd)
            .ReturnsAsync(new ExecResult { ExitCode = 0 });
        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.Contains("---FILES---")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "" });

        await _service.CloneRepositoryAsync(container.Id, repo.Id);

        cloneCommand.Should().StartWith("git clone ");
        cloneCommand.Should().NotContain("--depth");
        cloneCommand.Should().NotContain("--branch");
        cloneCommand.Should().NotContain("--recurse-submodules");
        cloneCommand.Should().Contain("/workspace/repo");
    }

    [Fact]
    public async Task CloneRepositories_ShouldCloneAllPending()
    {
        var container = await SeedContainer();
        _db.ContainerGitRepositories.Add(new ContainerGitRepository
        {
            ContainerId = container.Id, Url = "https://github.com/owner/repo1.git", TargetPath = "/workspace/repo1"
        });
        _db.ContainerGitRepositories.Add(new ContainerGitRepository
        {
            ContainerId = container.Id, Url = "https://github.com/owner/repo2.git", TargetPath = "/workspace/repo2"
        });
        await _db.SaveChangesAsync();

        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        await _service.CloneRepositoriesAsync(container.Id);

        // 2 clone commands + 2 metadata collection commands
        _mockContainerService.Verify(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.StartsWith("git clone")), It.IsAny<CancellationToken>()), Times.Exactly(2));

        var repos = await _db.ContainerGitRepositories.Where(r => r.ContainerId == container.Id).ToListAsync();
        repos.Should().OnlyContain(r => r.CloneStatus == GitCloneStatus.Cloned);
    }

    [Fact]
    public async Task CloneRepositories_ShouldSkipNonPendingRepos()
    {
        var container = await SeedContainer();
        _db.ContainerGitRepositories.Add(new ContainerGitRepository
        {
            ContainerId = container.Id, Url = "https://github.com/owner/repo1.git", TargetPath = "/workspace/repo1",
            CloneStatus = GitCloneStatus.Cloned // Already cloned
        });
        _db.ContainerGitRepositories.Add(new ContainerGitRepository
        {
            ContainerId = container.Id, Url = "https://github.com/owner/repo2.git", TargetPath = "/workspace/repo2",
            CloneStatus = GitCloneStatus.Pending // Should be cloned
        });
        await _db.SaveChangesAsync();

        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        await _service.CloneRepositoriesAsync(container.Id);

        // Only the Pending repo should be cloned (1 clone + 1 metadata)
        _mockContainerService.Verify(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.StartsWith("git clone")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CloneRepositories_OneFailsShouldContinueToNext()
    {
        var container = await SeedContainer();
        _db.ContainerGitRepositories.Add(new ContainerGitRepository
        {
            ContainerId = container.Id, Url = "https://github.com/owner/fail-repo.git", TargetPath = "/workspace/fail"
        });
        _db.ContainerGitRepositories.Add(new ContainerGitRepository
        {
            ContainerId = container.Id, Url = "https://github.com/owner/success-repo.git", TargetPath = "/workspace/success"
        });
        await _db.SaveChangesAsync();

        // Clone commands: first fails, second succeeds. Metadata collection also calls ExecAsync.
        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.Contains("fail-repo")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 128, StdErr = "fatal: repo not found" });
        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.Contains("success-repo")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });
        // Metadata collection call (after successful clone)
        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.Contains("---FILES---")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "" });

        // Should NOT throw — failed clones don't fail the container
        await _service.CloneRepositoriesAsync(container.Id);

        // Both repos should have been attempted
        _mockContainerService.Verify(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.StartsWith("git clone")), It.IsAny<CancellationToken>()), Times.Exactly(2));

        var repos = await _db.ContainerGitRepositories.Where(r => r.ContainerId == container.Id).ToListAsync();
        // One should have failed, one should have succeeded
        repos.Should().ContainSingle(r => r.CloneStatus == GitCloneStatus.Failed);
        repos.Should().ContainSingle(r => r.CloneStatus == GitCloneStatus.Cloned);
        repos.Single(r => r.CloneStatus == GitCloneStatus.Failed).CloneError.Should().Contain("repo not found");
    }

    [Fact]
    public async Task PullRepository_ClonedRepo_ShouldPullAndCreateEvent()
    {
        var container = await SeedContainer();
        var repo = new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "https://github.com/owner/repo.git",
            TargetPath = "/workspace/repo",
            CloneStatus = GitCloneStatus.Cloned
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.Contains("git pull")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "Already up to date." });

        var result = await _service.PullRepositoryAsync(container.Id, repo.Id);

        result.CloneStatus.Should().Be(GitCloneStatus.Cloned);
        result.CloneError.Should().BeNull();
        result.CloneCompletedAt.Should().NotBeNull();

        var events = await _db.Events.Where(e => e.ContainerId == container.Id).ToListAsync();
        events.Should().ContainSingle(e => e.EventType == ContainerEventType.GitPulled);
    }

    [Fact]
    public async Task PullRepository_ExecUsesCorrectPath()
    {
        var container = await SeedContainer();
        var repo = new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "https://github.com/owner/repo.git",
            TargetPath = "/home/dev/myproject",
            CloneStatus = GitCloneStatus.Cloned
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        string? pullCommand = null;
        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.Contains("git pull")), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, CancellationToken>((_, cmd, _) => pullCommand = cmd)
            .ReturnsAsync(new ExecResult { ExitCode = 0 });
        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.Is<string>(cmd => cmd.Contains("---FILES---")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "" });

        await _service.PullRepositoryAsync(container.Id, repo.Id);

        pullCommand.Should().Be("cd '/home/dev/myproject' && git pull");
    }

    [Fact]
    public async Task PullRepository_PullFails_ShouldRevertToClonedAndTrackError()
    {
        var container = await SeedContainer();
        var repo = new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "https://github.com/owner/repo.git",
            TargetPath = "/workspace/repo",
            CloneStatus = GitCloneStatus.Cloned
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        _mockContainerService.Setup(s => s.ExecAsync(container.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 1, StdErr = "merge conflict" });

        var result = await _service.PullRepositoryAsync(container.Id, repo.Id);

        result.CloneStatus.Should().Be(GitCloneStatus.Cloned); // Reverted, not Failed
        result.CloneError.Should().Contain("Pull failed").And.Contain("merge conflict");
    }

    [Fact]
    public async Task PullRepository_NotCloned_ShouldThrow()
    {
        var container = await SeedContainer();
        var repo = new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "https://github.com/owner/repo.git",
            TargetPath = "/workspace/repo",
            CloneStatus = GitCloneStatus.Pending
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        var act = () => _service.PullRepositoryAsync(container.Id, repo.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Pending*cannot pull*");
    }

    [Fact]
    public async Task PullRepository_FailedRepo_ShouldThrow()
    {
        var container = await SeedContainer();
        var repo = new ContainerGitRepository
        {
            ContainerId = container.Id,
            Url = "https://github.com/owner/repo.git",
            TargetPath = "/workspace/repo",
            CloneStatus = GitCloneStatus.Failed
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        var act = () => _service.PullRepositoryAsync(container.Id, repo.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Failed*cannot pull*");
    }

    [Fact]
    public async Task CloneRepository_NonExistentRepo_ShouldThrow()
    {
        var container = await SeedContainer();

        var act = () => _service.CloneRepositoryAsync(container.Id, Guid.NewGuid());

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task PullRepository_NonExistentRepo_ShouldThrow()
    {
        var container = await SeedContainer();

        var act = () => _service.PullRepositoryAsync(container.Id, Guid.NewGuid());

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task CloneRepository_RepoFromDifferentContainer_ShouldThrow()
    {
        var container1 = await SeedContainer();
        // Create second container with different template/provider (avoiding unique constraint)
        var template2 = new ContainerTemplate { Code = "t2", Name = "T2", Version = "1.0", BaseImage = "ubuntu:24.04" };
        _db.Templates.Add(template2);
        var provider2 = new InfrastructureProvider { Code = "docker2", Name = "Docker2", Type = ProviderType.Docker, IsEnabled = true };
        _db.Providers.Add(provider2);
        var container2 = new Container
        {
            Name = "other-container", OwnerId = "user1",
            TemplateId = template2.Id, ProviderId = provider2.Id,
            Status = ContainerStatus.Running, ExternalId = "ext-2"
        };
        _db.Containers.Add(container2);

        var repo = new ContainerGitRepository
        {
            ContainerId = container2.Id,
            Url = "https://github.com/owner/repo.git",
            TargetPath = "/workspace/repo"
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync();

        // Try to clone repo from container2 using container1's ID
        var act = () => _service.CloneRepositoryAsync(container1.Id, repo.Id);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }
}
