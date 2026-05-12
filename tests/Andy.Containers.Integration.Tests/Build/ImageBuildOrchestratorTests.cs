// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Containers.Abstractions.Images;
using Andy.Containers.Infrastructure.Build;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Registries;
using Andy.Containers.Models;
using Andy.Containers.Models.ImageManagement;
using Andy.Containers.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Andy.Containers.Configuration;
using Andy.Containers.DependencyInjection;
using Moq;
using Xunit;

namespace Andy.Containers.Integration.Tests.Build;

// IM8 (rivoli-ai/andy-containers#262). End-to-end tests of the
// orchestrator's cache-vs-build branching against a real EF stack
// (SQLite in-memory). The build backend and registry adapter are
// mocked because their own tests already exercise their internals;
// this suite proves the *orchestration* logic — what gets called
// when, what gets persisted, and what the response shape is.
public class ImageBuildOrchestratorTests : IAsyncLifetime
{
    private SqliteConnection _conn = null!;
    private ContainersDbContext _db = null!;
    private BuildArtifactStore _store = null!;
    private Mock<IBuildBackend> _backend = null!;
    private Mock<IRegistryAdapter> _adapter = null!;
    private ImageBuildOrchestrator _orchestrator = null!;
    private Guid _templateId;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        var options = new DbContextOptionsBuilder<ContainersDbContext>()
            .UseSqlite(_conn).Options;
        _db = new ContainersDbContext(options);
        await _db.Database.EnsureCreatedAsync();
        _store = new BuildArtifactStore(_db);

        _backend = new Mock<IBuildBackend>();
        _backend.SetupGet(b => b.BackendId).Returns("local");
        _backend.SetupGet(b => b.Capabilities).Returns(new BuildBackendCapabilities(
            false, ["amd64"], true, false, false));

        _adapter = new Mock<IRegistryAdapter>();
        _adapter.SetupGet(a => a.RegistryId).Returns("local-zot");

        // Wire IRegistryConfiguration the same way Program.cs does —
        // through AddImageManagement + an IOptions binding — so the
        // test exercises the production registration path rather than
        // a stub. The internal default impl stays internal that way.
        var services = new ServiceCollection();
        services.AddImageManagement();
        services.Configure<RegistryConfigurationOptions>(opts =>
        {
            opts.PrimaryRegistryId = "local-zot";
            opts.Registries.Add(new RegistryConfigEntry
            {
                Id = "local-zot",
                Kind = "zot",
                Url = "http://localhost:5050",
                IsPrimary = true,
            });
        });
        var registries = services.BuildServiceProvider().GetRequiredService<IRegistryConfiguration>();

        _orchestrator = new ImageBuildOrchestrator(
            _db, _store, registries, [_adapter.Object], _backend.Object,
            NullLogger<ImageBuildOrchestrator>.Instance);

        _templateId = Guid.NewGuid();
        _db.Templates.Add(new ContainerTemplate
        {
            Id = _templateId,
            Code = "test",
            Name = "Test",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            SpecHash = "sha256:test-hash",
        });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    [Fact]
    public async Task BuildAsync_TemplateNotFound_ReturnsFailedWithCode()
    {
        var result = await _orchestrator.BuildAsync(
            new ImageBuildRequest(Guid.NewGuid(), null, false, "user"),
            new Progress<BuildProgressEvent>(_ => { }),
            CancellationToken.None);

        result.Status.Should().Be(BuildResultStatus.Failed);
        result.ErrorCode.Should().Be("template.not_found");
    }

    [Fact]
    public async Task BuildAsync_CacheMiss_BuildsAndPushes()
    {
        SetupBackendBuilds("andy-build:tmp", "sha256:test-hash");
        SetupAdapterPush("local-zot", "test", "sha256-test-hash", returnedDigest: "sha256:abc");

        var result = await _orchestrator.BuildAsync(
            new ImageBuildRequest(_templateId, null, false, "user"),
            new Progress<BuildProgressEvent>(_ => { }),
            CancellationToken.None);

        result.Status.Should().Be(BuildResultStatus.Succeeded);
        result.Digest.Should().Be("sha256:abc");
        result.References.Should().ContainSingle()
            .Which.RegistryId.Should().Be("local-zot");

        // Persistence: BuildArtifactEntity + RegistryReferenceEntity
        // both written under one orchestrator call.
        (await _db.BuildArtifacts.AnyAsync(b => b.Digest == "sha256:abc")).Should().BeTrue();
        (await _db.RegistryReferences.AnyAsync(r => r.RegistryId == "local-zot" && r.Tag == "sha256-test-hash")).Should().BeTrue();
    }

