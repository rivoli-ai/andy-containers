using System.Text.Json;
using Andy.Containers.Api;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests;

/// <summary>
/// RC3 (#201). Exercises the <c>andy-containers-api migrate</c> CLI
/// entry. The two contracts under test are the ones a Helm hook
/// pipeline depends on:
/// <list type="number">
///   <item>Exit code 0 on success, non-zero on failure.</item>
///   <item>On failure, a single JSON line goes to stderr — Helm hook
///         logs concatenate stderr verbatim, so consumers can branch on
///         <c>error</c> / <c>provider</c> without scraping plain text.</item>
/// </list>
/// SQLite is used here because the migration-history bootstrap path
/// is the more interesting branch (see <c>SqliteMigrationBootstrap</c>);
/// the Postgres path is a thin <c>MigrateAsync</c> wrapper covered
/// implicitly when the integration suite runs against the real DB.
/// </summary>
public class MigrationEntryPointTests : IDisposable
{
    private readonly string _tempDbPath;

    public MigrationEntryPointTests()
    {
        _tempDbPath = Path.Combine(
            Path.GetTempPath(),
            $"rc3-migrate-{Guid.NewGuid():N}.sqlite");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath);
        }
        catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RunAsync_OnFreshSqliteDb_AppliesMigrations_ReturnsZero()
    {
        var args = ConfigArgs(
            ("Database:Provider", "Sqlite"),
            ("ConnectionStrings:Sqlite", $"Data Source={_tempDbPath}"));

        var exitCode = await MigrationEntryPoint.RunAsync(args);

        exitCode.Should().Be(0, "successful migration must surface as exit 0 for the Helm hook");
        File.Exists(_tempDbPath).Should().BeTrue("migration must create the SQLite file");
    }

    [Fact]
    public async Task RunAsync_OnExistingSqliteDb_IsIdempotent_ReturnsZero()
    {
        // Re-running the migrate command must be a no-op exit-0 — Helm
        // hooks may re-run if a release rolls back and forward.
        var args = ConfigArgs(
            ("Database:Provider", "Sqlite"),
            ("ConnectionStrings:Sqlite", $"Data Source={_tempDbPath}"));

        var first = await MigrationEntryPoint.RunAsync(args);
        var second = await MigrationEntryPoint.RunAsync(args);

        first.Should().Be(0);
        second.Should().Be(0, "re-running migrate must be idempotent");
    }

    [Fact]
    public async Task RunAsync_OnUnreachablePostgres_ReturnsOne_AndWritesStructuredStderr()
    {
        // Capture stderr so we can assert the JSON envelope shape. The
        // rest of the pipeline (Helm hook log parser) treats this as
        // the structured failure signal.
        var originalErr = Console.Error;
        using var capturedErr = new StringWriter();
        Console.SetError(capturedErr);

        try
        {
            var args = ConfigArgs(
                ("Database:Provider", "PostgreSql"),
                // Port deliberately closed; libpq fails fast.
                ("ConnectionStrings:DefaultConnection",
                 "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope;Timeout=2"));

            var exitCode = await MigrationEntryPoint.RunAsync(args);

            exitCode.Should().NotBe(0, "an unreachable database must surface as a non-zero exit");
            var stderr = capturedErr.ToString();
            stderr.Should().NotBeEmpty("stderr must carry the failure envelope");

            var jsonLine = stderr
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .First(l => l.TrimStart().StartsWith("{"));
            using var doc = JsonDocument.Parse(jsonLine);
            doc.RootElement.GetProperty("event").GetString().Should().Be("migration_failed");
            doc.RootElement.GetProperty("provider").GetString().Should().Be("PostgreSql");
            doc.RootElement.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
            doc.RootElement.GetProperty("message").GetString().Should().NotBeNullOrEmpty();
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    /// <summary>
    /// Build CLI args of the form <c>--Key=Value</c> that
    /// <c>HostApplicationBuilder</c> picks up via the default command-
    /// line configuration provider.
    /// </summary>
    private static string[] ConfigArgs(params (string Key, string Value)[] kvps)
        => kvps.Select(kv => $"--{kv.Key}={kv.Value}").ToArray();
}
