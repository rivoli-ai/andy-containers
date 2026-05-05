using System.Text.Json;
using Andy.Containers.Api.Data;
using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api;

/// <summary>
/// Console entry point for `dotnet andy-containers-api migrate` (RC3, #201).
/// Builds a minimal host (no Kestrel, no workers, no seeding), applies
/// pending EF migrations, and exits. Sized to slot into a Helm
/// pre-install / pre-upgrade Job (RC6, #204) so the rollout is decoupled
/// from per-pod startup migration races.
/// </summary>
public static class MigrationEntryPoint
{
    /// <summary>
    /// Runs migrations against the configured provider and returns a
    /// process exit code. <c>0</c> on success, non-zero on any failure.
    /// On failure a single JSON line is written to stderr so a Helm
    /// hook log line can be parsed without scraping plain text.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Configuration mirrors Program.cs: appsettings → environment-
        // specific → environment variables → command-line. Keeping the
        // chain identical means a deployed Helm Job sees the same
        // ConnectionStrings__DefaultConnection / Database__Provider env
        // vars as the API pods.

        var dbProvider = DatabaseProviderExtensions.GetDatabaseProvider(builder.Configuration);
        var dbConnectionString = DatabaseProviderExtensions.ResolveConnectionString(
            builder.Configuration, dbProvider);
        builder.Services.AddDbContext<ContainersDbContext>(options =>
        {
            DatabaseProviderExtensions.ConfigureDbContext(options, dbProvider, dbConnectionString);
        });

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("MigrationEntryPoint");

        try
        {
            await ApplyMigrationsAsync(db, loggerFactory);
            logger.LogInformation("Migrations applied successfully (provider={Provider})", dbProvider);
            return 0;
        }
        catch (Exception ex)
        {
            // Single-line JSON to stderr — Helm hook logs concatenate
            // pod stderr verbatim, so a structured shape lets ops
            // pipelines branch on `error` / `provider` without regex.
            var payload = new
            {
                @event = "migration_failed",
                provider = dbProvider.ToString(),
                error = ex.GetType().FullName,
                message = ex.Message,
            };
            await Console.Error.WriteLineAsync(JsonSerializer.Serialize(payload));
            logger.LogError(ex, "Migration failed");
            return 1;
        }
    }

    /// <summary>
    /// Applies pending migrations to <paramref name="db"/>, taking the
    /// SQLite legacy-bootstrap path when the provider is SQLite. Shared
    /// between this entry point and the Program.cs startup branch so
    /// both surfaces stay in lock-step.
    /// </summary>
    public static async Task ApplyMigrationsAsync(
        ContainersDbContext db,
        ILoggerFactory loggerFactory)
    {
        if (db.Database.IsSqlite())
        {
            var bootstrapLogger = loggerFactory.CreateLogger("SqliteMigrationBootstrap");
            await SqliteMigrationBootstrap.EnsureSchemaAsync(db, bootstrapLogger);
        }
        else
        {
            await db.Database.MigrateAsync();
        }
    }
}
