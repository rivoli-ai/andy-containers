namespace Andy.Containers.Models;

public class InfrastructureProvider
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public ProviderType Type { get; set; }
    public string? ConnectionConfig { get; set; }
    public string? Capabilities { get; set; }
    public string? Region { get; set; }
    public Guid? OrganizationId { get; set; }
    public bool IsEnabled { get; set; } = true;
    public ProviderHealth HealthStatus { get; set; } = ProviderHealth.Unknown;
    public DateTime? LastHealthCheck { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? Metadata { get; set; }
}

public enum ProviderType
{
    Docker,
    AppleContainer,
    Rivoli,
    Ssh,
    AzureAci,
    AzureAca,
    AzureAcp
}

public enum ProviderHealth
{
    Unknown,
    Healthy,
    Degraded,
    Unreachable
}
