// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Andy.Containers.Models.ImageManagement;
using Andy.Containers.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Andy.Containers.Integration.Tests.Data;

// IM3 (rivoli-ai/andy-containers#252). Round-trip the digest-anchored
// schema through a real EF stack (SQLite in-memory). Exercises:
//   - unique constraint on Digest (rejects duplicate physical images)
//   - composite unique on (RegistryId, RepoPath, Tag) (no two refs claim
//     the same coordinate)
//   - SpecHash lookup for content-addressable cache hits
//   - cascade delete: removing an artifact removes its references
//   - SetNull on ContainerImage.BuildArtifactId so legacy rows survive
public class BuildArtifactStoreTests : IAsyncLifetime
{
    private SqliteConnection _conn = null!;
    private ContainersDbContext _db = null!;
    private IBuildArtifactStore _store = null!;
    private Guid _templateId;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();

        var options = new DbContextOptionsBuilder<ContainersDbContext>()
            .UseSqlite(_conn).ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _db = new ContainersDbContext(options);
        await _db.Database.EnsureCreatedAsync();

        _store = new BuildArtifactStore(_db);

        // Seed a template so artifact rows have a valid FK.
        _templateId = Guid.NewGuid();
        _db.Templates.Add(new ContainerTemplate
        {
            Id = _templateId,
            Code = "test-template",
            Name = "Test Template",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
        });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    [Fact]
    public async Task Add_PersistsArtifact()
    {
        var artifact = MakeArtifact("sha256:abc", "sha256:spec1");

        var added = await _store.AddAsync(artifact, CancellationToken.None);

        added.Id.Should().NotBeEmpty();
        var loaded = await _store.GetByDigestAsync("sha256:abc", CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.SpecHash.Should().Be("sha256:spec1");
        loaded.BuildBackendId.Should().Be("local-docker");
    }

    [Fact]
    public async Task Add_RejectsDuplicateDigest()
    {
        await _store.AddAsync(MakeArtifact("sha256:abc", "sha256:spec1"), CancellationToken.None);

        var act = async () => await _store.AddAsync(
            MakeArtifact("sha256:abc", "sha256:spec2"),
            CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>(
            "the unique constraint on Digest enforces 'same bytes ⇒ one row'.");
    }

    [Fact]
    public async Task GetByDigest_ReturnsNull_WhenAbsent()
    {
        var loaded = await _store.GetByDigestAsync("sha256:not-real", CancellationToken.None);
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task GetBySpecHash_FindsCachedArtifact()
    {
        await _store.AddAsync(
            MakeArtifact("sha256:digest-a", "sha256:spec-shared"),
            CancellationToken.None);

        var hit = await _store.GetBySpecHashAsync(
            _templateId,
            "sha256:spec-shared",
            CancellationToken.None);

        hit.Should().NotBeNull();
        hit!.Digest.Should().Be("sha256:digest-a");
    }

    [Fact]
    public async Task GetBySpecHash_IsScopedByTemplate()
    {
        await _store.AddAsync(
            MakeArtifact("sha256:digest-a", "sha256:spec-x", templateId: _templateId),
            CancellationToken.None);

        var otherTemplateId = Guid.NewGuid();
        _db.Templates.Add(new ContainerTemplate
        {
            Id = otherTemplateId,
            Code = "other",
            Name = "Other",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
        });
        await _db.SaveChangesAsync();

        var miss = await _store.GetBySpecHashAsync(
            otherTemplateId,
            "sha256:spec-x",
            CancellationToken.None);

        miss.Should().BeNull(
            "a different template with the same spec hash is a different cache entry.");
    }

    [Fact]
    public async Task AddReference_RejectsDuplicateCoordinate()
    {
        var artifact = await _store.AddAsync(
            MakeArtifact("sha256:abc", "sha256:spec1"),
            CancellationToken.None);

        await _store.AddReferenceAsync(
            artifact.Id,
            MakeReference("local-zot", "foo/bar", "v1"),
            CancellationToken.None);

        var act = async () => await _store.AddReferenceAsync(
            artifact.Id,
            MakeReference("local-zot", "foo/bar", "v1"),
            CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>(
            "(RegistryId, RepoPath, Tag) is unique — only one reference per coordinate.");
    }

    [Fact]
    public async Task AddReference_AllowsSameTagInDifferentRegistry()
    {
        var artifact = await _store.AddAsync(
            MakeArtifact("sha256:abc", "sha256:spec1"),
            CancellationToken.None);

        await _store.AddReferenceAsync(
            artifact.Id,
            MakeReference("local-zot", "foo/bar", "v1"),
            CancellationToken.None);

        // Same tag in a different registry is fine.
        await _store.AddReferenceAsync(
            artifact.Id,
            MakeReference("mycorp-artifactory", "foo/bar", "v1"),
            CancellationToken.None);

        var refs = await _store.ListReferencesAsync(artifact.Id, CancellationToken.None);
        refs.Should().HaveCount(2);
    }

    [Fact]
    public async Task AddReference_ThrowsWhenArtifactMissing()
    {
        var act = async () => await _store.AddReferenceAsync(
            Guid.NewGuid(),
            MakeReference("local-zot", "foo/bar", "v1"),
            CancellationToken.None);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task RemoveReference_IsIdempotent()
    {
        var artifact = await _store.AddAsync(
            MakeArtifact("sha256:abc", "sha256:spec1"),
            CancellationToken.None);
        var reference = await _store.AddReferenceAsync(
            artifact.Id,
            MakeReference("local-zot", "foo/bar", "v1"),
            CancellationToken.None);

        await _store.RemoveReferenceAsync(reference.Id, CancellationToken.None);
        // Second removal is a no-op, not an error.
        await _store.RemoveReferenceAsync(reference.Id, CancellationToken.None);

        var refs = await _store.ListReferencesAsync(artifact.Id, CancellationToken.None);
        refs.Should().BeEmpty();
    }

    [Fact]
    public async Task DeletingArtifact_CascadesReferences()
    {
        var artifact = await _store.AddAsync(
            MakeArtifact("sha256:abc", "sha256:spec1"),
            CancellationToken.None);
        await _store.AddReferenceAsync(
            artifact.Id,
            MakeReference("local-zot", "foo/bar", "v1"),
            CancellationToken.None);

        _db.BuildArtifacts.Remove(artifact);
        await _db.SaveChangesAsync();

        var orphaned = await _db.RegistryReferences
            .Where(r => r.BuildArtifactId == artifact.Id)
            .ToListAsync();
        orphaned.Should().BeEmpty(
            "RegistryReference is cascade-deleted when its artifact is removed — orphan refs are meaningless.");
    }

    [Fact]
    public async Task DeletingArtifact_SetsContainerImageBuildArtifactIdToNull()
    {
        var artifact = await _store.AddAsync(
            MakeArtifact("sha256:abc", "sha256:spec1"),
            CancellationToken.None);

        var legacyImage = new ContainerImage
        {
            Id = Guid.NewGuid(),
            ContentHash = "sha256:legacy",
            Tag = "test:1.0.0-1",
            ImageReference = "test:1.0.0-1",
            BaseImageDigest = "sha256:base",
            DependencyManifest = "{}",
            DependencyLock = "{}",
            TemplateId = _templateId,
            BuildNumber = 1,
            BuildArtifactId = artifact.Id,
        };
        _db.Images.Add(legacyImage);
        await _db.SaveChangesAsync();

        _db.BuildArtifacts.Remove(artifact);
        await _db.SaveChangesAsync();

        // Legacy ContainerImage row survives with BuildArtifactId nulled out.
        var reloaded = await _db.Images.FirstAsync(i => i.Id == legacyImage.Id);
        reloaded.BuildArtifactId.Should().BeNull(
            "OnDelete(SetNull) on ContainerImage.BuildArtifactId keeps legacy rows queryable even after the new artifact row is GC'd.");
    }

    [Fact]
    public async Task ToAbstraction_RoundTripsCoreFields()
    {
        var entity = await _store.AddAsync(
            MakeArtifact("sha256:abc", "sha256:spec1"),
            CancellationToken.None);

        var abstraction = entity.ToAbstraction();

        abstraction.Digest.Should().Be("sha256:abc");
        abstraction.SpecHash.Should().Be("sha256:spec1");
        abstraction.MediaType.Should().Be("application/vnd.oci.image.manifest.v1+json");
        abstraction.SizeBytes.Should().Be(12_345_678L);
        abstraction.LocalReference.Should().BeEmpty(
            "LocalReference is a build-time hint with no DB column; mapping leaves it empty when reading back.");
    }

    private BuildArtifactEntity MakeArtifact(string digest, string specHash, Guid? templateId = null)
        => new()
        {
            Digest = digest,
            MediaType = "application/vnd.oci.image.manifest.v1+json",
            SizeBytes = 12_345_678L,
            SpecHash = specHash,
            TemplateId = templateId ?? _templateId,
            BuildBackendId = "local-docker",
            BuiltBy = "test-user",
            BuiltAt = DateTime.UtcNow,
        };

    private static RegistryReferenceEntity MakeReference(string registryId, string repoPath, string tag)
        => new()
        {
            RegistryId = registryId,
            RepoPath = repoPath,
            Tag = tag,
            PushedAt = DateTime.UtcNow,
            PushedBy = "test-user",
        };
}
