using Andy.Containers.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Services;

public class ContainerPermissionService : IContainerPermissionService
{
    private readonly ContainersDbContext _db;
    private readonly ICurrentUserService _currentUser;

    public ContainerPermissionService(ContainersDbContext db, ICurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<bool> HasPermissionAsync(string userId, Guid containerId, string permission, CancellationToken ct = default)
    {
        // Admin always has access
        if (_currentUser.IsAdmin())
            return true;

        var container = await _db.Containers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == containerId, ct);

        if (container is null)
            return false;

        // Owner always has access
        if (container.OwnerId == userId)
            return true;

        // Team-level access for container:connect
        if (permission == "container:connect" && container.TeamId.HasValue)
        {
            var userTeamId = _currentUser.GetTeamId();
            if (userTeamId.HasValue && userTeamId == container.TeamId)
                return true;
        }

        // Organization-level access for container:connect
        if (permission == "container:connect" && container.OrganizationId.HasValue)
        {
            var userOrgId = _currentUser.GetOrganizationId();
            if (userOrgId.HasValue && userOrgId == container.OrganizationId)
                return true;
        }

        return false;
    }
}