    [Fact]
    public async Task BuildAsync_CacheHit_ReturnsCachedWithoutInvokingBackend()
    {
        // Pre-seed an existing artifact + reference for the template's
        // current spec hash. This is what a previous successful build
        // would have written.
        var artifact = new BuildArtifactEntity
        {
            Id = Guid.NewGuid(),
            Digest = "sha256:cached",
            MediaType = "application/vnd.oci.image.manifest.v1+json",
            SizeBytes = 1000,
            SpecHash = "sha256:test-hash",
            TemplateId = _templateId,
            BuildBackendId = "local",
            BuiltBy = "previous-user",
            BuiltAt = DateTime.UtcNow.AddMinutes(-5),
        };
        _db.BuildArtifacts.Add(artifact);
        _db.RegistryReferences.Add(new RegistryReferenceEntity
        {
            Id = Guid.NewGuid(),
            BuildArtifactId = artifact.Id,
            RegistryId = "local-zot",
            RepoPath = "test",
            Tag = "sha256-test-hash",
            PushedAt = DateTime.UtcNow.AddMinutes(-5),
            PushedBy = "previous-user",
        });
        await _db.SaveChangesAsync();

        var result = await _orchestrator.BuildAsync(
            new ImageBuildRequest(_templateId, null, false, "user"),
            new Progress<BuildProgressEvent>(_ => { }),
            CancellationToken.None);

        result.Status.Should().Be(BuildResultStatus.Cached);
        result.Digest.Should().Be("sha256:cached");
        _backend.Verify(b => b.BuildAsync(
            It.IsAny<TemplateSpec>(),
            It.IsAny<IBuildContext>(),
            It.IsAny<IProgress<BuildProgressEvent>>(),
            It.IsAny<CancellationToken>()),
            Times.Never,
            "the build backend MUST NOT be invoked on a cache hit — that's the whole point of content-addressable caching.");
    }

