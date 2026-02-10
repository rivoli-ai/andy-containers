namespace Andy.Containers.Models;

public class ContainerSession
{
    public Guid Id { get; set; }
    public Guid ContainerId { get; set; }
    public Container? Container { get; set; }
    public required string SubjectId { get; set; }
    public SessionType SessionType { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Active;
    public string? EndpointUrl { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public string? AgentId { get; set; }
    public string? Metadata { get; set; }
}

public enum SessionType
{
    Ide,
    Vnc,
    Ssh,
    Agent,
    Api
}

public enum SessionStatus
{
    Active,
    Disconnected,
    Expired
}
