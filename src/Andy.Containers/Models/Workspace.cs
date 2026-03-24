namespace Andy.Containers.Models;

public class Workspace
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string OwnerId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? TeamId { get; set; }
    public WorkspaceStatus Status { get; set; } = WorkspaceStatus.Active;
    public Guid? DefaultContainerId { get; set; }
    public Container? DefaultContainer { get; set; }
    public string? GitRepositoryUrl { get; set; }
    public string? GitBranch { get; set; }
    public string? GitRepositories { get; set; }
    public string? Configuration { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public DateTime? LastAccessedAt { get; set; }
    public string? Metadata { get; set; }

    public ICollection<Container> Containers { get; set; } = new List<Container>();
}

public enum WorkspaceStatus
{
    Active,
    Suspended,
    Archived
}