    [Fact]
    public async Task BuildAsync_ForceFlag_BypassesCache()
    {
        // Same setup as the cache-hit test, but force=true.
        var artifact = new BuildArtifactEntity
        {
            Id = Guid.NewGuid(),
            Digest = "sha256:cached",
            MediaType = "application/vnd.oci.image.manifest.v1+json",
            SizeBytes = 1000,
            SpecHash = "sha256:test-hash",
            TemplateId = _templateId,
            BuildBackendId = "local",
            BuiltBy = "previous-user",
            BuiltAt = DateTime.UtcNow.AddMinutes(-5),
        };
        _db.BuildArtifacts.Add(artifact);
        _db.RegistryReferences.Add(new RegistryReferenceEntity
        {
            Id = Guid.NewGuid(),
            BuildArtifactId = artifact.Id,
            RegistryId = "local-zot",
            RepoPath = "test",
            Tag = "sha256-test-hash",
            PushedAt = DateTime.UtcNow.AddMinutes(-5),
            PushedBy = "previous-user",
        });
        await _db.SaveChangesAsync();

        SetupBackendBuilds("andy-build:rebuilt", "sha256:test-hash");
        SetupAdapterPush("local-zot", "test", "sha256-test-hash-fresh", returnedDigest: "sha256:rebuilt");

        var result = await _orchestrator.BuildAsync(
            new ImageBuildRequest(_templateId, null, Force: true, "user"),
            new Progress<BuildProgressEvent>(_ => { }),
            CancellationToken.None);

        result.Status.Should().Be(BuildResultStatus.Succeeded,
            "Force=true must produce a fresh build even when a cached artifact exists.");
        _backend.Verify(b => b.BuildAsync(
            It.IsAny<TemplateSpec>(),
            It.IsAny<IBuildContext>(),
            It.IsAny<IProgress<BuildProgressEvent>>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task BuildAsync_BuildBackendThrows_ReturnsFailedWithCapturedLogs()
    {
        _backend.Setup(b => b.BuildAsync(
                It.IsAny<TemplateSpec>(),
                It.IsAny<IBuildContext>(),
                It.IsAny<IProgress<BuildProgressEvent>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ImageBuildFailedException(
                backendId: "local",
                capturedLogs: "ERROR Step 7/12 failed",
                specHash: "sha256:test-hash",
                failingStepName: "packages-install",
                message: "build failed at packages-install"));

        var result = await _orchestrator.BuildAsync(
            new ImageBuildRequest(_templateId, null, false, "user"),
            new Progress<BuildProgressEvent>(_ => { }),
            CancellationToken.None);

        result.Status.Should().Be(BuildResultStatus.Failed);
        result.ErrorCode.Should().Be("build.packages-install");
        result.FailureLog.Should().Contain("ERROR Step 7/12");
    }

    [Fact]
    public async Task BuildAsync_RegistryNotConfigured_ReturnsFailed()
    {
        var result = await _orchestrator.BuildAsync(
            new ImageBuildRequest(_templateId, RegistryId: "ghost-registry", false, "user"),
            new Progress<BuildProgressEvent>(_ => { }),
            CancellationToken.None);

        result.Status.Should().Be(BuildResultStatus.Failed);
        result.ErrorCode.Should().Be("registry.not_configured");
    }

    // P1F4 Part B (rivoli-ai/andy-containers#277). When the multipart
    // register path stamped UploadedFilesPath on the template (PR A),
    // the orchestrator must enumerate that staging dir and surface
    // each file via IBuildContext.Files — including files nested in
    // subdirectories, whose LogicalName is the POSIX-style path
    // relative to the staging root.
    [Fact]
    public async Task BuildAsync_WhenTemplateHasUploadedFilesPath_SurfacesFilesViaBuildContext()
    {
        var stagingDir = Directory.CreateTempSubdirectory("p1f4-partb-staging-").FullName;
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(stagingDir, "install-assistants.sh"),
                "#!/usr/bin/env bash\nexit 0\n");
            Directory.CreateDirectory(Path.Combine(stagingDir, "bin"));
            await File.WriteAllTextAsync(
                Path.Combine(stagingDir, "bin", "nested.sh"),
                "echo nested\n");

            var template = await _db.Templates.FindAsync(_templateId);
            template!.UploadedFilesPath = stagingDir;
            await _db.SaveChangesAsync();

            SetupBackendBuilds("andy-build:tmp", "sha256:test-hash");
            SetupAdapterPush("local-zot", "test", "sha256-test-hash", returnedDigest: "sha256:abc");

            IBuildContext? captured = null;
            _backend.Setup(b => b.BuildAsync(
                    It.IsAny<TemplateSpec>(),
                    It.IsAny<IBuildContext>(),
                    It.IsAny<IProgress<BuildProgressEvent>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<TemplateSpec, IBuildContext, IProgress<BuildProgressEvent>, CancellationToken>(
                    (_, ctx, _, _) => captured = ctx)
                .ReturnsAsync(new BuildArtifact(
                    Digest: string.Empty,
                    MediaType: "application/vnd.oci.image.manifest.v1+json",
                    SizeBytes: 1000,
                    SpecHash: "sha256:test-hash",
                    LocalReference: "andy-build:tmp"));

            await _orchestrator.BuildAsync(
                new ImageBuildRequest(_templateId, null, false, "user"),
                new Progress<BuildProgressEvent>(_ => { }),
                CancellationToken.None);

            captured.Should().NotBeNull();
            captured!.Files.Should().HaveCount(2);
            captured.Files.Should().Contain(f => f.LogicalName == "install-assistants.sh"
                && f.AbsolutePath == Path.Combine(stagingDir, "install-assistants.sh"));
            captured.Files.Should().Contain(f => f.LogicalName == "bin/nested.sh"
                && f.AbsolutePath == Path.Combine(stagingDir, "bin", "nested.sh"),
                "nested files must use POSIX-style LogicalName so the build backend writes them at the same relative path inside the Linux build context.");
        }
        finally
        {
            try { Directory.Delete(stagingDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // P1F4 Part B back-compat: JSON register path (or legacy rows
    // pre-#277) leaves UploadedFilesPath null. The orchestrator must
    // still build, with an empty Files collection — exactly the
    // pre-PR-B behaviour.
    [Fact]
    public async Task BuildAsync_WhenUploadedFilesPathIsNull_BuildContextHasNoFiles()
    {
        SetupBackendBuilds("andy-build:tmp", "sha256:test-hash");
        SetupAdapterPush("local-zot", "test", "sha256-test-hash", returnedDigest: "sha256:abc");

        IBuildContext? captured = null;
        _backend.Setup(b => b.BuildAsync(
                It.IsAny<TemplateSpec>(),
                It.IsAny<IBuildContext>(),
                It.IsAny<IProgress<BuildProgressEvent>>(),
                It.IsAny<CancellationToken>()))
            .Callback<TemplateSpec, IBuildContext, IProgress<BuildProgressEvent>, CancellationToken>(
                (_, ctx, _, _) => captured = ctx)
            .ReturnsAsync(new BuildArtifact(
                Digest: string.Empty,
                MediaType: "application/vnd.oci.image.manifest.v1+json",
                SizeBytes: 1000,
                SpecHash: "sha256:test-hash",
                LocalReference: "andy-build:tmp"));

        await _orchestrator.BuildAsync(
            new ImageBuildRequest(_templateId, null, false, "user"),
            new Progress<BuildProgressEvent>(_ => { }),
            CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Files.Should().BeEmpty();
    }

    // P1F4 Part B graceful fallback: a stale UploadedFilesPath
    // (manual /tmp wipe between API restarts) must not crash the
    // build — the orchestrator surfaces an empty Files list and lets
    // the build fail downstream with the engine's own error if the
    // Dockerfile references the missing source.
    [Fact]
    public async Task BuildAsync_WhenUploadedFilesPathMissingOnDisk_BuildContextHasNoFiles()
    {
        var template = await _db.Templates.FindAsync(_templateId);
        template!.UploadedFilesPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}");
        await _db.SaveChangesAsync();

        SetupBackendBuilds("andy-build:tmp", "sha256:test-hash");
        SetupAdapterPush("local-zot", "test", "sha256-test-hash", returnedDigest: "sha256:abc");

        IBuildContext? captured = null;
        _backend.Setup(b => b.BuildAsync(
                It.IsAny<TemplateSpec>(),
                It.IsAny<IBuildContext>(),
                It.IsAny<IProgress<BuildProgressEvent>>(),
                It.IsAny<CancellationToken>()))
            .Callback<TemplateSpec, IBuildContext, IProgress<BuildProgressEvent>, CancellationToken>(
                (_, ctx, _, _) => captured = ctx)
            .ReturnsAsync(new BuildArtifact(
                Digest: string.Empty,
                MediaType: "application/vnd.oci.image.manifest.v1+json",
                SizeBytes: 1000,
                SpecHash: "sha256:test-hash",
                LocalReference: "andy-build:tmp"));

        await _orchestrator.BuildAsync(
            new ImageBuildRequest(_templateId, null, false, "user"),
            new Progress<BuildProgressEvent>(_ => { }),
            CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Files.Should().BeEmpty();
    }

    private void SetupBackendBuilds(string localTag, string specHash)
    {
        _backend.Setup(b => b.BuildAsync(
                It.IsAny<TemplateSpec>(),
                It.IsAny<IBuildContext>(),
                It.IsAny<IProgress<BuildProgressEvent>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BuildArtifact(
                Digest: string.Empty,
                MediaType: "application/vnd.oci.image.manifest.v1+json",
                SizeBytes: 1000,
                SpecHash: specHash,
                LocalReference: localTag));
    }

    private void SetupAdapterPush(string registryId, string repo, string tag, string returnedDigest)
    {
        _adapter.Setup(a => a.PushAsync(
                It.IsAny<BuildArtifact>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((BuildArtifact a, string repoArg, string tagArg, CancellationToken _) =>
                new RegistryReference(
                    Id: Guid.NewGuid(),
                    RegistryId: registryId,
                    RepoPath: repoArg,
                    Tag: tagArg,
                    Digest: returnedDigest,
                    PushedAt: DateTimeOffset.UtcNow,
                    PushedBy: "test-user"));
    }
}
