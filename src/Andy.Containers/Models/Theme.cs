namespace Andy.Containers.Models;

/// <summary>
/// Predefined visual theme that can be attached to a container or
/// a template. Conductor #886.
///
/// Themes are catalog records: seeded from
/// <c>config/themes/global/*.yaml</c> at startup (mirroring the
/// X2 EnvironmentProfile pattern), exposed via
/// <c>GET /api/themes</c>, and referenced by
/// <see cref="Container.ThemeId"/> /
/// <see cref="ContainerTemplate.ThemeId"/>.
///
/// Catalog-vs-instance: this entity is read-only-by-default for
/// API consumers (no <c>POST /api/themes</c> in v1). Operators
/// add themes by adding YAML files; the seeder upserts them at
/// next host start. User-defined themes are deliberately out of
/// scope for v1 (story-level decision).
///
/// Resolution at session-attach time follows a fixed precedence:
/// container > template > Conductor user preference > hardcoded
/// default. Conductor's <c>ThemeResolver</c> implements the
/// chain; this entity is just storage.
/// </summary>
public class Theme
{
    /// <summary>
    /// Stable catalog id (e.g. "dracula", "github-dark"). Unique
    /// across the catalog. Seeded entries use a slug derived from
    /// the YAML filename so cross-deploy ids are predictable.
    /// </summary>
    public required string Id { get; set; }

    /// <summary>
    /// Machine-readable name — typically equal to <see cref="Id"/>
    /// but kept separate so a future rename can ship a stable id
    /// + a refreshed name without invalidating containers that
    /// already reference the old id.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Human-readable display name shown in the picker UI
    /// (e.g. "Dracula", "GitHub Dark").
    /// </summary>
    public required string DisplayName { get; set; }

    /// <summary>
    /// Surface this theme applies to: <c>terminal</c>, <c>ide</c>,
    /// or <c>vnc</c>. The picker filters by kind so users only
    /// see palette options that make sense for the surface
    /// they're configuring.
    /// </summary>
    public required string Kind { get; set; }

    /// <summary>
    /// JSON-encoded palette. Schema is documented in
    /// <c>docs/design/theme-persistence.md</c>; the consumer
    /// (Conductor) parses it. Stored as a string column rather
    /// than a relational JSON column so the catalog stays
    /// portable across Postgres + SQLite without schema-per-
    /// dialect handling.
    /// </summary>
    public required string PaletteJson { get; set; }

    /// <summary>
    /// Schema version for the palette JSON. Bumped when the
    /// canonical key set changes (e.g. adding 256-color
    /// extensions on top of the 16-color base). Lets clients
    /// reject palettes they don't understand without crashing.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// When the seeder first inserted this row. Stable across
    /// re-seeds — only the mutable fields (display name, palette,
    /// version) get refreshed on subsequent runs.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
