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

    /// <summary>
    /// True when the caller is a trusted machine-to-machine SERVICE account
    /// (an OAuth <c>client_credentials</c> token), not a human user. Used to
    /// authorise "on-behalf-of" operations: a service (e.g. andy-tasks creating
    /// a goal-execution container) may set the resource owner to the originating
    /// human; a human caller can never spoof ownership.
    /// </summary>
    bool IsServiceAccount();
}
