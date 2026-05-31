using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// F6.1 (rivoli-ai/conductor#1940). GitDiffService runs git diff via the exec
// surface and aggregates per-repo results, resolving the run/base branch from
// the Run row + Workspace.
public class GitDiffServiceTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IContainerService> _exec = new();
    private readonly GitDiffService _service;

    public GitDiffServiceTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _service = new GitDiffService(_db, _exec.Object, NullLogger<GitDiffService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private const string DiffOutput =
        "---NUMSTAT---\n1\t0\tFoo.cs\n---PATCH---\n" +
        "diff --git a/Foo.cs b/Foo.cs\n--- a/Foo.cs\n+++ b/Foo.cs\n@@ -1 +1,2 @@\n line\n+added\n";

    [Fact]
    public async Task GetDiff_SingleRepo_ReturnsParsedFiles_AndBranches()
    {
        var (containerId, _) = SeedContainerRunRepos("main", "andy/run/x", ("/workspace", GitCloneStatus.Cloned));
        _exec.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = DiffOutput });

        var result = await _service.GetDiffAsync(containerId, null);

        result.RunBranch.Should().Be("andy/run/x");
        result.BaseBranch.Should().Be("main");
        result.Files.Should().ContainSingle();
        result.Files[0].Path.Should().Be("Foo.cs");
        result.Files[0].Additions.Should().Be(1);
        result.RawPatch.Should().Contain("+added");
    }

    [Fact]
    public async Task GetDiff_CleanTree_ReturnsEmptyFiles_NotError()
    {
        var (containerId, _) = SeedContainerRunRepos("main", "andy/run/x", ("/workspace", GitCloneStatus.Cloned));
        _exec.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "---NUMSTAT---\n---PATCH---\n" });

        var result = await _service.GetDiffAsync(containerId, null);

        result.Files.Should().BeEmpty();
        result.RawPatch.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDiff_NotAGitRepo_Exit3_TreatedAsEmpty()
    {
        var (containerId, _) = SeedContainerRunRepos("main", "andy/run/x", ("/workspace", GitCloneStatus.Cloned));
        _exec.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 3 });

        var result = await _service.GetDiffAsync(containerId, null);

        result.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDiff_NoClonedRepos_ReturnsEmpty_NoExec()
    {
        var (containerId, _) = SeedContainerRunRepos("main", "andy/run/x", ("/workspace", GitCloneStatus.Pending));

        var result = await _service.GetDiffAsync(containerId, null);

        result.Files.Should().BeEmpty();
        _exec.Verify(s => s.ExecAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetDiff_RepoIdScope_OnlyDiffsThatRepo()
    {
        var (containerId, repoIds) = SeedContainerRunRepos("main", "andy/run/x",
            ("/workspace/a", GitCloneStatus.Cloned),
            ("/workspace/b", GitCloneStatus.Cloned));
        _exec.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = DiffOutput });

        await _service.GetDiffAsync(containerId, repoIds[0]);

        _exec.Verify(s => s.ExecAsync(containerId, It.Is<string>(c => c.Contains("/workspace/a")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        _exec.Verify(s => s.ExecAsync(containerId, It.Is<string>(c => c.Contains("/workspace/b")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetDiff_MultiRepo_AggregatesWithPathPrefixes()
    {
        var (containerId, _) = SeedContainerRunRepos("main", "andy/run/x",
            ("/workspace/a", GitCloneStatus.Cloned),
            ("/workspace/b", GitCloneStatus.Cloned));
        _exec.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = DiffOutput });

        var result = await _service.GetDiffAsync(containerId, null);

        result.Files.Should().HaveCount(2);
        result.Files.Select(f => f.Path).Should().BeEquivalentTo("/workspace/a/Foo.cs", "/workspace/b/Foo.cs");
    }

    private (Guid containerId, List<Guid> repoIds) SeedContainerRunRepos(
        string workspaceBase, string runBranch, params (string Path, GitCloneStatus Status)[] repos)
    {
        var template = new ContainerTemplate { Code = "t", Name = "T", Version = "1", BaseImage = "ubuntu:24.04" };
        var provider = new InfrastructureProvider { Code = "docker", Name = "Docker", Type = ProviderType.Docker, IsEnabled = true };
        _db.Templates.Add(template);
        _db.Providers.Add(provider);

        var workspaceId = Guid.NewGuid();
        _db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "ws", OwnerId = "u", GitBranch = workspaceBase });

        var container = new Container
        {
            Id = Guid.NewGuid(), Name = "c", OwnerId = "u",
            TemplateId = template.Id, ProviderId = provider.Id,
            Status = ContainerStatus.Running, ExternalId = "ext",
        };
        _db.Containers.Add(container);

        var repoIds = new List<Guid>();
        var created = DateTime.UtcNow;
        foreach (var (path, status) in repos)
        {
            var id = Guid.NewGuid();
            repoIds.Add(id);
            _db.ContainerGitRepositories.Add(new ContainerGitRepository
            {
                Id = id,
                ContainerId = container.Id,
                Url = "https://github.com/owner/repo.git",
                Branch = workspaceBase,
                TargetPath = path,
                CloneStatus = status,
                CreatedAt = created,
            });
            created = created.AddSeconds(1);
        }

        _db.Runs.Add(new Run
        {
            Id = Guid.NewGuid(),
            AgentId = "triage-agent",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Status = RunStatus.Running,
            ContainerId = container.Id,
            WorkspaceRef = new WorkspaceRef { WorkspaceId = workspaceId, Branch = runBranch },
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
        return (container.Id, repoIds);
    }
}
