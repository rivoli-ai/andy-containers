using Andy.Containers.Abstractions.Images;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Andy.Containers.Infrastructure.Build.Local;

/// <summary>
/// DI wiring for the local build backend. Call after
/// <c>services.AddImageManagement()</c> (IM2). Registers the
/// engine detector and the build backend as
/// <see cref="IBuildBackend"/>. The detector caches its result on
/// first call so production code can resolve it lazily.
/// </summary>
public static class LocalBuildBackendServiceCollectionExtensions
{
    /// <summary>
    /// Register the <see cref="LocalBuildBackend"/> as the
    /// <see cref="IBuildBackend"/> implementation. Idempotent —
    /// callers can invoke it from multiple composition roots without
    /// duplicate registration.
    /// </summary>
    public static IServiceCollection AddLocalBuildBackend(this IServiceCollection services)
    {
        services.TryAddSingleton<IBuildEngineDetector, BuildEngineDetector>();
        services.TryAddSingleton<IBuildBackend, LocalBuildBackend>();
        return services;
    }
}
