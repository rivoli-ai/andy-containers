using Andy.Containers.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Andy.Containers.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddContainers(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ContainersOptions>(
            configuration.GetSection(ContainersOptions.SectionName));

        return services;
    }
}
