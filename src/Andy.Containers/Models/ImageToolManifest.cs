namespace Andy.Containers.Models;

/// <summary>
/// Complete bill of materials for a built container image, produced by introspection.
/// Serialized to JSON and stored in ContainerImage.DependencyManifest.
/// </summary>
public class ImageToolManifest
{
    /// <summary>Content-addressed hash (sha256:{hex}) computed from tool versions + base image.</summary>
    public required string ImageContentHash { get; set; }

    /// <summary>Base image reference (e.g., "ubuntu:24.04").</summary>
    public required string BaseImage { get; set; }

    /// <summary>Digest of the base OS image.</summary>
    public required string BaseImageDigest { get; set; }

    /// <summary>CPU architecture (e.g., "amd64", "arm64").</summary>
    public required string Architecture { get; set; }

    /// <summary>Operating system details.</summary>
    public required OsInfo OperatingSystem { get; set; }

    /// <summary>Detected tools, SDKs, and runtimes.</summary>
    public IReadOnlyList<InstalledTool> Tools { get; set; } = [];

    /// <summary>Detected OS-level packages.</summary>
    public IReadOnlyList<InstalledPackage> OsPackages { get; set; } = [];

    /// <summary>When introspection was performed.</summary>
    public DateTime IntrospectedAt { get; set; } = DateTime.UtcNow;
}
