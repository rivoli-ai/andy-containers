namespace Andy.Containers.Models;

/// <summary>
/// X3 (rivoli-ai/andy-containers#92). Wire-shape for the
/// <c>GET /api/environments</c> catalog. Mirrors
/// <see cref="EnvironmentProfile"/> with the structured
/// <see cref="EnvironmentCapabilities"/> envelope flattened into the
/// payload — clients see a typed capability block, not the raw JSON
/// column. Kept distinct from the entity so the wire shape can grow
/// (links, computed fields) without re-shaping the EF model.
/// </summary>
public sealed class EnvironmentProfileDto
{
    public Guid Id { get; init; }

    /// <summary>Slug — stable across renames (e.g. <c>headless-container</c>).</summary>
    public string Code { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Serialised <see cref="EnvironmentKind"/> for API/CLI/MCP consumers.</summary>
    public string Kind { get; init; } = string.Empty;

    public string BaseImageRef { get; init; } = string.Empty;

    public EnvironmentCapabilities Capabilities { get; init; } = new();

    public DateTimeOffset CreatedAt { get; init; }

    public static EnvironmentProfileDto FromEntity(EnvironmentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new EnvironmentProfileDto
        {
            Id = profile.Id,
            Code = profile.Name,
            DisplayName = profile.DisplayName,
            Kind = profile.Kind.ToString(),
            BaseImageRef = profile.BaseImageRef,
            // Capabilities is owned-by-EF and shape-stable; safe to surface
            // by reference. EF tracks no further mutation once the entity
            // is fetched read-only via AsNoTracking.
            Capabilities = profile.Capabilities,
            CreatedAt = profile.CreatedAt,
        };
    }
}
