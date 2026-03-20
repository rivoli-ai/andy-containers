using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

/// <summary>
/// Manages image manifests: generation via introspection, retrieval, and refresh.
/// </summary>
public interface IImageManifestService
{
    /// <summary>
    /// Generates a manifest for an image by running introspection, computing content hash,
    /// creating ResolvedDependency records, and storing the manifest JSON.
    /// Returns the existing image if a duplicate content hash is found.
    /// </summary>
    Task<(ImageToolManifest Manifest, ContainerImage Image)> GenerateManifestAsync(
        Guid imageId,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves the stored manifest for an image. Returns null if not yet introspected.
    /// </summary>
    Task<ImageToolManifest?> GetManifestAsync(
        Guid imageId,
        CancellationToken ct = default);

    /// <summary>
    /// Re-runs introspection and updates the manifest, content hash, and resolved dependencies.
    /// </summary>
    Task<(ImageToolManifest Manifest, ContainerImage Image)> RefreshManifestAsync(
        Guid imageId,
        CancellationToken ct = default);
}
