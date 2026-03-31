namespace Andy.Containers.Models;

public class ContainerTemplate
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string Version { get; set; }
    public required string BaseImage { get; set; }
    public CatalogScope CatalogScope { get; set; } = CatalogScope.Global;
    public Guid? OrganizationId { get; set; }
    public Guid? TeamId { get; set; }
    public string? OwnerId { get; set; }
    public string? Toolchains { get; set; }
    public IdeType IdeType { get; set; } = IdeType.CodeServer;
    public string GuiType { get; set; } = "none"; // "none" or "vnc"
    public string? DefaultResources { get; set; }
    public bool GpuRequired { get; set; }
    public bool GpuPreferred { get; set; }
    public string? EnvironmentVariables { get; set; }
    public string? Ports { get; set; }
    public string? Scripts { get; set; }
    public string[]? Tags { get; set; }
    public bool IsPublished { get; set; }
    public Guid? ParentTemplateId { get; set; }
    public ContainerTemplate? ParentTemplate { get; set; }
    public string? GitRepositories { get; set; }
    public string? CodeAssistant { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public string? Metadata { get; set; }
}

public enum CatalogScope
{
    Global,
    Organization,
    Team,
    User
}

public enum IdeType
{
    None,
    CodeServer,
    Zed,
    Both
}
