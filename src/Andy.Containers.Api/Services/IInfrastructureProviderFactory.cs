using Andy.Containers.Abstractions;
using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public interface IInfrastructureProviderFactory
{
    IInfrastructureProvider GetProvider(InfrastructureProvider providerEntity);

    /// <summary>
    /// Gets a default provider instance for the given type (uses default/empty connection config).
    /// Useful for ephemeral operations like image introspection.
    /// </summary>
    IInfrastructureProvider GetProvider(ProviderType type);
}
