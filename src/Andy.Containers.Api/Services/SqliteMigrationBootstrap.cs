using System.Data;
using Andy.Containers.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

/// <summary>
/// Bridges existing SQLite databases that pre-date the
/// <c>MigrateAsync</c> swap into the proper migration-history flow.
/// Conductor #883.
///
/// Until this commit, the SQLite startup branch in
/// <see cref="Program"/> called <c>EnsureCreatedAsync</c> — which
/// creates the schema once if the file is missing and then is a
/// no-op forever after. Existing users therefore have:
///
/// <list type="bullet">
///   <item><description>All current data tables (Containers,
///     Workspaces, Providers, …)</description></item>
///   <item><description>NO <c>__EFMigrationsHistory</c> table — EF
///     never tracked anything because <c>EnsureCreatedAsync</c>
///     skips it.</description></item>
/// </list>
///
/// If we naively swap to <c>MigrateAsync</c>, EF sees an empty
/// history and tries to apply every migration from scratch — which
/// fails on the first <c>CREATE TABLE</c> against a table that
/// already exists.
///
/// This bootstrap detects that state and pre-populates the history
/// with every committed migration id, marking them all as already
/// applied. <c>MigrateAsync</c> then runs as a no-op for these
/// existing users; for any future migration the user's update
/// hasn't applied yet, <c>MigrateAsync</c> picks up where the
/// history leaves off.
///
/// Caveat: this assumes the user's existing schema matches the
/// current model — which is true if their DB was created by an
/// equally-current <c>EnsureCreatedAsync</c>. A user who skipped
/// several Conductor versions could in theory have a partial
/// schema; that case still ships better than today (today they
/// 500-storm; with this bootstrap, the un-applied migrations get
/// marked "applied" and the user sees a "no such column" error
/// only on the missing column, not a global storm). A future
/// improvement would diff the model snapshot against actual schema
/// and synthesise missing-column ALTERs; out of scope for the
/// initial fix.
/// </summary>
public static class SqliteMigrationBootstrap
{
    /// <summary>
    /// Ensures the SQLite database is in a state where
    /// <c>MigrateAsync</c> can run successfully — seeds the
    /// migration history if tables exist but the history is missing.
    /// Postgres is unaffected (caller filters before invoking).
    ///
    /// Internal consumers should always call <see cref="EnsureSchemaAsync"/>
    /// rather than calling <c>MigrateAsync</c> directly on the
    /// SQLite path.
    /// </summary>
    public static async Task EnsureSchemaAsync(
        ContainersDbContext db,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
        {
            // Caller mis-routed us. Be defensive but loud.
            throw new InvalidOperationException(
                $"[SQLITE-BOOTSTRAP] called against non-SQLite provider: {db.Database.ProviderName}");
        }

        var state = await DetectStateAsync(db, ct).ConfigureAwait(false);
        switch (state)
        {
            case DbState.Empty:
                logger.LogInformation(
                    "[SQLITE-BOOTSTRAP] empty DB — letting MigrateAsync create everything from scratch");
                break;

            case DbState.HasHistory:
                logger.LogDebug(
                    "[SQLITE-BOOTSTRAP] DB already tracked by EF — passing through to MigrateAsync");
                break;

            case DbState.SchemaWithoutHistory:
                logger.LogInformation(
                    "[SQLITE-BOOTSTRAP] schema present without migration history — seeding __EFMigrationsHistory with current migrations (Conductor #883)");
                await SeedMigrationHistoryAsync(db, ct).ConfigureAwait(false);
                break;
        }

        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Three observable states a SQLite DB can be in at startup.
    /// Internal for tests.
    /// </summary>
    internal enum DbState
    {
        /// <summary>No data tables and no history — fresh install.</summary>
        Empty,
        /// <summary><c>__EFMigrationsHistory</c> exists — DB is already tracked.</summary>
        HasHistory,
        /// <summary>Data tables exist but no history — created by an older
        /// <c>EnsureCreatedAsync</c> call before the migration swap.</summary>
        SchemaWithoutHistory,
    }

    internal static async Task<DbState> DetectStateAsync(DbContext db, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var openedHere = false;
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            openedHere = true;
        }

        try
        {
            // Does the history table exist?
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'";
                var hasHistory = Convert.ToInt32(
                    await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0) > 0;
                if (hasHistory) return DbState.HasHistory;
            }

            // No history. Any of the canonical data tables present?
            // The list is intentionally short — these are the tables
            // every install has had since the InitialCreate migration.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN " +
                    "('Containers', 'Workspaces', 'Providers', 'Templates')";
                var hasSchema = Convert.ToInt32(
                    await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0) > 0;
                return hasSchema ? DbState.SchemaWithoutHistory : DbState.Empty;
            }
        }
        finally
        {
            if (openedHere) await conn.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates <c>__EFMigrationsHistory</c> and inserts a row for
    /// every committed migration id, marking them all applied. The
    /// product version stamped is the current EF runtime version —
    /// matches what <c>MigrateAsync</c> would write.
    /// Internal for tests.
    /// </summary>
    internal static async Task SeedMigrationHistoryAsync(DbContext db, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct).ConfigureAwait(false);

        using (var create = conn.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                )
                """;
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // EF gives us the canonical migration ids (in sort order)
        // straight from the migrations assembly. Inserting each one
        // marks it applied; if any already exist (defensive), the
        // INSERT OR IGNORE skips them.
        var migrations = db.Database.GetMigrations().ToList();
        var productVersion = typeof(DbContext).Assembly
            .GetName().Version?.ToString() ?? "8.0.0";

        foreach (var migrationId in migrations)
        {
            using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ($id, $ver)
                """;
            var idParam = insert.CreateParameter();
            idParam.ParameterName = "$id";
            idParam.Value = migrationId;
            insert.Parameters.Add(idParam);
            var verParam = insert.CreateParameter();
            verParam.ParameterName = "$ver";
            verParam.Value = productVersion;
            insert.Parameters.Add(verParam);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
