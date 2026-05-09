namespace Andy.Containers.Api.Services;

/// <summary>
/// #944 / M1.5.1 foundation. M2M token consumer that mints
/// service-to-service access tokens against <c>andy-auth</c>'s
/// <c>/connect/token</c> endpoint via the OAuth 2.0
/// <c>client_credentials</c> grant.
///
/// Used by future container-side wiring (#944 follow-up) to inject an
/// <c>ANDY_SERVICE_TOKEN</c> env var into provisioned containers so a
/// code assistant running inside the container can authenticate
/// against <c>andy-models</c> (and any other Conductor-backed service)
/// without manual configuration.
///
/// The implementation caches the most recently minted token until it
/// is within <see cref="ServiceTokenService.RefreshSkew"/> of expiry,
/// at which point the next call mints a fresh one.
/// </summary>
public interface IServiceTokenService
{
    /// <summary>
    /// Returns a valid bearer token. Mints a new one if no cached
    /// token is available or if the cached token is within the
    /// refresh skew of expiring.
    /// </summary>
    /// <exception cref="ServiceTokenException">
    /// Thrown when the token endpoint is unreachable, returns a
    /// non-2xx response, or returns a body the client can't parse.
    /// Caller-side should treat the failure as transient and retry
    /// at the next opportunity (per-call mint, with cache).
    /// </exception>
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);
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
