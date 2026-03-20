namespace Andy.Containers.Api.Services;

/// <summary>
/// Provides access to the current authenticated user's identity.
/// In development mode, returns a configurable dev user when no auth is present.
/// </summary>
public interface ICurrentUserService
{
    string GetUserId();
    string? GetEmail();
    string? GetDisplayName();
    bool IsAuthenticated();
    bool IsAdmin();
    Guid? GetOrganizationId();
    Guid? GetTeamId();
}
