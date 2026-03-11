namespace Andy.Containers.Models;

public class ImageDiffResult
{
    public Guid FromImageId { get; set; }
    public Guid ToImageId { get; set; }
    public bool BaseImageChanged { get; set; }
    public bool ArchitectureChanged { get; set; }
    public string? OsVersionChanged { get; set; }
    public IReadOnlyList<ToolChange> ToolChanges { get; set; } = [];
    public PackageChangeSummary PackageChanges { get; set; } = new();
    public string? SizeChange { get; set; }
}

public class ToolChange
{
    public required string Name { get; set; }
    public DependencyType Type { get; set; }
    public required string ChangeType { get; set; }
    public string? PreviousVersion { get; set; }
    public string? NewVersion { get; set; }
    public string? Severity { get; set; }
}

public class PackageChangeSummary
{
    public int Added { get; set; }
    public int Removed { get; set; }
    public int Upgraded { get; set; }
    public int Downgraded { get; set; }
}
