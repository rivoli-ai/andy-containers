using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

/// <summary>
/// Introspects containers or images to discover installed tool versions.
/// </summary>
public interface IToolVersionDetector
{
    /// <summary>
    /// Introspects a running container to discover installed tools.
    /// </summary>
    Task<ImageToolManifest> IntrospectContainerAsync(
        Guid containerId,
        IReadOnlyList<DependencySpec>? declaredDependencies = null,
        CancellationToken ct = default);

    /// <summary>
    /// Introspects an image by creating a temporary container, running detection,
    /// and destroying it. The temp container runs with --network none.
    /// </summary>
    Task<ImageToolManifest> IntrospectImageAsync(
        string imageReference,
        IReadOnlyList<DependencySpec>? declaredDependencies = null,
        CancellationToken ct = default);
}
