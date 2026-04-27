namespace Andy.Containers.Configurator;

/// <summary>
/// AP10 (rivoli-ai/andy-containers#112). Mints and revokes run-scoped
/// credentials. Today the implementation is an in-process stub
/// (<see cref="StubTokenIssuer"/>); when Y6 (the auth-issuer service)
/// lands, it will be replaced by an HTTP client to that service.
/// Consumers code against the interface so the swap is invisible.
/// </summary>
/// <remarks>
/// The contract is deliberately minimal: caller passes the
/// <see cref="Run.Id"/>, gets a token back, and later asks the issuer
/// to revoke. The issuer owns the runId→token mapping internally so
/// callers don't have to plumb token strings through the pipeline.
/// Revocation is best-effort: a revoke after the run is already gone
/// (e.g. server restart between mint and terminal) is a no-op.
/// </remarks>
public interface ITokenIssuer
{
    /// <summary>
    /// Mint a fresh run-scoped token. Returns the token string and its
    /// expiry. Calling twice for the same <paramref name="runId"/>
    /// returns the existing token rather than minting a new one — the
    /// configurator may run twice (initial + retry) and we don't want
    /// orphaned tokens piling up at the issuer.
    /// </summary>
    Task<RunToken> MintAsync(Guid runId, CancellationToken ct = default);

    /// <summary>
    /// Revoke the token associated with <paramref name="runId"/>, if any.
    /// Returns true when a token was revoked; false when no token was
    /// registered for that id (server restart, double-revoke, etc.) —
    /// callers treat both as success since the post-condition (no live
    /// token) holds either way.
    /// </summary>
    Task<bool> RevokeAsync(Guid runId, CancellationToken ct = default);
}

/// <summary>
/// Minted run-scoped credential. <see cref="Token"/> is the bearer
/// string injected as <c>ANDY_TOKEN</c> into the agent's environment;
/// <see cref="ExpiresAt"/> is informational (the issuer enforces
/// expiry server-side, not the client).
/// </summary>
public sealed record RunToken(string Token, DateTimeOffset ExpiresAt);
