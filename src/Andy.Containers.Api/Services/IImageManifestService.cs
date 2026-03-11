using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public interface IImageManifestService
{
    Task<ImageToolManifest?> GetManifestAsync(Guid imageId, CancellationToken ct = default);
    Task<ImageToolManifest> StoreManifestAsync(Guid imageId, ImageToolManifest manifest, CancellationToken ct = default);
    Task<ImageDiffResult> DiffImagesAsync(Guid fromImageId, Guid toImageId, CancellationToken ct = default);
    Task<IReadOnlyList<ContainerImage>> FindImagesByToolAsync(string toolName, string? minVersion = null, CancellationToken ct = default);
}
