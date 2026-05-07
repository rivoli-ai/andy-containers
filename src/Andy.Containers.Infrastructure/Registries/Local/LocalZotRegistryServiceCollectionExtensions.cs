using Andy.Containers.Abstractions.Images;
using Andy.Containers.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Andy.Containers.Infrastructure.Registries.Local;

/// <summary>
/// DI wiring for the local-zot registry adapter. Call after
/// <c>services.AddImageManagement()</c> (IM2). Registers a typed
/// <see cref="HttpClient"/> bound to the registry URL configured in
/// <see cref="RegistryConfigurationOptions"/>, an
/// <see cref="IRegistryUploader"/> that shells out to the Docker
/// CLI, and the adapter itself as a singleton
/// <see cref="IRegistryAdapter"/>.
/// </summary>
public static class LocalZotRegistryServiceCollectionExtensions
{
    /// <summary>
    /// Default id for the local zot registry, matching the convention
    /// in <see cref="RegistryConfigurationOptions.PrimaryRegistryId"/>.
    /// </summary>
    public const string DefaultRegistryId = "local-zot";

    /// <summary>
    /// Register the <see cref="LocalZotAdapter"/> as a registered
    /// <see cref="IRegistryAdapter"/>. Looks up the registry's URL
    /// from the configured <see cref="RegistryConfigurationOptions.Registries"/>
    /// list at construction time, falling back to
    /// <c>http://localhost:5050</c> if no entry is configured (the
    /// embedded-mode default).
    /// </summary>
    public static IServiceCollection AddLocalZotRegistry(
        this IServiceCollection services,
        string registryId = DefaultRegistryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryId);

        services.TryAddSingleton<IRegistryUploader, DockerCliUploader>();

        services.AddHttpClient<LocalZotAdapter>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<RegistryConfigurationOptions>>().Value;
            var entry = options.Registries.FirstOrDefault(r => r.Id == registryId);
            var url = string.IsNullOrWhiteSpace(entry?.Url) ? "http://localhost:5050" : entry.Url;
            client.BaseAddress = new Uri(url);
        });

        services.AddSingleton<IRegistryAdapter>(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(LocalZotAdapter));
            // Reuse the typed client's base-address binding above —
            // AddHttpClient<T> registers the named client as both the
            // type-bound singleton and a named client of the same name.
            // Apply the configured BaseAddress here too so direct
            // resolution path stays consistent with the typed-client one.
            var options = sp.GetRequiredService<IOptions<RegistryConfigurationOptions>>().Value;
            var entry = options.Registries.FirstOrDefault(r => r.Id == registryId);
            var url = string.IsNullOrWhiteSpace(entry?.Url) ? "http://localhost:5050" : entry.Url;
            http.BaseAddress = new Uri(url);

            var uploader = sp.GetRequiredService<IRegistryUploader>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LocalZotAdapter>>();
            return new LocalZotAdapter(http, uploader, logger, registryId);
        });

        return services;
    }
}
