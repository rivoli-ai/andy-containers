using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Andy.Containers.Infrastructure.Data;

/// <summary>
/// Applies SQLite reliability PRAGMAs to every connection as soon as it opens.
///
/// Why this exists (the "database is locked" 502 on /api/images/ensure-pull):
/// the embedded Conductor deployment runs andy-containers against a single
/// SQLite file that is hit concurrently by a hot reconciliation read loop
/// (<c>SELECT ... WHERE Status IN (...)</c>) and by writes such as the
/// image-pull bookkeeping that <c>ensure-pull</c> performs. Under SQLite's
/// default rollback journal (<c>journal_mode=DELETE</c>) readers and writers
/// are mutually exclusive, and with no <c>busy_timeout</c> a write that finds
/// the database locked fails *immediately* with
/// <c>SQLite Error 5: 'database is locked'</c>, which surfaces to the app as a
/// 502 and trips the client circuit breaker.
///
/// One PRAGMA is applied, deliberately:
///  * <c>busy_timeout=<see cref="BusyTimeoutMilliseconds"/></c> — THE operative
///    fix. A writer that hits momentary write/checkpoint contention waits and
///    retries for up to the timeout instead of failing on the first lock.
///    SQLite defaults this to 0 (fail immediately) and it is per-connection, so
///    it must be re-applied on every open — which is why an interceptor, not a
///    one-time setup, is required. Critically, <c>busy_timeout</c> is a pure
///    session setting that NEVER writes the database file, so it is safe to run
///    on the very first connection EF opens during its existence/migration
///    probe.
///
/// We deliberately do NOT issue <c>journal_mode=WAL</c> here: EF Core's SQLite
/// provider already opens connections in WAL by default, and re-issuing the
/// PRAGMA is a HEADER WRITE that throws <c>SQLite Error 8: 'attempt to write a
/// readonly database'</c> when it runs on the read-only existence probe
/// (<c>SqliteDatabaseCreator.Exists()</c>) at startup — which crashed the
/// service. WAL is relied upon, not re-asserted.
///
/// PostgreSQL (the hosted target) does not use this interceptor.
/// </summary>
public sealed class SqlitePragmaConnectionInterceptor : DbConnectionInterceptor
{
    /// <summary>
    /// How long a blocked writer waits for the lock before giving up. Five
    /// seconds is comfortably longer than the reconciliation read loop's
    /// hold time, so legitimate writes wait it out rather than 502.
    /// </summary>
    public const int BusyTimeoutMilliseconds = 5000;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    private static void ApplyPragmas(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = PragmaCommandText;
        command.ExecuteNonQuery();
    }

    private static async Task ApplyPragmasAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = PragmaCommandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // busy_timeout is a per-connection session setting (no DB write), so it is
    // safe on every open including EF's startup existence probe.
    private static string PragmaCommandText =>
        $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
}
