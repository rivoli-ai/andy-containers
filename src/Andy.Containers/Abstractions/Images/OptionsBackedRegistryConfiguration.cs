using Andy.Containers.Configuration;
using Microsoft.Extensions.Options;

namespace Andy.Containers.Abstractions.Images;

/// <summary>
/// Default <see cref="IRegistryConfiguration"/> implementation that
/// reads from <see cref="RegistryConfigurationOptions"/>. Singleton —
/// safe to capture <see cref="IOptions{TOptions}"/> because changes to
/// the registry list at runtime are not supported in IM2 (later stories
/// can swap in <see cref="IOptionsMonitor{TOptions}"/> if needed).
/// </summary>
internal sealed class OptionsBackedRegistryConfiguration : IRegistryConfiguration
{
    private readonly IOptions<RegistryConfigurationOptions> _options;

    public OptionsBackedRegistryConfiguration(IOptions<RegistryConfigurationOptions> options)
    {
        _options = options;
    }

    public IReadOnlyList<RegistryConfigEntry> Registries => _options.Value.Registries;

    public string PrimaryRegistryId
    {
        get
        {
            var explicitPrimary = _options.Value.PrimaryRegistryId;
            if (!string.IsNullOrWhiteSpace(explicitPrimary))
            {
                // Validate the explicit id resolves to a real entry; otherwise
                // a typo in config would silently shadow the IsPrimary fallback.
                if (_options.Value.Registries.All(r => r.Id != explicitPrimary))
                {
                    throw new InvalidOperationException(
                        $"PrimaryRegistryId '{explicitPrimary}' does not match any configured registry id.");
                }
                return explicitPrimary;
            }

            var primaryFlagged = _options.Value.Registries.FirstOrDefault(r => r.IsPrimary);
            if (primaryFlagged is not null)
            {
                return primaryFlagged.Id;
            }

            var first = _options.Value.Registries.FirstOrDefault();
            if (first is not null)
            {
                return first.Id;
            }

            throw new InvalidOperationException(
                "No registries configured. Populate RegistryConfigurationOptions.Registries via " +
                "the 'ImageManagement:Registries' configuration section, or register a registry " +
                "adapter through one of the per-vendor extension methods (AddZotRegistry, etc.).");
        }
    }

    public RegistryConfigEntry GetByIdOrThrow(string registryId)
    {
        var entry = _options.Value.Registries.FirstOrDefault(r => r.Id == registryId);
        if (entry is null)
        {
            throw new KeyNotFoundException(
                $"No registry configured with id '{registryId}'. " +
                $"Configured ids: [{string.Join(", ", _options.Value.Registries.Select(r => $"'{r.Id}'"))}].");
        }
        return entry;
    }
}
