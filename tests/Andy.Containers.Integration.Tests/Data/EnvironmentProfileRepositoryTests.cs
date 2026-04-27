// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Containers.Integration.Tests.Data;

// X1 integration tests: round-trip the EnvironmentProfile entity through a real
// EF Core stack (SQLite in-memory). Exercises the JSON-mapped Capabilities owned
// type, enum-as-string conversions on Kind / SecretsScope / AuditMode, and the
// unique index on Name.
public class EnvironmentProfileRepositoryTests : IAsyncLifetime
{
    private SqliteConnection _conn = null!;
    private ContainersDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();

        var options = new DbContextOptionsBuilder<ContainersDbContext>()
            .UseSqlite(_conn)
            .Options;

        _db = new ContainersDbContext(options);
        await _db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    [Fact]
    public async Task EnvironmentProfile_RoundTrip_PersistsAllFields_IncludingCapabilities()
    {
        var profile = new EnvironmentProfile
        {
            Name = "headless-container",
            DisplayName = "Headless container",
            Kind = EnvironmentKind.HeadlessContainer,
            BaseImageRef = "ghcr.io/rivoli-ai/andy-headless:2026.04",
            Capabilities = new EnvironmentCapabilities
            {
                NetworkAllowlist = ["api.github.com", "*.npmjs.org"],
                SecretsScope = SecretsScope.RunScoped,
                HasGui = false,
                AuditMode = AuditMode.Strict
            }
        };

        _db.EnvironmentProfiles.Add(profile);
        await _db.SaveChangesAsync();

        // Fresh DbContext so we re-hydrate from the database, not the change tracker.
        using var otherDb = new ContainersDbContext(
            new DbContextOptionsBuilder<ContainersDbContext>().UseSqlite(_conn).Options);
        var reloaded = await otherDb.EnvironmentProfiles.SingleAsync(p => p.Id == profile.Id);

        reloaded.Should().BeEquivalentTo(profile, options => options
            .Excluding(p => p.CreatedAt));
    }

    [Fact]
    public async Task EnvironmentProfile_NameIsUnique()
    {
        _db.EnvironmentProfiles.Add(NewProfile("desktop"));
        await _db.SaveChangesAsync();

        _db.EnvironmentProfiles.Add(NewProfile("desktop"));
        var act = async () => await _db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>(
            "the unique index on Name should reject a duplicate");
    }

    private static EnvironmentProfile NewProfile(string name) => new()
    {
        Name = name,
        DisplayName = name,
        Kind = EnvironmentKind.Desktop,
        BaseImageRef = "ghcr.io/rivoli-ai/andy-desktop:2026.04"
    };
}
