using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// F6.1 (rivoli-ai/conductor#1940). RunBranchService checks out
// andy/run/{runId} in every CLONED repo of the run's container and persists
// the branch name into Run.WorkspaceRef.Branch. Best-effort per repo.
public class RunBranchServiceTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IContainerService> _exec = new();
    private readonly RunBranchService _service;

    public RunBranchServiceTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _service = new RunBranchService(_db, _exec.Object, NullLogger<RunBranchService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void BranchNameFor_IsDeterministic()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        IRunBranchService.BranchNameFor(id).Should().Be("andy/run/11111111-1111-1111-1111-111111111111");
    }

    [Fact]
    public async Task EnsureRunBranch_SingleRepo_ChecksOutAndPersistsBranch()
    {
        var (run, containerId) = SeedRunWithRepos(("/workspace/repo", GitCloneStatus.Cloned));
        _exec.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        await _service.EnsureRunBranchAsync(run, containerId);

        run.WorkspaceRef!.Branch.Should().Be($"andy/run/{run.Id}");
        _exec.Verify(s => s.ExecAsync(containerId,
            It.Is<string>(c => c.Contains("checkout -B") && c.Contains($"andy/run/{run.Id}") && c.Contains("/workspace/repo")),
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnsureRunBranch_MultiRepo_BranchesEachClonedRepo()
    {
        var (run, containerId) = SeedRunWithRepos(
            ("/workspace/a", GitCloneStatus.Cloned),
            ("/workspace/b", GitCloneStatus.Cloned));
        _exec.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        await _service.EnsureRunBranchAsync(run, containerId);

        _exec.Verify(s => s.ExecAsync(containerId, It.Is<string>(c => c.Contains("/workspace/a")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        _exec.Verify(s => s.ExecAsync(containerId, It.Is<string>(c => c.Contains("/workspace/b")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        // RunBranchCheckedOut event recorded per repo.
        (await _db.Events.CountAsync(e => e.EventType == ContainerEventType.RunBranchCheckedOut)).Should().Be(2);
    }

    [Fact]
    public async Task EnsureRunBranch_SkipsUnclonedRepos()
    {
        var (run, containerId) = SeedRunWithRepos(
            ("/workspace/cloned", GitCloneStatus.Cloned),
            ("/workspace/pending", GitCloneStatus.Pending),
            ("/workspace/failed", GitCloneStatus.Failed));
        _exec.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        await _service.EnsureRunBranchAsync(run, containerId);

        _exec.Verify(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        _exec.Verify(s => s.ExecAsync(containerId, It.Is<string>(c => c.Contains("/workspace/cloned")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnsureRunBranch_NoClonedRepos_StillPersistsBranchName()
    {
        var (run, containerId) = SeedRunWithRepos(("/workspace/pending", GitCloneStatus.Pending));

        await _service.EnsureRunBranchAsync(run, containerId);

        run.WorkspaceRef!.Branch.Should().Be($"andy/run/{run.Id}");
        _exec.Verify(s => s.ExecAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureRunBranch_CheckoutFailure_DoesNotThrow_StillPersistsBranch()
    {
        var (run, containerId) = SeedRunWithRepos(("/workspace/repo", GitCloneStatus.Cloned));
        _exec.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 1, StdErr = "fatal: not a git repository" });

        var act = async () => await _service.EnsureRunBranchAsync(run, containerId);

        await act.Should().NotThrowAsync();
        run.WorkspaceRef!.Branch.Should().Be($"andy/run/{run.Id}");
        (await _db.Events.CountAsync(e => e.EventType == ContainerEventType.RunBranchCheckedOut)).Should().Be(0);
    }

    [Fact]
    public async Task EnsureRunBranch_ExecThrows_DoesNotAbort()
    {
        var (run, containerId) = SeedRunWithRepos(("/workspace/repo", GitCloneStatus.Cloned));
        _exec.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var act = async () => await _service.EnsureRunBranchAsync(run, containerId);

        await act.Should().NotThrowAsync();
        run.WorkspaceRef!.Branch.Should().Be($"andy/run/{run.Id}");
    }

    private (Run run, Guid containerId) SeedRunWithRepos(params (string Path, GitCloneStatus Status)[] repos)
    {
        var template = new ContainerTemplate { Code = "t", Name = "T", Version = "1", BaseImage = "ubuntu:24.04" };
        var provider = new InfrastructureProvider { Code = "docker", Name = "Docker", Type = ProviderType.Docker, IsEnabled = true };
        _db.Templates.Add(template);
        _db.Providers.Add(provider);
        var container = new Container
        {
            Id = Guid.NewGuid(), Name = "c", OwnerId = "u",
            TemplateId = template.Id, ProviderId = provider.Id,
            Status = ContainerStatus.Running, ExternalId = "ext"
        };
        _db.Containers.Add(container);

        var created = DateTime.UtcNow;
        foreach (var (path, status) in repos)
        {
            _db.ContainerGitRepositories.Add(new ContainerGitRepository
            {
                Id = Guid.NewGuid(),
                ContainerId = container.Id,
                Url = "https://github.com/owner/repo.git",
                Branch = "main",
                TargetPath = path,
                CloneStatus = status,
                CreatedAt = created,
            });
            created = created.AddSeconds(1);
        }

        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = "triage-agent",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Status = RunStatus.Provisioning,
            ContainerId = container.Id,
            WorkspaceRef = new WorkspaceRef { WorkspaceId = Guid.NewGuid() },
        };
        _db.Runs.Add(run);
        _db.SaveChanges();
        return (run, container.Id);
    }
}
