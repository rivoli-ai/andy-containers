namespace Andy.Containers.Api.Services;

/// <summary>
/// #944 / M1.5.1 foundation. M2M token consumer that mints
/// service-to-service access tokens against <c>andy-auth</c>'s
/// <c>/connect/token</c> endpoint via the OAuth 2.0
/// <c>client_credentials</c> grant.
///
/// Used by container-side wiring (#285) to inject an
/// <c>ANDY_SERVICE_TOKEN</c> env var into provisioned containers so a
/// code assistant running inside the container can authenticate
/// against <c>andy-models</c> (and any other Conductor-backed service)
/// without manual configuration.
///
/// The implementation caches the most recently minted token until it
/// is within <see cref="ServiceTokenService.RefreshSkew"/> of expiry,
/// at which point the next call mints a fresh one.
///
/// <para>
/// <strong>Known limitation — long-lived containers (rivoli-ai/conductor#1052):</strong>
/// the token here refreshes inside the andy-containers process, but
/// the value baked into a container's env at provisioning time
/// (<c>ANDY_SERVICE_TOKEN</c>) does NOT auto-refresh. Containers
/// running longer than the OAuth client's <c>AccessTokenLifetime</c>
/// (OpenIddict default: 1 hour) hold a stale JWT until restart.
/// rivoli-ai/conductor#1052 tracks the full design for in-container
/// refresh (sidecar / token-file mount / credential helper).
/// </para>
/// </summary>
public interface IServiceTokenService
{
    /// <summary>
    /// Returns a valid bearer token for the default audience
    /// (<see cref="ServiceAuthOptions.Audience"/>). Mints a new one if
    /// no cached token is available or if the cached token is within
    /// the refresh skew of expiring.
    /// </summary>
    /// <exception cref="ServiceTokenException">
    /// Thrown when the token endpoint is unreachable, returns a
    /// non-2xx response, or returns a body the client can't parse.
    /// Caller-side should treat the failure as transient and retry
    /// at the next opportunity (per-call mint, with cache).
    /// </exception>
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a valid bearer token scoped to an explicit audience.
    /// Use this for inter-service calls where the target service's
    /// <c>aud</c> claim differs from <see cref="ServiceAuthOptions.Audience"/>
    /// — e.g. andy-containers calling andy-models (audience
    /// <c>urn:andy-models-api</c>) for rivoli-ai/conductor#943.
    ///
    /// <para>
    /// The client must be permitted to request the requested scope —
    /// add <c>scp:&lt;audience&gt;</c> to the client's
    /// <c>apiClient.scopes</c> in the registration manifest. Without
    /// that, andy-auth rejects with <c>invalid_scope</c> (OpenIddict
    /// ID2052).
    /// </para>
    ///
    /// <para>
    /// Per-audience cache: each audience has its own cached token +
    /// expiry, so concurrent calls for different audiences do not
    /// thrash a single slot.
    /// </para>
    /// </summary>
    /// <param name="audience">
    /// Unprefixed audience name (e.g. <c>urn:andy-models-api</c>). The
    /// implementation sends this as the OAuth <c>scope</c> request
    /// parameter unprefixed — the <c>scp:</c> prefix is reserved for
    /// the client-side permission, never the request body.
    /// </param>
    /// <exception cref="ServiceTokenException">
    /// Same surface as <see cref="GetAccessTokenAsync(CancellationToken)"/>;
    /// additionally fires when <paramref name="audience"/> is
    /// empty or whitespace.
    /// </exception>
    Task<string> GetAccessTokenAsync(string audience, CancellationToken ct = default);
}

/// <summary>
/// Surfaced when token minting fails. Carries enough context for
/// triage without leaking the secret on the wire (the secret is
/// never written to <see cref="Exception.Message"/>).
/// </summary>
public sealed class ServiceTokenException : Exception
{
    public ServiceTokenException(string message) : base(message) { }
    public ServiceTokenException(string message, Exception inner) : base(message, inner) { }
}
