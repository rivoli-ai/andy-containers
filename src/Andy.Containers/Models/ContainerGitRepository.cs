namespace Andy.Containers.Models;

public class ContainerGitRepository
{
    public Guid Id { get; set; }
    public Guid ContainerId { get; set; }
    public Container? Container { get; set; }
    public required string Url { get; set; }
    public string? Branch { get; set; }
    public string TargetPath { get; set; } = "/workspace";
    public string? CredentialRef { get; set; }
    public int? CloneDepth { get; set; }
    public bool Submodules { get; set; }
    public bool IsFromTemplate { get; set; }
    public GitCloneStatus CloneStatus { get; set; } = GitCloneStatus.Pending;
    public string? CloneError { get; set; }
    public DateTime? CloneStartedAt { get; set; }
    public DateTime? CloneCompletedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum GitCloneStatus
{
    Pending,
    Cloning,
    Cloned,
    Failed,
    Pulling
}
