namespace Andy.Containers.Api.Services;

/// <summary>
/// Thrown by <see cref="ContainerOrchestrationService.CreateContainerAsync"/>
/// when the requesting user already owns the configured maximum
/// number of simultaneous live containers. The controller
/// translates this into HTTP 422 with the structured envelope
/// the Conductor UI expects.
///
/// Conductor #878. Carries enough context for the UI to render a
/// useful inline alert ("You're at the 32-container limit. Destroy
/// one to free up space.") without a second round-trip to learn
/// the limit.
/// </summary>
public sealed class QuotaExceededException : Exception
{
    /// <summary>The configured cap that was hit.</summary>
    public int Limit { get; }

    /// <summary>The user's container count at the time of the
    /// failed request. Equal to <see cref="Limit"/> in practice.</summary>
    public int Current { get; }

    /// <summary>The OwnerId that hit the cap.</summary>
    public string OwnerId { get; }

    /// <summary>
    /// Stable machine-readable code surfaced in the API response
    /// so clients can switch on this rather than parsing the
    /// human message.
    /// </summary>
    public const string Code = "QUOTA_EXCEEDED_PER_USER_CONTAINERS";

    public QuotaExceededException(int limit, int current, string ownerId)
        : base($"User {ownerId} already has {current} containers (limit {limit}).")
    {
        Limit = limit;
        Current = current;
        OwnerId = ownerId;
    }
}
