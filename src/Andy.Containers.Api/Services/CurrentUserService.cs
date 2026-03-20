using System.Security.Claims;

namespace Andy.Containers.Api.Services;

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string GetUserId()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
            throw new InvalidOperationException("User is not authenticated");

        return user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? throw new InvalidOperationException("User ID claim not found");
    }

    public string? GetEmail()
    {
        return _httpContextAccessor.HttpContext?.User
            ?.FindFirst(ClaimTypes.Email)?.Value
            ?? _httpContextAccessor.HttpContext?.User
                ?.FindFirst("email")?.Value;
    }

    public string? GetDisplayName()
    {
        return _httpContextAccessor.HttpContext?.User
            ?.FindFirst(ClaimTypes.Name)?.Value
            ?? _httpContextAccessor.HttpContext?.User
                ?.FindFirst("name")?.Value;
    }

    public bool IsAuthenticated()
    {
        return _httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated ?? false;
    }

    public bool IsAdmin()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        return user?.IsInRole("admin") == true
            || user?.HasClaim("role", "admin") == true;
    }

    public Guid? GetOrganizationId()
    {
        var orgClaim = _httpContextAccessor.HttpContext?.User
            ?.FindFirst("org_id")?.Value;
        return orgClaim is not null && Guid.TryParse(orgClaim, out var orgId) ? orgId : null;
    }

    public Guid? GetTeamId()
    {
        var teamClaim = _httpContextAccessor.HttpContext?.User
            ?.FindFirst("team_id")?.Value;
        return teamClaim is not null && Guid.TryParse(teamClaim, out var teamId) ? teamId : null;
    }
}
