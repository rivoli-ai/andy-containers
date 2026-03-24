using Andy.Containers.Models;

namespace Andy.Containers.Abstractions;

/// <summary>
/// High-level container orchestration service.
/// Handles the full lifecycle from template selection through provisioning to cleanup.
/// </summary>
public interface IContainerService
{
    Task<Container> CreateContainerAsync(CreateContainerRequest request, CancellationToken ct = default);
    Task<Container> GetContainerAsync(Guid containerId, CancellationToken ct = default);
    Task<IReadOnlyList<Container>> ListContainersAsync(ContainerFilter filter, CancellationToken ct = default);
    Task StartContainerAsync(Guid containerId, CancellationToken ct = default);
    Task StopContainerAsync(Guid containerId, CancellationToken ct = default);
    Task DestroyContainerAsync(Guid containerId, CancellationToken ct = default);
    Task<ExecResult> ExecAsync(Guid containerId, string command, CancellationToken ct = default);
    Task<ConnectionInfo> GetConnectionInfoAsync(Guid containerId, CancellationToken ct = default);
}

public class CreateContainerRequest
{
    public required string Name { get; set; }
    public Guid? TemplateId { get; set; }
    public string? TemplateCode { get; set; }
    public Guid? ProviderId { get; set; }
    public string? ProviderCode { get; set; }
    public Guid? WorkspaceId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? TeamId { get; set; }
    public string? OwnerId { get; set; }
    public ResourceSpec? Resources { get; set; }
    public GpuSpec? Gpu { get; set; }
    public GitRepositoryConfig? GitRepository { get; set; }
    public List<GitRepositoryConfig>? GitRepositories { get; set; }
    public bool ExcludeTemplateRepos { get; set; }
    public bool SkipUrlValidation { get; set; }
    public Dictionary<string, string>? EnvironmentVariables { get; set; }
    public Models.CodeAssistantConfig? CodeAssistant { get; set; }
    public bool ExcludeTemplateCodeAssistant { get; set; }
    public TimeSpan? ExpiresAfter { get; set; }
    public CreationSource Source { get; set; } = CreationSource.Unknown;
    public string? ClientInfo { get; set; }
}

public class GitRepositoryConfig
{
    public required string Url { get; set; }
    public string? Branch { get; set; }
    public string? CredentialRef { get; set; }
    public string? TargetPath { get; set; }
    public int? CloneDepth { get; set; }
    public bool Submodules { get; set; }
}

public class ContainerFilter
{
    public string? OwnerId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? TeamId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public ContainerStatus? Status { get; set; }
    public Guid? TemplateId { get; set; }
    public Guid? ProviderId { get; set; }
    public CreationSource? Source { get; set; }
    public int? Skip { get; set; }
    public int? Take { get; set; }
}
