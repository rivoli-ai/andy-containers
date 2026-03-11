namespace Andy.Containers.Models;

public class ContainerGitRepository
{
    public Guid Id { get; set; }
    public Guid ContainerId { get; set; }
    public Container? Container { get; set; }
    public required string Url { get; set; }
    public string Branch { get; set; } = "main";
    public string? TargetPath { get; set; }
    public string? CredentialRef { get; set; }
    public int CloneDepth { get; set; }
    public bool Submodules { get; set; }
    public CloneStatus Status { get; set; } = CloneStatus.Pending;
    public string? ErrorMessage { get; set; }
    public int SortOrder { get; set; }
    public DateTime? ClonedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum CloneStatus
{
    Pending,
    Cloning,
    Succeeded,
    Failed
}
