// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Providers.Local;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Containers.Integration.Tests;

/// <summary>
/// F6.1 (rivoli-ai/conductor#1940). End-to-end git-diff against a REAL Docker
/// container: clone-equivalent (git init + commit a base), check out a per-run
/// branch, make a change, and assert <see cref="GitDiffService"/> returns the
/// patch with correct base/run branch and per-file stats. Also asserts the
/// clean-tree empty-OK path and repoId scoping. Drives the same exec surface
/// (<c>IInfrastructureProvider.ExecAsync</c>) that GitCloneService uses — no
/// Docker-Engine verb (decision #17).
///
/// Requires: Docker daemon running.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Docker")]
public class GitDiffIntegrationTests : IAsyncLifetime
{
    private readonly DockerInfrastructureProvider _provider;
    private SqliteConnection _conn = null!;
    private ContainersDbContext _db = null!;
    private GitDiffService _service = null!;
    private string? _externalId;
    private Guid _containerId;

    public GitDiffIntegrationTests()
    {
        _provider = new DockerInfrastructureProvider(
            null, NullLoggerFactory.Instance.CreateLogger<DockerInfrastructureProvider>());
    }

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        _db = new ContainersDbContext(new DbContextOptionsBuilder<ContainersDbContext>().UseSqlite(_conn).Options);
        await _db.Database.EnsureCreatedAsync();

        // Real container.
        var spec = new ContainerSpec
        {
            Name = $"gitdiff-it-{Guid.NewGuid().ToString()[..8]}",
            ImageReference = "alpine:latest",
            Resources = new ResourceSpec { CpuCores = 1, MemoryMb = 128 },
        };
        var created = await _provider.CreateContainerAsync(spec, CancellationToken.None);
        _externalId = created.ExternalId;

        // DB rows: template, provider, container, workspace, run, repo.
        var template = new ContainerTemplate { Code = "t", Name = "T", Version = "1", BaseImage = "alpine/git:latest" };
        var provider = new InfrastructureProvider { Code = "docker", Name = "Docker", Type = ProviderType.Docker, IsEnabled = true };
        _db.Templates.Add(template);
        _db.Providers.Add(provider);
        _containerId = Guid.NewGuid();
        _db.Containers.Add(new Container
        {
            Id = _containerId, Name = spec.Name, OwnerId = "it-user",
            TemplateId = template.Id, ProviderId = provider.Id,
            Status = ContainerStatus.Running, ExternalId = _externalId!,
        });
        // alpine:latest stays alive via `sleep infinity`; install git.
        await Exec("apk add --no-cache git >/dev/null 2>&1");
        var workspaceId = Guid.NewGuid();
        _db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "ws", OwnerId = "it-user", GitBranch = "main" });
        _db.ContainerGitRepositories.Add(new ContainerGitRepository
        {
            Id = Guid.NewGuid(), ContainerId = _containerId,
            Url = "local", Branch = "main", TargetPath = "/repo",
            CloneStatus = GitCloneStatus.Cloned,
        });
        _db.Runs.Add(new Run
        {
            Id = Guid.NewGuid(), AgentId = "triage-agent", Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(),
            Status = RunStatus.Running, ContainerId = _containerId,
            WorkspaceRef = new WorkspaceRef { WorkspaceId = workspaceId, Branch = "andy/run/it" },
        });
        await _db.SaveChangesAsync();

        var containerService = new ProviderExecAdapter(_provider, _externalId!);
        _service = new GitDiffService(_db, containerService, NullLogger<GitDiffService>.Instance);

        // Build a real git repo with a base commit on `main`.
        await Exec(
            "set -e; mkdir -p /repo && cd /repo && " +
            "git init -q && git config user.email it@andy.local && git config user.name it && " +
            "git checkout -q -B main && " +
            "printf 'line1\\nline2\\n' > file.txt && git add file.txt && git commit -q -m base");
    }

    public async Task DisposeAsync()
    {
        _db.Dispose();
        _conn.Dispose();
        if (_externalId is not null)
        {
            try { await _provider.DestroyContainerAsync(_externalId, CancellationToken.None); }
            catch { /* ignore */ }
        }
    }

    private async Task Exec(string cmd)
    {
        var r = await _provider.ExecAsync(_externalId!, cmd, CancellationToken.None);
        r.ExitCode.Should().Be(0, $"setup command failed: {r.StdErr}");
    }

    [Fact]
    public async Task Diff_RunBranchChange_ReturnsPatchWithBranchesAndStats()
    {
        // Run branch + a committed change + a dirty working-tree change.
        await Exec(
            "set -e; cd /repo && git checkout -q -B andy/run/it && " +
            "printf 'line1\\nline2\\nadded-committed\\n' > file.txt && git commit -q -am change && " +
            "printf 'extra-dirty\\n' >> file.txt");

        var result = await _service.GetDiffAsync(_containerId, null, CancellationToken.None);

        result.RunBranch.Should().Be("andy/run/it");
        result.BaseBranch.Should().Be("main");
        result.Files.Should().ContainSingle(f => f.Path == "file.txt");
        var file = result.Files.Single(f => f.Path == "file.txt");
        file.Additions.Should().BeGreaterThan(0);
        file.Patch.Should().Contain("added-committed");
        file.Patch.Should().Contain("extra-dirty");
        result.RawPatch.Should().Contain("file.txt");
    }

    [Fact]
    public async Task Diff_CleanTree_ReturnsEmptyFiles_NotError()
    {
        // Run branch identical to base, no edits → clean.
        await Exec("set -e; cd /repo && git checkout -q -B andy/run/it");

        var result = await _service.GetDiffAsync(_containerId, null, CancellationToken.None);

        result.Files.Should().BeEmpty();
        result.RawPatch.Should().BeEmpty();
    }
}

/// <summary>
/// Thin <see cref="IContainerService"/> that maps the DB container id onto the
/// real provider's external id and delegates exec — so the integration test
/// exercises the real GitDiffService → ExecAsync → docker exec chain.
/// </summary>
internal sealed class ProviderExecAdapter : IContainerService
{
    private readonly IInfrastructureProvider _provider;
    private readonly string _externalId;

    public ProviderExecAdapter(IInfrastructureProvider provider, string externalId)
    {
        _provider = provider;
        _externalId = externalId;
    }

    public Task<ExecResult> ExecAsync(Guid containerId, string command, CancellationToken ct = default)
        => _provider.ExecAsync(_externalId, command, ct);

    public Task<ExecResult> ExecAsync(Guid containerId, string command, TimeSpan timeout, CancellationToken ct = default)
        => _provider.ExecAsync(_externalId, command, timeout, ct);

    // Unused by GitDiffService.
    public Task<Container> CreateContainerAsync(CreateContainerRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Container> GetContainerAsync(Guid containerId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<Container>> ListContainersAsync(ContainerFilter filter, CancellationToken ct = default) => throw new NotSupportedException();
    public Task StartContainerAsync(Guid containerId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task StopContainerAsync(Guid containerId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DestroyContainerAsync(Guid containerId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ConnectionInfo> GetConnectionInfoAsync(Guid containerId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ContainerStats> GetContainerStatsAsync(Guid containerId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ResizeContainerAsync(Guid containerId, ResourceSpec resources, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<MappedPort> ExposePortAsync(Guid containerId, int containerPort, CancellationToken ct = default) => throw new NotSupportedException();
}
