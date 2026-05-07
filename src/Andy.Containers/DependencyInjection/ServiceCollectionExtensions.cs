using Andy.Containers.Abstractions.Images;
using Andy.Containers.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

    /// <summary>
    /// Registers the image management abstraction layer (IM2). Wires
    /// the default <see cref="IRegistryConfiguration"/> backed by
    /// <see cref="RegistryConfigurationOptions"/>. Concrete adapters
    /// and build backends are registered separately by per-vendor
    /// extensions (<c>AddZotRegistry</c>, <c>AddLocalBuildBackend</c>,
    /// etc.) introduced in IM6+.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    /// Optional configuration root. When supplied, the
    /// <c>ImageManagement</c> section is bound to
    /// <see cref="RegistryConfigurationOptions"/>. When null, callers are
    /// expected to invoke <see cref="OptionsServiceCollectionExtensions.Configure{TOptions}(IServiceCollection, Action{TOptions})"/>
    /// themselves (typical in unit tests).
    /// </param>
    public static IServiceCollection AddImageManagement(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        if (configuration is not null)
        {
            services.Configure<RegistryConfigurationOptions>(
                configuration.GetSection(RegistryConfigurationOptions.SectionName));
        }

        services.TryAddSingleton<IRegistryConfiguration, OptionsBackedRegistryConfiguration>();

        return services;
    }
}
