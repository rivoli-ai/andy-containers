using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Containers.Integration.Tests;

/// <summary>
/// Probes for #883: can we swap the SQLite startup path from
/// <c>EnsureCreatedAsync</c> to <c>MigrateAsync</c> without
/// rewriting any existing migrations?
///
/// Today (April 2026) <c>Program.cs</c> takes two branches:
///   • PostgreSQL → <c>MigrateAsync</c>
///   • SQLite      → <c>EnsureCreatedAsync</c>
/// The SQLite branch never alters an existing schema, so model
/// changes silently break every existing user's DB. Three different
/// columns hit this in the past two days.
///
/// The fix candidate is "swap to <c>MigrateAsync</c> on both
/// branches" — but the comment in <c>Program.cs</c> claims SQLite
/// migrations aren't portable because the Designer files use
/// Npgsql column types (<c>uuid</c>, <c>jsonb</c>,
/// <c>timestamp with time zone</c>). EF Core's SQLite provider has
/// translated these to <c>TEXT</c> for years, so the comment is
/// likely outdated — but we need to prove it before flipping the
/// switch.
///
/// Two scenarios pinned here:
///
/// 1. <see cref="MigrateAsync_AppliesAllMigrationsToFreshDb"/> —
///    cleanest case: empty SQLite file, run <c>MigrateAsync</c>,
///    verify every committed migration applies. If THIS fails,
///    option (1) in the issue body is dead and we need the parallel
///    SQLite-migration set or the drop-and-recreate fallback.
///
/// 2. <see cref="MigrateAsync_HandlesExistingUserSchema_NoHistory"/>
///    — the actual production scenario: a user's existing DB has
///    the schema (created by an earlier <c>EnsureCreatedAsync</c>)
///    but no <c>__EFMigrationsHistory</c> table. <c>MigrateAsync</c>
///    will try to apply every migration from scratch — which means
///    <c>CREATE TABLE Containers</c> against a DB that already has
///    it. If THIS fails (likely), we need the bare-schema bootstrap
///    that pre-populates the migration history.
/// </summary>
[Trait("Category", "Integration")]
public class SqliteAutoMigrationProbeTests : IDisposable
{
    private readonly string _dbPath;

    public SqliteAutoMigrationProbeTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"andy-containers-probe-{Guid.NewGuid():N}.sqlite");
    }

    public void Dispose()
    {
        // Force any open SQLite connections to flush + close before delete.
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* ignore */ }
    }

    [Fact]
    public async Task MigrateAsync_AppliesAllMigrationsToFreshDb()
    {
        // Empty SQLite file → MigrateAsync should apply every
        // migration in order. This is the easy case; if Npgsql
        // types like `uuid` / `jsonb` / `timestamp with time zone`
        // are translatable by EF Core's SQLite provider, this passes.
        var connectionString = $"Data Source={_dbPath}";

        await using (var db = BuildContext(connectionString))
        {
            await db.Database.MigrateAsync();

            // Assert every migration ran.
            var applied = await db.Database.GetAppliedMigrationsAsync();
            var pending = await db.Database.GetPendingMigrationsAsync();
            applied.Should().NotBeEmpty(
                "MigrateAsync should report at least one applied migration on a fresh DB");
            pending.Should().BeEmpty(
                "no migrations should remain pending after a fresh MigrateAsync");
        }

        // Re-open and verify a column from the latest known migrations
        // is actually present (not just listed in the history table).
        await using (var conn = new SqliteConnection(connectionString))
        {
            await conn.OpenAsync();
            var columns = await GetColumnsAsync(conn, "Containers");
            columns.Should().Contain("FriendlyName",
                "the AddFriendlyNameAndOsLabel migration must have created this column");
            var workspaceColumns = await GetColumnsAsync(conn, "Workspaces");
            workspaceColumns.Should().Contain("EnvironmentProfileId",
                "the AddWorkspaceEnvironmentProfile migration must have added this column");
        }
    }

    [Fact]
    public async Task MigrateAsync_HandlesExistingUserSchema_NoHistory()
    {
        // Mimic the production scenario: a user whose DB was
        // created by `EnsureCreatedAsync` (the current branch)
        // before today's switch to `MigrateAsync`. The schema
        // exists; the `__EFMigrationsHistory` table doesn't.
        //
        // What we want: `MigrateAsync` recognizes this state and
        // doesn't blow up — either by detecting "schema exists,
        // history empty → seed history" or by being naturally
        // idempotent. This test is the canary.
        var connectionString = $"Data Source={_dbPath}";

        // Step 1: simulate an existing-user DB.
        await using (var db = BuildContext(connectionString))
        {
            await db.Database.EnsureCreatedAsync();
        }

        // Confirm the setup: schema exists, history absent.
        await using (var conn = new SqliteConnection(connectionString))
        {
            await conn.OpenAsync();
            var tables = await GetTablesAsync(conn);
            tables.Should().Contain("Containers",
                "EnsureCreatedAsync should have created the Containers table");
            tables.Should().NotContain("__EFMigrationsHistory",
                "EnsureCreatedAsync does not create the migration history table — that's the whole problem");
        }

        // Step 2: run the bootstrap. It should detect the
        // schema-without-history state, seed the history with the
        // current migration ids, then call MigrateAsync (which is
        // a no-op because everything's now marked applied).
        await using (var db = BuildContext(connectionString))
        {
            await SqliteMigrationBootstrap.EnsureSchemaAsync(
                db, NullLoggerFactory.Instance.CreateLogger("test"));

            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            var pending = await db.Database.GetPendingMigrationsAsync();
            applied.Should().NotBeEmpty(
                "the bootstrap should have seeded the migration history");
            pending.Should().BeEmpty(
                "after the bootstrap + MigrateAsync, no migrations should be pending");
        }

        // Verify the schema is intact — the bootstrap should not
        // have dropped anything.
        await using (var conn = new SqliteConnection(connectionString))
        {
            await conn.OpenAsync();
            var tables = await GetTablesAsync(conn);
            tables.Should().Contain("Containers");
            tables.Should().Contain("__EFMigrationsHistory");
        }
    }

    [Fact]
    public async Task EnsureSchemaAsync_IsIdempotentOnAlreadyMigratedDb()
    {
        // Second-launch case: the user's DB was bootstrapped on a
        // previous launch (history is now populated). Running the
        // bootstrap again should pass through to MigrateAsync
        // without re-seeding.
        var connectionString = $"Data Source={_dbPath}";

        // First run: bootstrap from empty.
        await using (var db = BuildContext(connectionString))
        {
            await SqliteMigrationBootstrap.EnsureSchemaAsync(
                db, NullLoggerFactory.Instance.CreateLogger("test"));
        }

        // Second run: same bootstrap, no failure, no duplicate
        // history rows.
        await using (var db = BuildContext(connectionString))
        {
            await SqliteMigrationBootstrap.EnsureSchemaAsync(
                db, NullLoggerFactory.Instance.CreateLogger("test"));

            var pending = await db.Database.GetPendingMigrationsAsync();
            pending.Should().BeEmpty();
        }

        // History table should have exactly one row per migration.
        await using (var conn = new SqliteConnection(connectionString))
        {
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) - COUNT(DISTINCT \"MigrationId\") FROM \"__EFMigrationsHistory\"";
            var dupCount = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
            dupCount.Should().Be(0,
                "no duplicate migration ids should appear in __EFMigrationsHistory");
        }
    }

    [Fact]
    public async Task DetectStateAsync_DistinguishesAllThreeCases()
    {
        var connectionString = $"Data Source={_dbPath}";

        // Empty.
        await using (var db = BuildContext(connectionString))
        {
            // Touch the connection so the file is created but empty.
            await db.Database.GetDbConnection().OpenAsync();
            (await SqliteMigrationBootstrap.DetectStateAsync(db))
                .Should().Be(SqliteMigrationBootstrap.DbState.Empty);
        }

        // Schema without history (legacy EnsureCreatedAsync state).
        await using (var db = BuildContext(connectionString))
        {
            await db.Database.EnsureCreatedAsync();
            (await SqliteMigrationBootstrap.DetectStateAsync(db))
                .Should().Be(SqliteMigrationBootstrap.DbState.SchemaWithoutHistory);
        }

        // Has history (after bootstrap).
        await using (var db = BuildContext(connectionString))
        {
            await SqliteMigrationBootstrap.EnsureSchemaAsync(
                db, NullLoggerFactory.Instance.CreateLogger("test"));
            (await SqliteMigrationBootstrap.DetectStateAsync(db))
                .Should().Be(SqliteMigrationBootstrap.DbState.HasHistory);
        }
    }

    private static ContainersDbContext BuildContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ContainersDbContext>()
            .UseSqlite(
                connectionString,
                sqlite => sqlite.MigrationsAssembly("Andy.Containers.Infrastructure"))
            .Options;
        return new ContainersDbContext(options);
    }

    private static async Task<HashSet<string>> GetColumnsAsync(SqliteConnection conn, string table)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync();
        // PRAGMA table_info columns: cid, name, type, notnull, dflt_value, pk.
        while (await reader.ReadAsync())
        {
            cols.Add(reader.GetString(1));
        }
        return cols;
    }

    private static async Task<HashSet<string>> GetTablesAsync(SqliteConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }
        return tables;
    }
}
