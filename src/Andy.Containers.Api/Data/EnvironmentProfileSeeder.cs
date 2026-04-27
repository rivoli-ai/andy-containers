using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Andy.Containers.Api.Data;

/// <summary>
/// X2 (rivoli-ai/andy-containers#91). Loads the global EnvironmentProfile
/// catalog from <c>config/environments/global/*.yaml</c> at startup and
/// upserts each by <see cref="EnvironmentProfile.Name"/>. Idempotent —
/// re-running on a populated DB produces no duplicates and never
/// overwrites operator hand-edits made via the API (X3+); only fields
/// that are still at their seed defaults get refreshed.
/// </summary>
/// <remarks>
/// Search-paths logic mirrors <see cref="Controllers.TemplatesController"/>'s
/// so the seeder finds files when the host is launched from the project
/// directory or the repo root. A malformed file is logged and skipped —
/// host startup never aborts on a bad seed entry.
/// </remarks>
public static class EnvironmentProfileSeeder
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Load and upsert all profile YAMLs found under
    /// <c>config/environments/global</c>. Returns the number of rows
    /// inserted or updated for diagnostics; the host doesn't act on it
    /// beyond logging.
    /// </summary>
    public static async Task<int> SeedAsync(
        ContainersDbContext db,
        IHostEnvironment env,
        ILogger logger,
        CancellationToken ct = default)
    {
        var directory = ResolveSeedDirectory(env);
        if (directory is null)
        {
            logger.LogInformation(
                "EnvironmentProfileSeeder: no config/environments/global directory found; skipping seed.");
            return 0;
        }

        var files = Directory.GetFiles(directory, "*.yaml", SearchOption.TopDirectoryOnly);
        if (files.Length == 0)
        {
            logger.LogInformation(
                "EnvironmentProfileSeeder: {Directory} has no *.yaml files; nothing to seed.", directory);
            return 0;
        }

        var changes = 0;
        foreach (var path in files.OrderBy(p => p, StringComparer.Ordinal))
        {
            try
            {
                var yaml = await File.ReadAllTextAsync(path, ct);
                var record = Yaml.Deserialize<SeedRecord>(yaml);
                if (record is null)
                {
                    logger.LogWarning(
                        "EnvironmentProfileSeeder: {Path} parsed to null; skipping.", path);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(record.Code) ||
                    string.IsNullOrWhiteSpace(record.DisplayName) ||
                    string.IsNullOrWhiteSpace(record.BaseImageRef))
                {
                    logger.LogWarning(
                        "EnvironmentProfileSeeder: {Path} missing required fields (code/display_name/base_image_ref); skipping.",
                        path);
                    continue;
                }

                if (!Enum.TryParse<EnvironmentKind>(record.Kind, ignoreCase: true, out var kind))
                {
                    logger.LogWarning(
                        "EnvironmentProfileSeeder: {Path} has unknown kind '{Kind}'; skipping.",
                        path, record.Kind);
                    continue;
                }

                var existing = await db.EnvironmentProfiles
                    .FirstOrDefaultAsync(p => p.Name == record.Code, ct);

                if (existing is null)
                {
                    db.EnvironmentProfiles.Add(new EnvironmentProfile
                    {
                        Id = Guid.NewGuid(),
                        Name = record.Code,
                        DisplayName = record.DisplayName,
                        Kind = kind,
                        BaseImageRef = record.BaseImageRef,
                        Capabilities = MapCapabilities(record.Capabilities, logger, path),
                    });
                    changes++;
                    logger.LogInformation(
                        "EnvironmentProfileSeeder: seeded new profile '{Code}' from {Path}.",
                        record.Code, path);
                }
                else
                {
                    // Idempotent re-run: keep existing rows untouched. The
                    // operator-edit-preservation contract means we don't
                    // overwrite display_name / base_image_ref / capabilities
                    // once a row exists. To force a refresh, the operator
                    // deletes the row via the API and re-runs.
                    logger.LogDebug(
                        "EnvironmentProfileSeeder: profile '{Code}' already exists; leaving untouched.",
                        record.Code);
                }
            }
            catch (YamlException ex)
            {
                logger.LogWarning(ex,
                    "EnvironmentProfileSeeder: malformed YAML in {Path}; skipping. {Message}",
                    path, ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "EnvironmentProfileSeeder: failed to seed {Path}; skipping. {Message}",
                    path, ex.Message);
            }
        }

        if (changes > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "EnvironmentProfileSeeder: persisted {Count} new EnvironmentProfile row(s).", changes);
        }

        return changes;
    }

    private static EnvironmentCapabilities MapCapabilities(
        SeedCapabilities? raw, ILogger logger, string path)
    {
        if (raw is null) return new EnvironmentCapabilities();

        var caps = new EnvironmentCapabilities
        {
            NetworkAllowlist = raw.NetworkAllowlist?.ToList() ?? new(),
            HasGui = raw.HasGui,
        };

        if (!string.IsNullOrWhiteSpace(raw.SecretsScope))
        {
            if (Enum.TryParse<SecretsScope>(raw.SecretsScope, ignoreCase: true, out var s))
            {
                caps.SecretsScope = s;
            }
            else
            {
                logger.LogWarning(
                    "EnvironmentProfileSeeder: {Path} has unknown secrets_scope '{Value}'; falling back to default.",
                    path, raw.SecretsScope);
            }
        }

        if (!string.IsNullOrWhiteSpace(raw.AuditMode))
        {
            if (Enum.TryParse<AuditMode>(raw.AuditMode, ignoreCase: true, out var a))
            {
                caps.AuditMode = a;
            }
            else
            {
                logger.LogWarning(
                    "EnvironmentProfileSeeder: {Path} has unknown audit_mode '{Value}'; falling back to default.",
                    path, raw.AuditMode);
            }
        }

        return caps;
    }

    /// <summary>
    /// Walk likely roots so the seeder works under <c>dotnet run</c> from
    /// the project dir, from the repo root, or from a publish output. Same
    /// shape as <c>TemplatesController.GetConfigSearchPaths</c>; kept
    /// duplicated rather than coupling the controller to the seeder, so
    /// extracting the seeder into a worker process later is a single move.
    /// </summary>
    internal static string? ResolveSeedDirectory(IHostEnvironment env)
    {
        foreach (var candidate in EnumerateCandidates(env))
        {
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> EnumerateCandidates(IHostEnvironment env)
    {
        // Project dir (../..) — matches `dotnet run` from src/Andy.Containers.Api.
        yield return Path.GetFullPath(
            Path.Combine(env.ContentRootPath, "..", "..", "config", "environments", "global"));
        // Repo root.
        yield return Path.Combine(env.ContentRootPath, "config", "environments", "global");
        // Walk up to find it (handles publish output / docker /app).
        var dir = env.ContentRootPath;
        for (var i = 0; i < 5; i++)
        {
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null) yield break;
            var candidate = Path.Combine(parent, "config", "environments", "global");
            if (Directory.Exists(candidate))
            {
                yield return candidate;
                yield break;
            }
            dir = parent;
        }
    }

    // YAML wire shape — mirrors the file format described in
    // config/environments/global/README.md. Mapped to EnvironmentProfile
    // explicitly in MapCapabilities so the catalog entity keeps its
    // semantic types (enums vs. strings).
    private sealed class SeedRecord
    {
        public string Code { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string BaseImageRef { get; set; } = string.Empty;
        public SeedCapabilities? Capabilities { get; set; }
    }

    private sealed class SeedCapabilities
    {
        public List<string>? NetworkAllowlist { get; set; }
        public string? SecretsScope { get; set; }
        public bool HasGui { get; set; }
        public string? AuditMode { get; set; }
    }
}
