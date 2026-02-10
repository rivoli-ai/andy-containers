using Andy.Containers.Models;

namespace Andy.Containers.Abstractions;

public interface IWorkspaceService
{
    Task<Workspace> CreateWorkspaceAsync(CreateWorkspaceRequest request, CancellationToken ct = default);
    Task<Workspace> GetWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);
    Task<IReadOnlyList<Workspace>> ListWorkspacesAsync(WorkspaceFilter filter, CancellationToken ct = default);
    Task<Workspace> UpdateWorkspaceAsync(Guid workspaceId, UpdateWorkspaceRequest request, CancellationToken ct = default);
    Task DeleteWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);
    Task SuspendWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);
    Task ResumeWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);
}

public class CreateWorkspaceRequest
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? TeamId { get; set; }
    public string? GitRepositoryUrl { get; set; }
    public string? GitBranch { get; set; }
    public Guid? TemplateId { get; set; }
    public string? TemplateCode { get; set; }
}

public class UpdateWorkspaceRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? GitBranch { get; set; }
}

public class WorkspaceFilter
{
    public string? OwnerId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? TeamId { get; set; }
    public WorkspaceStatus? Status { get; set; }
    public int? Skip { get; set; }
    public int? Take { get; set; }
}
