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

    public bool IsServiceAccount()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        // Positive, unambiguous signal: an OAuth client_credentials (M2M) token
        // represents the CLIENT itself — OpenIddict (andy-auth) sets the token's
        // subject to the client id, so `sub` == `client_id`/`azp`. There is no
        // human behind such a token.
        var sub = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value;
        var clientId = user.FindFirst("client_id")?.Value
            ?? user.FindFirst("azp")?.Value;
        if (!string.IsNullOrEmpty(sub)
            && !string.IsNullOrEmpty(clientId)
            && string.Equals(sub, clientId, StringComparison.Ordinal))
        {
            return true;
        }

        // Fallback (when the access token omits a public client_id claim): an
        // authenticated token carrying NO human-identity claim is a service
        // token. A human OIDC token always carries at least one of email /
        // name / preferred_username (from the identity scopes); a
        // client_credentials token carries none.
        var hasHumanIdentity =
            !string.IsNullOrEmpty(user.FindFirst(ClaimTypes.Email)?.Value)
            || !string.IsNullOrEmpty(user.FindFirst("email")?.Value)
            || !string.IsNullOrEmpty(user.FindFirst(ClaimTypes.Name)?.Value)
            || !string.IsNullOrEmpty(user.FindFirst("name")?.Value)
            || !string.IsNullOrEmpty(user.FindFirst("preferred_username")?.Value);
        return !hasHumanIdentity;
    }
}
