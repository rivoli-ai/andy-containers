using Andy.Containers.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Services;

public class ContainerAuthorizationService : IContainerAuthorizationService
{
    private readonly ICurrentUserService _currentUser;
    private readonly IOrganizationMembershipService _orgMembership;
    private readonly ContainersDbContext _db;

    public ContainerAuthorizationService(
        ICurrentUserService currentUser,
        IOrganizationMembershipService orgMembership,
        ContainersDbContext db)
    {
        _currentUser = currentUser;
        _orgMembership = orgMembership;
        _db = db;
    }

    public async Task<bool> CanAccessContainerAsync(string userId, Guid containerId, string action, CancellationToken ct = default)
    {
        if (_currentUser.IsAdmin()) return true;

        var container = await _db.Containers.FindAsync([containerId], ct);
        if (container is null) return false;
        if (container.OwnerId == userId) return true;

        if (container.OrganizationId.HasValue)
            return await _orgMembership.HasPermissionAsync(userId, container.OrganizationId.Value, $"container:{action}", ct);

        return false;
    }

    public async Task<bool> CanAccessImageAsync(string userId, Guid imageId, string action, CancellationToken ct = default)
    {
        if (_currentUser.IsAdmin()) return true;

        var image = await _db.Images.FindAsync([imageId], ct);
        if (image is null) return false;

        // Global images: any authenticated user can read
        if (!image.OrganizationId.HasValue)
            return action == "read";

        return await _orgMembership.HasPermissionAsync(userId, image.OrganizationId.Value, $"image:{action}", ct);
    }

    public async Task<bool> CanManageOrgResourceAsync(string userId, Guid orgId, string resource, string action, CancellationToken ct = default)
    {
        if (_currentUser.IsAdmin()) return true;
        return await _orgMembership.HasPermissionAsync(userId, orgId, $"{resource}:{action}", ct);
    }
}
