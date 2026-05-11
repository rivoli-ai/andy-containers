namespace Andy.Containers.Api.Services;

/// <summary>
/// rivoli-ai/conductor#943 (M1.5.1). Per-container service-token consumer.
///
/// At container creation time we call <see cref="MintForContainerAsync"/>
/// to ask andy-models for a JWT scoped to <c>{containerId, allowedSlugs[]}</c>.
/// That JWT is injected into the container as <c>ANDY_SERVICE_TOKEN</c> so
/// the code assistant inside can authenticate against the unified proxy
/// for exactly the slugs we minted it for — narrower than the user's full
/// RBAC grant. On destroy we call <see cref="RevokeAsync"/> to invalidate
/// the JWT (denylist enforcement).
///
/// Distinct from <see cref="IServiceTokenService"/>: that one mints the
/// single, process-wide M2M bearer that andy-containers itself uses to
/// authenticate as a service against andy-auth (and indirectly to call
/// this very endpoint on andy-models). Container-facing injection used
/// to leak that broad token; with #943 it's replaced with the narrowly
/// scoped per-container JWT minted by andy-models.
/// </summary>
public interface IProxyTokenService
{
    /// <summary>
    /// Ask andy-models to mint a fresh proxy token. Returns <c>null</c>
    /// when <paramref name="allowedSlugs"/> is empty — that signals
    /// "this container talks to its model surface directly" (Ollama,
    /// OpenAI-compatible self-hosted) and no proxy token is needed.
    /// </summary>
    /// <exception cref="ProxyTokenException">
    /// Thrown when andy-models is unreachable, returns a non-2xx, or
    /// returns a body the client can't parse. Container creation should
    /// fail fast on this — the assistant inside would otherwise start
    /// without working credentials and produce confusing 401s.
    /// </exception>
    Task<MintedProxyToken?> MintForContainerAsync(
        string containerId,
        string subjectId,
        IReadOnlyList<string> allowedSlugs,
        CancellationToken ct = default);

    /// <summary>
    /// Revoke a previously minted token by its andy-models row id.
    /// Idempotent — andy-models returns 204 for unknown ids too. Logs
    /// and swallows transport errors so container destroy never wedges
    /// on a slow / down andy-models; the token will expire naturally
    /// at its <c>exp</c> claim anyway.
    /// </summary>
    Task RevokeAsync(Guid tokenId, CancellationToken ct = default);
}

/// <summary>Return shape of <see cref="IProxyTokenService.MintForContainerAsync"/>.</summary>
public sealed record MintedProxyToken(Guid TokenId, string Jwt, DateTimeOffset ExpiresAt);

/// <summary>
/// Surfaced when minting fails. Carries enough context for triage; the
/// JWT itself is never written to <see cref="Exception.Message"/>.
/// </summary>
public sealed class ProxyTokenException : Exception
{
    public ProxyTokenException(string message) : base(message) { }
    public ProxyTokenException(string message, Exception inner) : base(message, inner) { }
}
