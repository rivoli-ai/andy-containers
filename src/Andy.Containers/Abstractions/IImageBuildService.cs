using Andy.Containers.Models;

namespace Andy.Containers.Abstractions;

/// <summary>
/// Manages container image building, versioning, and dependency tracking.
/// Images are content-addressed and automatically rebuilt when dependencies change.
/// </summary>
public interface IImageBuildService
{
    /// <summary>
    /// Build a new image for a template, resolving all dependencies to exact versions.
    /// </summary>
    Task<ContainerImage> BuildImageAsync(Guid templateId, ImageBuildOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Get the latest built image for a template.
    /// </summary>
    Task<ContainerImage?> GetLatestImageAsync(Guid templateId, CancellationToken ct = default);

    /// <summary>
    /// Get a specific image by its content hash.
    /// </summary>
    Task<ContainerImage?> GetImageByHashAsync(string contentHash, CancellationToken ct = default);

    /// <summary>
    /// List all images for a template, ordered by build number descending.
    /// </summary>
    Task<IReadOnlyList<ContainerImage>> ListImagesAsync(Guid templateId, CancellationToken ct = default);

    /// <summary>
    /// Get the resolved dependencies for an image build.
    /// </summary>
    Task<IReadOnlyList<ResolvedDependency>> GetResolvedDependenciesAsync(Guid imageId, CancellationToken ct = default);

    /// <summary>
    /// Compare two images and return a diff of what changed.
    /// </summary>
    Task<ImageDiff> DiffImagesAsync(Guid fromImageId, Guid toImageId, CancellationToken ct = default);

    /// <summary>
    /// Check all templates for available dependency updates and trigger rebuilds as needed.
    /// Called by the background update checker service.
    /// </summary>
    Task<IReadOnlyList<UpdateCheckResult>> CheckForUpdatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Resolve dependencies without building, returning what versions would be used.
    /// </summary>
    Task<IReadOnlyList<DependencyResolution>> ResolveDependenciesAsync(Guid templateId, CancellationToken ct = default);
}

public class ImageBuildOptions
{
    /// <summary>
    /// Build in air-gapped mode: all dependencies must come from the offline cache.
    /// Fails if any dependency is not cached.
    /// </summary>
    public bool Offline { get; set; }

    /// <summary>
    /// Force rebuild even if no dependency changes detected.
    /// </summary>
    public bool Force { get; set; }

    /// <summary>
    /// Specific provider to build on (for provider-specific images).
    /// </summary>
    public Guid? ProviderId { get; set; }
}

public class ImageDiff
{
    public Guid FromImageId { get; set; }
    public Guid ToImageId { get; set; }
    public IReadOnlyList<DependencyChange> DependencyChanges { get; set; } = [];
    public string? BaseImageChanged { get; set; }
}

public class DependencyChange
{
    public required string DependencyName { get; set; }
    public DependencyType DependencyType { get; set; }
    public string? PreviousVersion { get; set; }
    public string? NewVersion { get; set; }
    public DependencyChangeType ChangeType { get; set; }
}

public enum DependencyChangeType
{
    Added,
    Removed,
    VersionChanged,
    Unchanged
}

public class UpdateCheckResult
{
    public Guid TemplateId { get; set; }
    public required string TemplateCode { get; set; }
    public IReadOnlyList<AvailableUpdate> AvailableUpdates { get; set; } = [];
    public bool RebuildTriggered { get; set; }
}

public class AvailableUpdate
{
    public required string DependencyName { get; set; }
    public required string CurrentVersion { get; set; }
    public required string AvailableVersion { get; set; }
    public UpdatePolicy PolicyMatched { get; set; }
}

public class DependencyResolution
{
    public required string DependencyName { get; set; }
    public DependencyType DependencyType { get; set; }
    public required string VersionConstraint { get; set; }
    public required string ResolvedVersion { get; set; }
    public string? Source { get; set; }
    public bool AvailableOffline { get; set; }
}
