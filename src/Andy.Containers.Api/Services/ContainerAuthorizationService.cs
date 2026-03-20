using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Services;

public class ContainerAuthorizationService : IContainerAuthorizationService
{
    private readonly ContainersDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IOrganizationMembershipService _orgMembership;
    private readonly ILogger<ContainerAuthorizationService> _logger;

    public ContainerAuthorizationService(
        ContainersDbContext db,
        ICurrentUserService currentUser,
        IOrganizationMembershipService orgMembership,
        ILogger<ContainerAuthorizationService> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _orgMembership = orgMembership;
        _logger = logger;
    }

    public async Task<bool> CanAccessContainerAsync(string userId, Guid containerId, string action, CancellationToken ct = default)
    {
        if (_currentUser.IsAdmin()) return true;

        var container = await _db.Containers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == containerId, ct);
        if (container is null) return false;

        // Owner always has access to their own container
        if (container.OwnerId == userId) return true;

        // Check org membership if container is org-scoped
        if (container.OrganizationId.HasValue)
        {
            return await _orgMembership.IsMemberAsync(userId, container.OrganizationId.Value, ct);
        }

        return false;
    }

    public async Task<bool> CanAccessTemplateAsync(string userId, Guid templateId, string action, CancellationToken ct = default)
    {
        if (_currentUser.IsAdmin()) return true;

        var template = await _db.Templates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId, ct);
        if (template is null) return false;

        // Global templates: anyone can read, only admins can modify
        if (template.CatalogScope == CatalogScope.Global)
            return action == "read";

        // Owner access
        if (template.OwnerId == userId) return true;

        // Org-scoped template: check org permissions
        if (template.OrganizationId.HasValue)
        {
            var permission = $"template:{action}";
            return await _orgMembership.HasPermissionAsync(userId, template.OrganizationId.Value, permission, ct);
        }

        return false;
    }

    public async Task<bool> CanAccessImageAsync(string userId, Guid imageId, string action, CancellationToken ct = default)
    {
        if (_currentUser.IsAdmin()) return true;

        var image = await _db.Images.AsNoTracking().FirstOrDefaultAsync(i => i.Id == imageId, ct);
        if (image is null) return false;

        // Global images: anyone can read
        if (image.Visibility == ImageVisibility.Global)
            return action == "read" || action == "build";

        // Owner access
        if (image.OwnerId == userId) return true;

        // Org-scoped images: check org permissions
        if (image.OrganizationId.HasValue)
        {
            var permission = $"image:{action}";
            return await _orgMembership.HasPermissionAsync(userId, image.OrganizationId.Value, permission, ct);
        }

        return false;
    }

    public async Task<bool> CanManageOrgResourceAsync(string userId, Guid orgId, string resource, string action, CancellationToken ct = default)
    {
        if (_currentUser.IsAdmin())
        {
            _logger.LogInformation("Platform admin {UserId} accessing org {OrgId} resource {Resource}:{Action}", userId, orgId, resource, action);
            return true;
        }

        var permission = $"{resource}:{action}";
        return await _orgMembership.HasPermissionAsync(userId, orgId, permission, ct);
    }
}
