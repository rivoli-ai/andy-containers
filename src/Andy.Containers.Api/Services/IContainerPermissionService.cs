namespace Andy.Containers.Api.Services;

public interface IContainerPermissionService
{
    Task<bool> HasPermissionAsync(string userId, Guid containerId, string permission, CancellationToken ct = default);
}
