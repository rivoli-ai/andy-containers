namespace Andy.Containers.Abstractions.Images;

/// <summary>
/// The ordered set of registries this <c>andy-containers</c> instance is
/// configured to push to. In solo mode this is one entry (managed local
/// zot); in single-tenant cloud mode it's the customer-mandated registry;
/// in multi-tenant Rivoli Cloud it's the scale-out cluster plus optional
/// pull-through caches.
/// </summary>
public interface IRegistryConfiguration
{
    /// <summary>
    /// All configured registries, in the order they were declared. The
    /// first entry is conventionally the primary push target unless
    /// <see cref="PrimaryRegistryId"/> overrides.
    /// </summary>
    IReadOnlyList<RegistryConfigEntry> Registries { get; }

    /// <summary>
    /// Id of the primary push target. Resolution order:
    /// (1) explicit <c>PrimaryRegistryId</c> in options;
    /// (2) the entry with <c>IsPrimary=true</c>;
    /// (3) the first entry in <see cref="Registries"/>.
    /// Throws if no registries are configured.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if <see cref="Registries"/> is empty.
    /// </exception>
    string PrimaryRegistryId { get; }

    /// <summary>
    /// Look up a registry config by id. Throws if no entry matches.
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// Thrown if no registry is configured with this id.
    /// </exception>
    RegistryConfigEntry GetByIdOrThrow(string registryId);
}

/// <summary>
/// Configuration metadata for one registry. The actual I/O is owned by
/// an <see cref="IRegistryAdapter"/> registered separately in DI; the
/// link between a config entry and its adapter is by <c>Id</c>.
/// </summary>
/// <remarks>
/// Defined as a non-positional record with init-only properties so the
/// default <see cref="Microsoft.Extensions.Configuration.ConfigurationBinder"/>
/// can populate it from the <c>ImageManagement:Registries</c> section
/// without a custom binder. Construct in code using object-initializer
/// syntax: <c>new RegistryConfigEntry { Id = "...", Kind = "..." }</c>.
/// </remarks>
public sealed record RegistryConfigEntry
{
    /// <summary>
    /// Stable id used by the rest of the system to refer to this registry,
    /// matching the corresponding adapter's
    /// <see cref="IRegistryAdapter.RegistryId"/>.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Discriminator naming the registry technology — <c>zot</c>,
    /// <c>artifactory</c>, <c>acr</c>, <c>ecr</c>, <c>harbor</c>, <c>gar</c>.
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// Base URL where the registry serves its OCI Distribution API.
    /// </summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>
    /// True if this entry is the primary push target. At most one entry
    /// should be primary; if multiple are flagged the first wins.
    /// </summary>
    public bool IsPrimary { get; init; }

    /// <summary>
    /// Adapter-specific configuration (auth-mode hints, repo-path-prefix,
    /// scan-policy id, etc.). Kept open-ended so adding a new adapter
    /// doesn't require changing this record.
    /// </summary>
    public Dictionary<string, string> Properties { get; init; } = new();
}
