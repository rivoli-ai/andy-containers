namespace Andy.Containers.Models;

/// <summary>
/// A built container image with a unique content-addressed identifier.
/// Tracks exact versions of all tools, compilers, and libraries installed.
/// Automatically rebuilt when upstream dependencies change.
/// </summary>
public class ContainerImage
{
    public Guid Id { get; set; }

    /// <summary>
    /// Content-addressed unique identifier: hash of the full dependency manifest.
    /// Format: "sha256:{hash}" computed from sorted dependency versions + base image digest.
    /// </summary>
    public required string ContentHash { get; set; }

    /// <summary>
    /// Human-readable tag: "{template-code}:{version}-{build-number}"
    /// e.g., "full-stack:1.2.0-42"
    /// </summary>
    public required string Tag { get; set; }

    public Guid TemplateId { get; set; }
    public ContainerTemplate? Template { get; set; }

    /// <summary>
    /// Full OCI image reference (registry/repo:tag@digest).
    /// </summary>
    public required string ImageReference { get; set; }

    /// <summary>
    /// Digest of the base OS image used (e.g., Ubuntu 24.04 specific digest).
    /// </summary>
    public required string BaseImageDigest { get; set; }

    /// <summary>
    /// Complete, reproducible dependency manifest (JSON).
    /// Lists every tool, compiler, library, and their exact versions.
    /// </summary>
    public required string DependencyManifest { get; set; }

    /// <summary>
    /// Resolved dependency lock file (JSON).
    /// For each declared dependency, the exact resolved version, source, and hash.
    /// Analogous to package-lock.json / packages.lock.json.
    /// </summary>
    public required string DependencyLock { get; set; }

    public int BuildNumber { get; set; }
    public ImageBuildStatus BuildStatus { get; set; } = ImageBuildStatus.Pending;
    public string? BuildLog { get; set; }
    public DateTime? BuildStartedAt { get; set; }
    public DateTime? BuildCompletedAt { get; set; }
    public long? ImageSizeBytes { get; set; }

    /// <summary>
    /// Whether this image was built in air-gapped/offline mode
    /// with all dependencies pre-fetched.
    /// </summary>
    public bool BuiltOffline { get; set; }

    /// <summary>
    /// Previous image in the chain (for diffing what changed).
    /// </summary>
    public Guid? PreviousImageId { get; set; }
    public ContainerImage? PreviousImage { get; set; }

    /// <summary>
    /// Human-readable changelog: what changed from the previous image.
    /// </summary>
    public string? Changelog { get; set; }

    // === Story 2: Org RBAC ===
    public Guid? OrganizationId { get; set; }
    public string? OwnerId { get; set; }
    public ImageVisibility Visibility { get; set; } = ImageVisibility.Global;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? Metadata { get; set; }
}

public enum ImageBuildStatus
{
    Pending,
    Building,
    Succeeded,
    Failed
}
