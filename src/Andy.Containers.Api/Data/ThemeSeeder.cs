using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Andy.Containers.Api.Data;

/// <summary>
/// Conductor #886. Loads the global Theme catalog from
/// <c>config/themes/global/*.yaml</c> at startup and upserts each
/// by <see cref="Theme.Id"/>. Idempotent on re-run; existing rows
/// get their mutable fields (display_name, palette, version)
/// refreshed so an operator-edited YAML re-deploys cleanly.
///
/// Search-paths logic mirrors
/// <see cref="EnvironmentProfileSeeder"/>'s — same project-dir /
/// repo-root / parent-walk pattern. A malformed file is logged
/// and skipped; host startup never aborts on a bad seed.
///
/// Why upsert (not insert-only) on re-run, unlike
/// EnvironmentProfileSeeder?
/// EnvironmentProfile rows are operator-editable via the API
/// (X3+) — we deliberately don't overwrite an operator's
/// hand-tuned profile from a stale YAML. Themes are read-only:
/// no <c>POST /api/themes</c> in v1, so the YAML is the only
/// source of truth and we always converge to it.
/// </summary>
public static class ThemeSeeder
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Load and upsert every theme YAML found under
    /// <c>config/themes/global</c>. Returns the number of rows
    /// inserted or updated for diagnostics.
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
                "ThemeSeeder: no config/themes/global directory found; skipping seed.");
            return 0;
        }

        var files = Directory.GetFiles(directory, "*.yaml", SearchOption.TopDirectoryOnly);
        if (files.Length == 0)
        {
            logger.LogInformation(
                "ThemeSeeder: {Directory} has no *.yaml files; nothing to seed.", directory);
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
                        "ThemeSeeder: {Path} parsed to null; skipping.", path);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(record.Id) ||
                    string.IsNullOrWhiteSpace(record.Name) ||
                    string.IsNullOrWhiteSpace(record.DisplayName) ||
                    string.IsNullOrWhiteSpace(record.Kind))
                {
                    logger.LogWarning(
                        "ThemeSeeder: {Path} missing required field (id/name/display_name/kind); skipping.",
                        path);
                    continue;
                }

                if (record.Palette is null || record.Palette.Count == 0)
                {
                    logger.LogWarning(
                        "ThemeSeeder: {Path} has empty palette; skipping.", path);
                    continue;
                }

                // Serialise the palette dictionary as compact JSON
                // for storage. The Conductor client deserialises
                // back to its own typed Theme model — keeping the
                // backend column dialect-portable (TEXT in SQLite,
                // also TEXT in Postgres for ergonomic equality
                // checks against the catalog row).
                var paletteJson = JsonSerializer.Serialize(
                    record.Palette,
                    new JsonSerializerOptions { WriteIndented = false });

                var existing = await db.Themes
                    .FirstOrDefaultAsync(t => t.Id == record.Id, ct);

                if (existing is null)
                {
                    db.Themes.Add(new Theme
                    {
                        Id = record.Id,
                        Name = record.Name,
                        DisplayName = record.DisplayName,
                        Kind = record.Kind,
                        PaletteJson = paletteJson,
                        Version = record.Version <= 0 ? 1 : record.Version,
                    });
                    changes++;
                    logger.LogInformation(
                        "ThemeSeeder: seeded new theme '{Id}' from {Path}.",
                        record.Id, path);
                }
                else
                {
                    var mutated = false;
                    if (existing.Name != record.Name) { existing.Name = record.Name; mutated = true; }
                    if (existing.DisplayName != record.DisplayName) { existing.DisplayName = record.DisplayName; mutated = true; }
                    if (existing.Kind != record.Kind) { existing.Kind = record.Kind; mutated = true; }
                    if (existing.PaletteJson != paletteJson) { existing.PaletteJson = paletteJson; mutated = true; }
                    var newVersion = record.Version <= 0 ? 1 : record.Version;
                    if (existing.Version != newVersion) { existing.Version = newVersion; mutated = true; }

                    if (mutated)
                    {
                        changes++;
                        logger.LogInformation(
                            "ThemeSeeder: refreshed theme '{Id}' from {Path}.",
                            record.Id, path);
                    }
                    else
                    {
                        logger.LogDebug(
                            "ThemeSeeder: theme '{Id}' unchanged; leaving as-is.",
                            record.Id);
                    }
                }
            }
            catch (YamlException ex)
            {
                logger.LogWarning(ex,
                    "ThemeSeeder: malformed YAML in {Path}; skipping. {Message}",
                    path, ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "ThemeSeeder: failed to seed {Path}; skipping. {Message}",
                    path, ex.Message);
            }
        }

        if (changes > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "ThemeSeeder: persisted {Count} theme row(s).", changes);
        }

        return changes;
    }

    /// <summary>
    /// Walk likely roots so the seeder works under <c>dotnet run</c>
    /// from the project dir, from the repo root, or from a publish
    /// output. Same shape as <see cref="EnvironmentProfileSeeder"/>;
    /// duplicated deliberately so an extraction into a worker
    /// process later is a single move.
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
            Path.Combine(env.ContentRootPath, "..", "..", "config", "themes", "global"));
        // Repo root.
        yield return Path.Combine(env.ContentRootPath, "config", "themes", "global");
        // Walk up to find it (handles publish output / docker /app).
        var dir = env.ContentRootPath;
        for (var i = 0; i < 5; i++)
        {
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null) yield break;
            var candidate = Path.Combine(parent, "config", "themes", "global");
            if (Directory.Exists(candidate))
            {
                yield return candidate;
                yield break;
            }
            dir = parent;
        }
    }

    /// <summary>
    /// YAML wire shape — see config/themes/global/dracula.yaml for
    /// the canonical example. Map names use snake_case so the
    /// existing UnderscoredNamingConvention covers both this and
    /// EnvironmentProfileSeeder without a separate deserialiser.
    /// </summary>
    internal sealed class SeedRecord
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public int Version { get; set; } = 1;
        public Dictionary<string, string>? Palette { get; set; }
    }
}
