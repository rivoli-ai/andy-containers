using Andy.Containers.Models;

namespace Andy.Containers.Abstractions;

/// <summary>
/// Manages the hierarchical container template catalog.
/// Templates are scoped: Global > Organization > Team > User.
/// </summary>
public interface ITemplateCatalogService
{
    Task<ContainerTemplate> CreateTemplateAsync(CreateTemplateRequest request, CancellationToken ct = default);
    Task<ContainerTemplate> GetTemplateAsync(Guid templateId, CancellationToken ct = default);
    Task<ContainerTemplate?> GetTemplateByCodeAsync(string code, CancellationToken ct = default);

    /// <summary>
    /// Browse the catalog with visibility scoping.
    /// Returns templates visible to the caller based on their org/team/user context.
    /// </summary>
    Task<IReadOnlyList<ContainerTemplate>> BrowseCatalogAsync(CatalogBrowseRequest request, CancellationToken ct = default);

    Task<ContainerTemplate> UpdateTemplateAsync(Guid templateId, UpdateTemplateRequest request, CancellationToken ct = default);
    Task PublishTemplateAsync(Guid templateId, CancellationToken ct = default);
    Task UnpublishTemplateAsync(Guid templateId, CancellationToken ct = default);
    Task DeleteTemplateAsync(Guid templateId, CancellationToken ct = default);
}

public class CreateTemplateRequest
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string Version { get; set; }
    public required string BaseImage { get; set; }
    public CatalogScope CatalogScope { get; set; } = CatalogScope.User;
    public Guid? OrganizationId { get; set; }
    public Guid? TeamId { get; set; }
    public IdeType IdeType { get; set; } = IdeType.CodeServer;
    public bool GpuRequired { get; set; }
    public bool GpuPreferred { get; set; }
    public Guid? ParentTemplateId { get; set; }
    public string[]? Tags { get; set; }
    public List<DependencySpecRequest>? Dependencies { get; set; }
}

public class DependencySpecRequest
{
    public DependencyType Type { get; set; }
    public required string Name { get; set; }
    public string? Ecosystem { get; set; }
    public required string VersionConstraint { get; set; }
    public bool AutoUpdate { get; set; } = true;
    public UpdatePolicy UpdatePolicy { get; set; } = UpdatePolicy.Minor;
}

public class UpdateTemplateRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Version { get; set; }
    public IdeType? IdeType { get; set; }
    public string[]? Tags { get; set; }
}

public class CatalogBrowseRequest
{
    public CatalogScope? Scope { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? TeamId { get; set; }
    public string? OwnerId { get; set; }
    public string? SearchTerm { get; set; }
    public string[]? Tags { get; set; }
    public bool? GpuRequired { get; set; }
    public IdeType? IdeType { get; set; }
    public bool IncludeUnpublished { get; set; }
    public int? Skip { get; set; }
    public int? Take { get; set; }
}
