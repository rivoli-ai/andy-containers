using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Providers.Apple;
using Andy.Containers.Infrastructure.Providers.Local;
using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public class InfrastructureProviderFactory : IInfrastructureProviderFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public InfrastructureProviderFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public IInfrastructureProvider GetProvider(InfrastructureProvider providerEntity)
    {
        return providerEntity.Type switch
        {
            ProviderType.Docker => new DockerInfrastructureProvider(
                providerEntity.ConnectionConfig,
                _loggerFactory.CreateLogger<DockerInfrastructureProvider>()),
            ProviderType.AppleContainer => new AppleContainerProvider(
                providerEntity.ConnectionConfig,
                _loggerFactory.CreateLogger<AppleContainerProvider>()),
            _ => throw new NotSupportedException($"Provider type {providerEntity.Type} is not yet implemented")
        };
    }
}
