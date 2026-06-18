using Andy.Containers.Infrastructure.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Containers.Integration.Tests;

/// <summary>
/// Pins the fix for the "database is locked" 502 on
/// <c>POST /api/images/ensure-pull</c>: the embedded SQLite connection must
/// open with <c>journal_mode=WAL</c> and a non-zero <c>busy_timeout</c> so a
/// writer waits for the lock instead of failing instantly while the hot
/// reconciliation read loop holds it.
///
/// These tests drive the REAL production wiring
/// (<see cref="DatabaseProviderExtensions.ConfigureDbContext"/>) — not a
/// hand-rolled <c>UseSqlite</c> — so a regression that drops the interceptor
/// is caught. <see cref="BareUseSqlite_WithoutInterceptor_HasNoWalOrBusyTimeout"/>
/// is the control: it proves the PRAGMAs come from our interceptor and not
/// from a SQLite default, so this suite fails against the pre-fix code and
/// passes against the fix.
/// </summary>
[Trait("Category", "Integration")]
public class SqlitePragmaConnectionInterceptorTests : IDisposable
{
    private readonly string _dbPath;

    public SqlitePragmaConnectionInterceptorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"andy-containers-pragma-{Guid.NewGuid():N}.sqlite");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* ignore */ }
    }

    [Fact]
    public async Task ConfigureDbContext_Sqlite_OpensConnectionInWalModeWithBusyTimeout()
    {
        var connectionString = $"Data Source={_dbPath}";

        await using var db = BuildProductionContext(connectionString);
        // Materialize the schema so the connection is genuinely opened the way
        // a request would open it (the interceptor fires on ConnectionOpened).
        await db.Database.EnsureCreatedAsync();

        var journalMode = await ScalarAsync(db, "PRAGMA journal_mode;");
        var busyTimeout = await ScalarAsync(db, "PRAGMA busy_timeout;");

        journalMode.Should().BeEquivalentTo("wal",
            "the interceptor must put the embedded DB in WAL mode so readers don't block the writer");
        Convert.ToInt32(busyTimeout).Should().Be(
            SqlitePragmaConnectionInterceptor.BusyTimeoutMilliseconds,
            "a non-zero busy_timeout makes a contended writer wait instead of 502-ing immediately");
    }

    [Fact]
    public async Task BareUseSqlite_WithoutInterceptor_HasNoBusyTimeout()
    {
        // Control: the pre-fix wiring. EF Core's SQLite provider already opens
        // connections in WAL mode by default, so WAL was NOT the missing piece
        // — the operative root cause of the immediate "database is locked" 502
        // is the absence of a busy_timeout: with timeout 0 a writer that hits
        // momentary write/checkpoint contention fails on the first lock instead
        // of waiting. This control proves the busy_timeout comes from our
        // interceptor (so the suite is a real regression guard) — it would pass
        // trivially against the pre-fix code and the production-path test above
        // would fail against it.
        var connectionString = $"Data Source={_dbPath}";
        var options = new DbContextOptionsBuilder<ContainersDbContext>()
            .UseSqlite(connectionString, s => s.MigrationsAssembly("Andy.Containers.Infrastructure"))
            .Options;

        await using var db = new ContainersDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var busyTimeout = await ScalarAsync(db, "PRAGMA busy_timeout;");

        Convert.ToInt32(busyTimeout).Should().Be(0,
            "without the interceptor there is no busy_timeout — the root cause of the immediate lock failure");
    }

    [Fact]
    public async Task WalAndBusyTimeout_LetConcurrentReadAndWriteCoexist()
    {
        // Reproduces the shape of the bug: a long-lived reader holding the DB
        // while a writer commits. Under the default journal + no busy_timeout
        // the write throws SQLite Error 5 'database is locked'. With WAL +
        // busy_timeout it must succeed.
        var connectionString = $"Data Source={_dbPath}";

        await using (var seed = BuildProductionContext(connectionString))
        {
            await seed.Database.EnsureCreatedAsync();
        }

        // Open a reader on its own connection and keep its result set live.
        await using var readerConn = new SqliteConnection(connectionString);
        await readerConn.OpenAsync();
        await ExecAsync(readerConn, $"PRAGMA busy_timeout={SqlitePragmaConnectionInterceptor.BusyTimeoutMilliseconds};");
        await using var readerCmd = readerConn.CreateCommand();
        readerCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
        await using var liveReader = await readerCmd.ExecuteReaderAsync();
        (await liveReader.ReadAsync()).Should().BeTrue("there is at least one table to read");

        // Now commit a write through the production-configured context while the
        // reader is still open. This is the operation that 502-ed.
        Func<Task> write = async () =>
        {
            await using var writer = BuildProductionContext(connectionString);
            await writer.Database.ExecuteSqlRawAsync(
                "CREATE TABLE IF NOT EXISTS pragma_probe (id INTEGER PRIMARY KEY);");
            await writer.Database.ExecuteSqlRawAsync(
                "INSERT INTO pragma_probe (id) VALUES (1);");
        };

        await write.Should().NotThrowAsync(
            "WAL + busy_timeout must let the writer commit while a reader is open, instead of 'database is locked'");
    }

    private static ContainersDbContext BuildProductionContext(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<ContainersDbContext>();
        // The exact call Program.cs / MigrationEntryPoint.cs make in production.
        DatabaseProviderExtensions.ConfigureDbContext(
            builder, DatabaseProvider.Sqlite, connectionString);
        return new ContainersDbContext(builder.Options);
    }

    private static async Task<object?> ScalarAsync(ContainersDbContext db, string sql)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
