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
    public string? HostIp { get; set; }
    public string? GitRepository { get; set; }
    public string? EnvironmentVariables { get; set; }
    public string? CodeAssistant { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? StoppedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastActivityAt { get; set; }
    public CreationSource CreationSource { get; set; } = CreationSource.Unknown;
    public string? ClientInfo { get; set; }
    public string? ContainerUser { get; set; }
    public string? Metadata { get; set; }

    // Optional correlation to a backlog story. Stamped by the caller
    // (typically andy-issues' SandboxService) at create time. Propagated
    // into run.* event payloads so andy-issues' Story 15.6 consumer can
    // transition the linked UserStory's state on run completion.
    public Guid? StoryId { get; set; }

    /// <summary>
    /// Human-friendly identifier generated at create time
    /// (<c>{adjective}-{animal}</c>, e.g. "amber-pelican"). Stable
    /// across the container's lifetime; never collides with the
    /// short ExternalId (12-char hash) but is much easier to refer
    /// to in conversation / docs / chat. Conductor #871.
    /// </summary>
    public string? FriendlyName { get; set; }

    /// <summary>
    /// OS label populated post-creation by reading
    /// <c>/etc/os-release</c> inside the container. Format follows
    /// <c>{NAME} {VERSION_ID}</c> (e.g. "Debian 12", "Alpine 3.19").
    /// Best-effort: probe failures leave this null without blocking
    /// provisioning. Conductor #871.
    /// </summary>
    public string? OsLabel { get; set; }

    public ICollection<ContainerSession> Sessions { get; set; } = new List<ContainerSession>();
    public ICollection<ContainerEvent> Events { get; set; } = new List<ContainerEvent>();
    public ICollection<ContainerGitRepository> GitRepositories { get; set; } = new List<ContainerGitRepository>();
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

public enum CreationSource
{
    Unknown,
    WebUi,
    RestApi,
    Mcp,
    Grpc,
    Cli,
}
