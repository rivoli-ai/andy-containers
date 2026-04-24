using Microsoft.Extensions.Hosting;

namespace Andy.Containers.Api.Services;

/// <summary>
/// Named accessors for the three deployment modes (Development /
/// Docker / Embedded). See andy-service-template/docs/ports.md.
/// Duplicated across every andy-* service (no shared library yet);
/// keep the <c>EmbeddedEnvironmentName</c> constant in sync with
/// <c>ServiceEnvironment.embeddedEnvironmentName</c> on the Swift side.
/// </summary>
public static class HostEnvironmentExtensions
{
    public const string EmbeddedEnvironmentName = "Embedded";
    public const string DockerEnvironmentName = "Docker";

    public static bool IsEmbedded(this IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.IsEnvironment(EmbeddedEnvironmentName);
    }

    public static bool IsDocker(this IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.IsEnvironment(DockerEnvironmentName);
    }

    public static bool IsLocalOrEmbedded(this IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return !environment.IsProduction();
    }
}
