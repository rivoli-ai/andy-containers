namespace Andy.Containers.Models;

public class TemplateEvent
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public TemplateEventType EventType { get; set; }
    public string? SubjectId { get; set; }
    public string? BeforeSnapshot { get; set; }
    public string? AfterSnapshot { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public enum TemplateEventType
{
    Created,
    Updated,
    DefinitionUpdated,
    Published,
    Deleted,
    DependenciesUpdated
}
