using Andy.Containers.Abstractions.Images;

namespace Andy.Containers.Configuration;

/// <summary>
/// Options binding for the set of image registries this
/// <c>andy-containers</c> instance is configured to push to. Bound from
/// the <c>ImageManagement:Registries</c> configuration section by
/// <see cref="DependencyInjection.ServiceCollectionExtensions.AddImageManagement"/>.
/// </summary>
/// <remarks>
/// IM2 introduces this options class with no default registry entries.
/// IM6 (the local zot adapter) wires a default <c>local-zot</c> entry
/// when no registries are configured at all, so embedded mode works out
/// of the box.
/// </remarks>
public sealed class RegistryConfigurationOptions
{
    public const string SectionName = "ImageManagement";

    /// <summary>
    /// All configured registries, in declaration order.
    /// </summary>
    public List<RegistryConfigEntry> Registries { get; set; } = [];

    /// <summary>
    /// Optional explicit primary id. When unset, primary resolution
    /// falls back to the entry with <see cref="RegistryConfigEntry.IsPrimary"/>
    /// true, then to the first entry in <see cref="Registries"/>.
    /// </summary>
    public string? PrimaryRegistryId { get; set; }
}
