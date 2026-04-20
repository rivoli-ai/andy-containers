// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Containers.Integration.Tests.Data;

// AP1 integration tests: verify the Run entity + its EF configuration survive a
// full round-trip through a real EF Core stack (SQLite in-memory). These
// exercise column mapping, owned-value-object (WorkspaceRef) serialization,
// enum-as-string conversions, and the indexes that the schema declares.
//
// We use SQLite in-memory because it's the one provider that's available
// everywhere (including CI) without extra infrastructure. The Postgres path
// goes through the same EF model and is exercised by the migration that
// `dotnet ef migrations add AddRuns` produced — the migration file itself is
// reviewed as part of the AP1 PR.
public class RunRepositoryTests : IAsyncLifetime
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
    public async Task Run_RoundTrip_PersistsAllScalarFields()
    {
        var runId = Guid.NewGuid();
        var run = new Run
        {
            Id = runId,
            AgentId = "triage-agent",
            AgentRevision = 3,
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            WorkspaceRef = new WorkspaceRef
            {
                WorkspaceId = Guid.NewGuid(),
                Branch = "main"
            },
            PolicyId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Status = RunStatus.Pending
        };

        _db.Runs.Add(run);
        await _db.SaveChangesAsync();

        // New DbContext so we read fresh state, not the tracked entity.
        using var otherDb = new ContainersDbContext(
            new DbContextOptionsBuilder<ContainersDbContext>().UseSqlite(_conn).Options);
        var reloaded = await otherDb.Runs.SingleAsync(r => r.Id == runId);

        reloaded.Should().BeEquivalentTo(run, options => options
            .Excluding(r => r.CreatedAt)
            .Excluding(r => r.UpdatedAt));
    }

    [Fact]
    public async Task Run_EnumsRoundTripAsStrings()
    {
        var run = new Run
        {
            AgentId = "coding-agent",
            Mode = RunMode.Terminal,
            Status = RunStatus.Running,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid()
        };

        _db.Runs.Add(run);
        await _db.SaveChangesAsync();

        // Read the raw string value straight from the column to prove the
        // conversion is string-based (not an int cast). This is the invariant
        // that keeps psql/sqlite-cli debugging readable. Match "any row" (we
        // only inserted one) to sidestep SQLite Guid-storage quirks.
        var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT Mode, Status FROM Runs";
        using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("Terminal");
        reader.GetString(1).Should().Be("Running");
    }

    [Fact]
    public async Task Run_OwnedWorkspaceRef_StoredAsInlinedColumns()
    {
        var workspaceId = Guid.NewGuid();
        var run = new Run
        {
            AgentId = "planning-agent",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            WorkspaceRef = new WorkspaceRef
            {
                WorkspaceId = workspaceId,
                Branch = "feature/x"
            }
        };

        _db.Runs.Add(run);
        await _db.SaveChangesAsync();

        // The owned value object should have mapped to WorkspaceRef_WorkspaceId
        // and WorkspaceRef_Branch columns (not a separate table). Column
        // names are the invariant we assert — the values go through EF's
        // Guid conversion which differs subtly across providers.
        var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM pragma_table_info('Runs') ORDER BY cid";
        using var reader = await cmd.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        columns.Should().Contain("WorkspaceRef_WorkspaceId");
        columns.Should().Contain("WorkspaceRef_Branch");

        // And verify EF round-trips the owned value object end-to-end.
        using var otherDb = new ContainersDbContext(
            new DbContextOptionsBuilder<ContainersDbContext>().UseSqlite(_conn).Options);
        var reloaded = await otherDb.Runs.SingleAsync(r => r.Id == run.Id);
        reloaded.WorkspaceRef.WorkspaceId.Should().Be(workspaceId);
        reloaded.WorkspaceRef.Branch.Should().Be("feature/x");
    }

    [Fact]
    public async Task Run_IndexOnStatus_Exists()
    {
        // SQLite exposes its indexes via sqlite_master; verifying the declared
        // IX_Runs_Status index made it through EnsureCreated ensures the EF
        // configuration didn't silently drop it.
        var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Runs' ORDER BY name";
        using var reader = await cmd.ExecuteReaderAsync();
        var indexes = new List<string>();
        while (await reader.ReadAsync())
        {
            indexes.Add(reader.GetString(0));
        }

        indexes.Should().Contain(i => i.Contains("IX_Runs_Status", StringComparison.Ordinal));
        indexes.Should().Contain(i => i.Contains("IX_Runs_CorrelationId", StringComparison.Ordinal));
        indexes.Should().Contain(i => i.Contains("IX_Runs_AgentId", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_TransitionPersistsThroughSaveChanges()
    {
        var run = new Run
        {
            AgentId = "review-agent",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid()
        };
        _db.Runs.Add(run);
        await _db.SaveChangesAsync();

        run.TransitionTo(RunStatus.Provisioning);
        run.TransitionTo(RunStatus.Running);
        await _db.SaveChangesAsync();

        using var otherDb = new ContainersDbContext(
            new DbContextOptionsBuilder<ContainersDbContext>().UseSqlite(_conn).Options);
        var reloaded = await otherDb.Runs.SingleAsync(r => r.Id == run.Id);

        reloaded.Status.Should().Be(RunStatus.Running);
        reloaded.StartedAt.Should().NotBeNull();
        reloaded.EndedAt.Should().BeNull();
    }

    [Fact]
    public async Task Run_QueryByStatus_UsesIndexedColumn()
    {
        // Not verifying the query plan (SQLite quirks), but proving the
        // materializer recognises the string-backed enum in a WHERE clause.
        for (var i = 0; i < 3; i++)
        {
            _db.Runs.Add(new Run
            {
                AgentId = $"agent-{i}",
                Mode = RunMode.Headless,
                EnvironmentProfileId = Guid.NewGuid(),
                CorrelationId = Guid.NewGuid(),
                Status = i == 0 ? RunStatus.Running : RunStatus.Succeeded
            });
        }
        await _db.SaveChangesAsync();

        var active = await _db.Runs
            .Where(r => r.Status == RunStatus.Running)
            .ToListAsync();

        active.Should().HaveCount(1);
        active[0].AgentId.Should().Be("agent-0");
    }
}
