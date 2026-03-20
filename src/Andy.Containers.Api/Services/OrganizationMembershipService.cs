using Andy.Containers.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Andy.Containers.Api.Services;

public class OrganizationMembershipService : IOrganizationMembershipService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IMemoryCache _cache;
    private readonly HttpClient _httpClient;
    private readonly ILogger<OrganizationMembershipService> _logger;

    private static readonly TimeSpan MembershipCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PermissionCacheTtl = TimeSpan.FromMinutes(1);

    public OrganizationMembershipService(
        IHttpContextAccessor httpContextAccessor,
        IMemoryCache cache,
        IHttpClientFactory httpClientFactory,
        ILogger<OrganizationMembershipService> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _cache = cache;
        _httpClient = httpClientFactory.CreateClient("AndyRbac");
        _logger = logger;
    }

    public async Task<bool> IsMemberAsync(string userId, Guid organizationId, CancellationToken ct = default)
    {
        var cacheKey = $"org-member:{userId}:{organizationId}";
        if (_cache.TryGetValue(cacheKey, out bool cached))
            return cached;

        // Check JWT claims first
        var claims = _httpContextAccessor.HttpContext?.User;
        if (claims != null)
        {
            var orgClaim = claims.FindFirst("org_id")?.Value;
            if (orgClaim != null && Guid.TryParse(orgClaim, out var claimOrgId) && claimOrgId == organizationId)
            {
                _cache.Set(cacheKey, true, MembershipCacheTtl);
                return true;
            }

            var orgIdsClaim = claims.FindFirst("org_ids")?.Value;
            if (orgIdsClaim != null)
            {
                var orgIds = orgIdsClaim.Split(',', StringSplitOptions.RemoveEmptyEntries);
                var isMember = orgIds.Any(id => Guid.TryParse(id.Trim(), out var oid) && oid == organizationId);
                _cache.Set(cacheKey, isMember, MembershipCacheTtl);
                return isMember;
            }
        }

        // Fallback: call RBAC API
        var result = await CallRbacApiMembershipAsync(userId, organizationId, ct);
        _cache.Set(cacheKey, result, MembershipCacheTtl);
        return result;
    }

    public async Task<string?> GetRoleAsync(string userId, Guid organizationId, CancellationToken ct = default)
    {
        var cacheKey = $"org-role:{userId}:{organizationId}";
        if (_cache.TryGetValue(cacheKey, out string? cached))
            return cached;

        // Check JWT claims first
        var claims = _httpContextAccessor.HttpContext?.User;
        if (claims != null)
        {
            var orgClaim = claims.FindFirst("org_id")?.Value;
            if (orgClaim != null && Guid.TryParse(orgClaim, out var claimOrgId) && claimOrgId == organizationId)
            {
                var roleClaim = claims.FindFirst("org_role")?.Value;
                if (roleClaim != null)
                {
                    _cache.Set(cacheKey, roleClaim, MembershipCacheTtl);
                    return roleClaim;
                }
            }
        }

        // Fallback: call RBAC API
        var role = await CallRbacApiRoleAsync(userId, organizationId, ct);
        _cache.Set(cacheKey, role, MembershipCacheTtl);
        return role;
    }

    public async Task<bool> HasPermissionAsync(string userId, Guid organizationId, string permission, CancellationToken ct = default)
    {
        var cacheKey = $"org-perm:{userId}:{organizationId}:{permission}";
        if (_cache.TryGetValue(cacheKey, out bool cached))
            return cached;

        var role = await GetRoleAsync(userId, organizationId, ct);
        if (role == null)
        {
            _cache.Set(cacheKey, false, PermissionCacheTtl);
            return false;
        }

        var permissions = OrgRoles.GetPermissions(role);
        var hasPermission = permissions.Contains(permission);
        _cache.Set(cacheKey, hasPermission, PermissionCacheTtl);
        return hasPermission;
    }

    public async Task<IReadOnlyList<Guid>> GetUserOrganizationsAsync(string userId, CancellationToken ct = default)
    {
        var cacheKey = $"org-list:{userId}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<Guid>? cached))
            return cached!;

        // Check JWT claims first
        var claims = _httpContextAccessor.HttpContext?.User;
        var orgIds = new List<Guid>();

        if (claims != null)
        {
            var orgClaim = claims.FindFirst("org_id")?.Value;
            if (orgClaim != null && Guid.TryParse(orgClaim, out var claimOrgId))
                orgIds.Add(claimOrgId);

            var orgIdsClaim = claims.FindFirst("org_ids")?.Value;
            if (orgIdsClaim != null)
            {
                foreach (var id in orgIdsClaim.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Guid.TryParse(id.Trim(), out var oid) && !orgIds.Contains(oid))
                        orgIds.Add(oid);
                }
            }
        }

        if (orgIds.Count == 0)
        {
            // Fallback: call RBAC API
            orgIds = (await CallRbacApiOrganizationsAsync(userId, ct)).ToList();
        }

        IReadOnlyList<Guid> result = orgIds;
        _cache.Set(cacheKey, result, MembershipCacheTtl);
        return result;
    }

    private async Task<bool> CallRbacApiMembershipAsync(string userId, Guid organizationId, CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetAsync($"api/organizations/{organizationId}/members/{userId}", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check org membership via RBAC API for user {UserId} in org {OrgId}", userId, organizationId);
            return false;
        }
    }

    private async Task<string?> CallRbacApiRoleAsync(string userId, Guid organizationId, CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetAsync($"api/organizations/{organizationId}/members/{userId}/role", ct);
            if (!response.IsSuccessStatusCode) return null;
            var role = await response.Content.ReadAsStringAsync(ct);
            return role.Trim('"');
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get org role via RBAC API for user {UserId} in org {OrgId}", userId, organizationId);
            return null;
        }
    }

    private async Task<IReadOnlyList<Guid>> CallRbacApiOrganizationsAsync(string userId, CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetAsync($"api/users/{userId}/organizations", ct);
            if (!response.IsSuccessStatusCode) return [];
            var json = await response.Content.ReadAsStringAsync(ct);
            return System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(json) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get organizations via RBAC API for user {UserId}", userId);
            return [];
        }
    }
}
