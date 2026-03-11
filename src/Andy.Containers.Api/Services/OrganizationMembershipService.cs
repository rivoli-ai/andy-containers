using Microsoft.Extensions.Caching.Memory;

namespace Andy.Containers.Api.Services;

public class OrganizationMembershipService : IOrganizationMembershipService
{
    private readonly ICurrentUserService _currentUser;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan MembershipCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PermissionCacheTtl = TimeSpan.FromMinutes(1);

    private static readonly Dictionary<string, HashSet<string>> RolePermissions = new()
    {
        ["org:admin"] = [
            "image:create", "image:read", "image:build", "image:publish", "image:delete",
            "template:create", "template:read", "template:publish", "template:manage",
            "provider:read", "provider:manage"
        ],
        ["org:editor"] = [
            "image:create", "image:read", "image:build",
            "template:create", "template:read", "template:manage"
        ],
        ["org:viewer"] = [
            "image:read", "template:read", "provider:read"
        ]
    };

    public OrganizationMembershipService(ICurrentUserService currentUser, IMemoryCache cache)
    {
        _currentUser = currentUser;
        _cache = cache;
    }

    public Task<bool> IsMemberAsync(string userId, Guid organizationId, CancellationToken ct = default)
    {
        if (_currentUser.IsAdmin()) return Task.FromResult(true);

        var cacheKey = $"org-member:{userId}:{organizationId}";
        if (_cache.TryGetValue(cacheKey, out bool cached))
            return Task.FromResult(cached);

        var userOrgId = _currentUser.GetOrganizationId();
        var isMember = userOrgId.HasValue && userOrgId.Value == organizationId;

        _cache.Set(cacheKey, isMember, MembershipCacheTtl);
        return Task.FromResult(isMember);
    }

    public Task<string?> GetRoleAsync(string userId, Guid organizationId, CancellationToken ct = default)
    {
        if (_currentUser.IsAdmin()) return Task.FromResult<string?>("org:admin");

        var cacheKey = $"org-role:{userId}:{organizationId}";
        if (_cache.TryGetValue(cacheKey, out string? cachedRole))
            return Task.FromResult(cachedRole);

        var userOrgId = _currentUser.GetOrganizationId();
        if (!userOrgId.HasValue || userOrgId.Value != organizationId)
        {
            _cache.Set(cacheKey, (string?)null, MembershipCacheTtl);
            return Task.FromResult<string?>(null);
        }

        // Default to editor role; in production this would come from RBAC API
        var role = "org:editor";
        _cache.Set(cacheKey, (string?)role, MembershipCacheTtl);
        return Task.FromResult<string?>(role);
    }

    public async Task<bool> HasPermissionAsync(string userId, Guid organizationId, string permission, CancellationToken ct = default)
    {
        if (_currentUser.IsAdmin()) return true;

        var cacheKey = $"org-perm:{userId}:{organizationId}:{permission}";
        if (_cache.TryGetValue(cacheKey, out bool cached))
            return cached;

        var role = await GetRoleAsync(userId, organizationId, ct);
        var hasPermission = role is not null
            && RolePermissions.TryGetValue(role, out var perms)
            && perms.Contains(permission);

        _cache.Set(cacheKey, hasPermission, PermissionCacheTtl);
        return hasPermission;
    }

    public Task<IReadOnlyList<Guid>> GetUserOrganizationsAsync(string userId, CancellationToken ct = default)
    {
        var orgId = _currentUser.GetOrganizationId();
        IReadOnlyList<Guid> result = orgId.HasValue ? [orgId.Value] : [];
        return Task.FromResult(result);
    }
}
