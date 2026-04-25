using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Andy.Containers.Infrastructure.Data;

/// <summary>
/// Supported database providers for andy-containers.
///
/// SQLite is the default for embedded (Conductor desktop) deployments;
/// PostgreSQL is the production hosted deployment target.
/// </summary>
public enum DatabaseProvider
{
    Sqlite,
    PostgreSql
}

/// <summary>
/// Helpers for switching between database providers based on configuration.
///
/// The provider is selected at startup via the <c>Database:Provider</c>
/// configuration key (or the equivalent <c>Database__Provider</c> environment
/// variable). The hosted layout (Docker, dev, production) pins
/// <c>PostgreSql</c> in <c>appsettings.json</c>; Conductor's embedded
/// launcher overrides that with the <c>Database__Provider=Sqlite</c>
/// environment variable.
/// </summary>
public static class DatabaseProviderExtensions
{
    /// <summary>
    /// Resolves the configured provider, defaulting to
    /// <see cref="DatabaseProvider.PostgreSql"/> when nothing is configured.
    ///
    /// SQLite is for embedded Conductor only — it must be opted into
    /// explicitly via <c>Database__Provider=Sqlite</c> (which Conductor's
    /// embedded launcher sets). The hosted/Docker/IDE/CLI paths read
    /// <c>Database:Provider=PostgreSql</c> from <c>appsettings.json</c>;
    /// the code default acts as a safety net so a broken JSON load can
    /// never silently fall to SQLite under hosted deployments.
    /// </summary>
    public static DatabaseProvider GetDatabaseProvider(IConfiguration configuration)
    {
        var providerString = configuration["Database:Provider"] ?? "PostgreSql";

        return providerString.ToLowerInvariant() switch
        {
            "sqlite" => DatabaseProvider.Sqlite,
            "postgresql" or "postgres" or "npgsql" => DatabaseProvider.PostgreSql,
            _ => throw new InvalidOperationException($"Unsupported database provider: {providerString}")
        };
    }

    public static void ConfigureDbContext(
        DbContextOptionsBuilder options,
        DatabaseProvider provider,
        string connectionString,
        string migrationsAssembly = "Andy.Containers.Infrastructure")
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string is null or empty");
        }

        switch (provider)
        {
            case DatabaseProvider.Sqlite:
                EnsureSqliteDirectory(connectionString);
                options.UseSqlite(connectionString, sqlite =>
                {
                    sqlite.MigrationsAssembly(migrationsAssembly);
                });
                break;

            case DatabaseProvider.PostgreSql:
                var normalized = NormalizePostgresConnectionString(connectionString);
                options.UseNpgsql(normalized, npgsql =>
                {
                    npgsql.MigrationsAssembly(migrationsAssembly);
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "public");
                });
                break;

            default:
                throw new InvalidOperationException($"Unsupported database provider: {provider}");
        }
    }

    public static string ResolveConnectionString(
        IConfiguration configuration,
        DatabaseProvider provider)
    {
        return provider switch
        {
            DatabaseProvider.Sqlite =>
                NotEmpty(configuration.GetConnectionString("Sqlite"))
                ?? DefaultSqliteConnectionString(),

            DatabaseProvider.PostgreSql =>
                NotEmpty(configuration.GetConnectionString("DefaultConnection"))
                ?? throw new InvalidOperationException(
                    "ConnectionStrings:DefaultConnection is not configured. " +
                    "Set the connection string via env var (ConnectionStrings__DefaultConnection) " +
                    "or appsettings.Development.json. appsettings.json ships with an empty value " +
                    "so a Postgres password is never committed to the repo."),

            _ => throw new InvalidOperationException($"Unsupported database provider: {provider}")
        };

        static string? NotEmpty(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string DefaultSqliteConnectionString()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(home, ".andy-containers");
        Directory.CreateDirectory(dir);
        return $"Data Source={Path.Combine(dir, "andy-containers.sqlite")}";
    }

    private static void EnsureSqliteDirectory(string connectionString)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
        var path = builder.DataSource;
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public static string NormalizePostgresConnectionString(string connectionString)
    {
        if (!connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) &&
            !connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }

        var uri = new Uri(connectionString);
        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 5432;
        var database = uri.AbsolutePath.TrimStart('/');
        var userInfo = uri.UserInfo.Split(':');
        var username = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : "postgres";
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";

        var sb = new System.Text.StringBuilder();
        sb.Append($"Host={host};");
        sb.Append($"Port={port};");
        sb.Append($"Database={database};");
        sb.Append($"Username={username};");
        if (!string.IsNullOrEmpty(password))
        {
            sb.Append($"Password={password};");
        }

        return sb.ToString().TrimEnd(';');
    }
}
