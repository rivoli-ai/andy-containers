namespace Andy.Containers.Models;

public class ImageToolManifest
{
    public required string ImageContentHash { get; set; }
    public required string BaseImage { get; set; }
    public required string BaseImageDigest { get; set; }
    public required string Architecture { get; set; }
    public required OsInfo OperatingSystem { get; set; }
    public IReadOnlyList<InstalledTool> Tools { get; set; } = [];
    public IReadOnlyList<InstalledPackage> OsPackages { get; set; } = [];
    public DateTime IntrospectedAt { get; set; } = DateTime.UtcNow;
}
