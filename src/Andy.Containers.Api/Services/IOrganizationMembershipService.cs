namespace Andy.Containers.Api.Services;

public interface IOrganizationMembershipService
{
    Task<bool> IsMemberAsync(string userId, Guid organizationId, CancellationToken ct = default);
    Task<string?> GetRoleAsync(string userId, Guid organizationId, CancellationToken ct = default);
    Task<bool> HasPermissionAsync(string userId, Guid organizationId, string permission, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetUserOrganizationsAsync(string userId, CancellationToken ct = default);
}
