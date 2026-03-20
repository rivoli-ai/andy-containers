namespace Andy.Containers.Models;

public class Container
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public Guid TemplateId { get; set; }
    public ContainerTemplate? Template { get; set; }
    public Guid ProviderId { get; set; }
    public InfrastructureProvider? Provider { get; set; }
    public string? ExternalId { get; set; }
    public ContainerStatus Status { get; set; } = ContainerStatus.Pending;
    public required string OwnerId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? TeamId { get; set; }
    public string? AllocatedResources { get; set; }
    public string? NetworkConfig { get; set; }
    public string? IdeEndpoint { get; set; }
    public string? VncEndpoint { get; set; }
    public string? GitRepository { get; set; }
    public string? EnvironmentVariables { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? StoppedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastActivityAt { get; set; }
    public string? Metadata { get; set; }

    // === Story 3: SSH Access ===
    public bool SshEnabled { get; set; }
    public string? SshEndpoint { get; set; }
    public string? SshUser { get; set; }

    public ICollection<ContainerSession> Sessions { get; set; } = new List<ContainerSession>();
    public ICollection<ContainerEvent> Events { get; set; } = new List<ContainerEvent>();
}

public enum ContainerStatus
{
    Pending,
    Creating,
    Running,
    Stopping,
    Stopped,
    Failed,
    Destroying,
    Destroyed
}
