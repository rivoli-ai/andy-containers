using System.Collections.Concurrent;
using System.Security.Cryptography;
using Andy.Containers.Configurator;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

/// <summary>
/// AP10 in-process stub issuer. Generates an opaque random token per
/// run, holds it in memory until <see cref="RevokeAsync"/>, and logs
/// both mint and revoke for debuggability. Replace with an HTTP client
/// to Y6 once that service ships — the interface stays the same.
/// </summary>
/// <remarks>
/// Singleton-scoped so the runId→token map survives across request
/// scopes (configurator mints in one request; runner revokes from a
/// different scope when it observes terminal). Tokens never persist
/// to disk — a server restart loses them, which is acceptable because
/// any in-flight run loses its container too.
/// </remarks>
public sealed class StubTokenIssuer : ITokenIssuer
{
    // Tokens have no real semantics yet; pick a generous default
    // expiry so the env var isn't surprisingly stale during long
    // runs. Configurable via SecretsOptions if a story needs to
    // tune it; until then, 24h is an arbitrary-but-safe ceiling.
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<Guid, RunToken> _tokens = new();
    private readonly ILogger<StubTokenIssuer> _logger;

    public StubTokenIssuer(ILogger<StubTokenIssuer> logger)
    {
        _logger = logger;
    }

    public Task<RunToken> MintAsync(Guid runId, CancellationToken ct = default)
    {
        // Idempotent: a configurator retry must not blow up the
        // existing token. AddOrUpdate keeps the existing one if
        // already present (Update returns the same value).
        var token = _tokens.GetOrAdd(runId, _ =>
        {
            var raw = RandomNumberGenerator.GetBytes(32);
            // URL-safe base64 — the env var lands in shell pipelines
            // and HTTP Authorization headers; '/' and '+' bite there.
            var encoded = Convert.ToBase64String(raw)
                .Replace('/', '_').Replace('+', '-').TrimEnd('=');
            var minted = new RunToken(
                Token: $"andy-run.{encoded}",
                ExpiresAt: DateTimeOffset.UtcNow + DefaultLifetime);
            _logger.LogInformation(
                "Stub token issuer: minted run-scoped token for Run {RunId} (expires {ExpiresAt})",
                runId, minted.ExpiresAt);
            return minted;
        });

        return Task.FromResult(token);
    }

    public Task<bool> RevokeAsync(Guid runId, CancellationToken ct = default)
    {
        var removed = _tokens.TryRemove(runId, out _);
        if (removed)
        {
            _logger.LogInformation(
                "Stub token issuer: revoked run-scoped token for Run {RunId}", runId);
        }
        else
        {
            _logger.LogDebug(
                "Stub token issuer: no token registered for Run {RunId} on revoke (already revoked or server restarted)",
                runId);
        }

        return Task.FromResult(removed);
    }
}
