namespace Andy.Containers.Models;

public class ContainerEvent
{
    public Guid Id { get; set; }
    public Guid ContainerId { get; set; }
    public Container? Container { get; set; }
    public ContainerEventType EventType { get; set; }
    public string? SubjectId { get; set; }
    public string? Details { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public enum ContainerEventType
{
    Created,
    Started,
    Stopped,
    Restarted,
    Resized,
    Failed,
    Destroyed,
    SessionOpened,
    SessionClosed,
    AgentSpawned,
    AgentCompleted,
    GitCloneStarted,
    GitCloned,
    GitCloneFailed,
    GitPulled,
    ExpiredAutoStopped,
    ExpiredAutoDestroyed
}
