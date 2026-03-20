namespace Andy.Containers.Api.Services;

public interface IContainerAuthorizationService
{
    Task<bool> CanAccessContainerAsync(string userId, Guid containerId, string action, CancellationToken ct = default);
    Task<bool> CanAccessTemplateAsync(string userId, Guid templateId, string action, CancellationToken ct = default);
    Task<bool> CanAccessImageAsync(string userId, Guid imageId, string action, CancellationToken ct = default);
    Task<bool> CanManageOrgResourceAsync(string userId, Guid orgId, string resource, string action, CancellationToken ct = default);
}
