using Andy.Containers.Abstractions;
using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public interface IInfrastructureProviderFactory
{
    IInfrastructureProvider GetProvider(InfrastructureProvider providerEntity);
}
