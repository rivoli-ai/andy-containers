using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Providers.Apple;
using Andy.Containers.Infrastructure.Providers.Aws;
using Andy.Containers.Infrastructure.Providers.Azure;
using Andy.Containers.Infrastructure.Providers.Gcp;
using Andy.Containers.Infrastructure.Providers.Local;
using Andy.Containers.Infrastructure.Providers.ThirdParty;
using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public class InfrastructureProviderFactory : IInfrastructureProviderFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public InfrastructureProviderFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public IInfrastructureProvider GetProvider(ProviderType type)
    {
        var entity = new InfrastructureProvider
        {
            Code = "default",
            Name = $"Default {type}",
            Type = type,
            IsEnabled = true
        };
        return GetProvider(entity);
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
            ProviderType.AzureAci => new AzureAciProvider(
                providerEntity.ConnectionConfig,
                _loggerFactory.CreateLogger<AzureAciProvider>()),
            ProviderType.GcpCloudRun => new GcpCloudRunProvider(
                providerEntity.ConnectionConfig,
                _loggerFactory.CreateLogger<GcpCloudRunProvider>()),
            ProviderType.AwsFargate => new AwsFargateProvider(
                providerEntity.ConnectionConfig,
                _loggerFactory.CreateLogger<AwsFargateProvider>()),
            ProviderType.FlyIo => new FlyIoProvider(
                providerEntity.ConnectionConfig,
                _loggerFactory.CreateLogger<FlyIoProvider>()),
            ProviderType.Hetzner => new HetznerCloudProvider(
                providerEntity.ConnectionConfig,
                _loggerFactory.CreateLogger<HetznerCloudProvider>()),
            ProviderType.DigitalOcean => new DigitalOceanProvider(
                providerEntity.ConnectionConfig,
                _loggerFactory.CreateLogger<DigitalOceanProvider>()),
            ProviderType.Civo => new CivoProvider(
                providerEntity.ConnectionConfig,
                _loggerFactory.CreateLogger<CivoProvider>()),
            _ => throw new NotSupportedException($"Provider type {providerEntity.Type} is not yet implemented")
        };
    }
}
