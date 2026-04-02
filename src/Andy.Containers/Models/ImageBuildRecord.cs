namespace Andy.Containers.Models;

public class ImageBuildRecord
{
    public Guid Id { get; set; }
    public required string ImageReference { get; set; }
    public string? TemplateCode { get; set; }
    public TemplateBuildStatus Status { get; set; } = TemplateBuildStatus.Unknown;
    public DateTime? LastBuiltAt { get; set; }
    public string? DockerfileChecksum { get; set; }
    public string? LastBuildError { get; set; }
    public string? BuildLog { get; set; }
    public long? ImageSizeBytes { get; set; }
    public string? Architecture { get; set; }
    public string? Os { get; set; }
    public int? LayerCount { get; set; }
    public string? ImageDigest { get; set; }
    public DateTime? ImageCreatedAt { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}

public enum TemplateBuildStatus
{
    Unknown,
    NotBuilt,
    Building,
    Built,
    Outdated,
    Failed
}
