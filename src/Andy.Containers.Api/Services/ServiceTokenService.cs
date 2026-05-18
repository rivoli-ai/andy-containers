using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Andy.Containers.Api.Services;

/// <summary>
/// #944 / M1.5.1 foundation. Concrete <see cref="IServiceTokenService"/>
/// that mints client-credentials tokens from <c>andy-auth</c>.
///
/// Single-process token cache: serialised behind a <see cref="SemaphoreSlim"/>
/// so a thundering herd of concurrent <see cref="GetAccessTokenAsync"/>
/// calls produces exactly one mint instead of N. The cache is in-
/// memory only — when the host restarts, the next call mints a fresh
/// token, which is fine for our usage pattern (token is materialised
/// at container creation time, then handed off as an env var).
/// </summary>
public sealed class ServiceTokenService : IServiceTokenService, IDisposable
{
    /// <summary>
    /// Mint a fresh token when the cached one is within this window
    /// of its expiry. 30s gives a comfortable margin for clock skew
    /// + the time it takes the token to reach the consumer + verify.
    /// </summary>
    public static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<ServiceAuthOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ServiceTokenService> _logger;

    // rivoli-ai/conductor#1055. Per-audience cache. Each audience the
    // process talks to (urn:andy-containers-api, urn:andy-models-api,
    // future others) gets its own slot. A single shared gate is fine
    // for the dev/embedded volume — the critical section is just a
    // dictionary lookup + an HTTP mint behind the cache miss.
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    // Epic IDP (rivoli-ai/conductor#1246). Separate cache for OBO
    // tokens, keyed by (SHA-256 of the user's subject token, audience).
    // Hashing keeps the raw user JWT out of cache keys.
    private readonly Dictionary<DelegatedCacheKey, CacheEntry> _delegatedCache = new();

    /// <summary>HttpClient name registered in DI; tests can override.</summary>
    public const string HttpClientName = "ServiceTokenClient";

    public ServiceTokenService(
        IHttpClientFactory httpFactory,
        IOptions<ServiceAuthOptions> options,
        ILogger<ServiceTokenService> logger,
        TimeProvider? timeProvider = null)
    {
        _httpFactory = httpFactory;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        // Default-audience path — preserves the existing contract for
        // callers that don't care about cross-service audiences.
        var defaultAudience = _options.Value.Audience;
        if (string.IsNullOrWhiteSpace(defaultAudience))
        {
            throw new ServiceTokenException(
                "ServiceAuth:Audience is not configured. Cannot mint service-to-service tokens without an audience.");
        }
        return GetAccessTokenAsync(defaultAudience, ct);
    }

    public async Task<string> GetAccessTokenAsync(string audience, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(audience))
        {
            throw new ServiceTokenException(
                "Audience is required for inter-service token requests.");
        }

        // Fast path: token cached for this audience + comfortably ahead
        // of expiry. Dictionary reads of a single slot are safe under
        // .NET's memory model when concurrent writers serialise behind
        // the gate, which they do (only the lock'd block below writes).
        if (TryGetFreshCached(audience, out var fast))
        {
            return fast;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check inside the lock — another caller may have
            // refreshed while we were waiting.
            if (TryGetFreshCached(audience, out var current))
            {
                return current;
            }

            var fresh = await MintAsync(audience, ct).ConfigureAwait(false);
            // MintAsync throws on a null/empty access_token, so the
            // null-forgiving operator is sound here — the only way
            // we're past the throw above is if AccessToken is set.
            var expiry = _timeProvider.GetUtcNow() + TimeSpan.FromSeconds(fresh.ExpiresInSeconds);
            _cache[audience] = new CacheEntry(fresh.AccessToken!, expiry);
            return fresh.AccessToken!;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryGetFreshCached(string audience, out string token)
    {
        if (_cache.TryGetValue(audience, out var entry)
            && _timeProvider.GetUtcNow() + RefreshSkew < entry.Expiry)
        {
            token = entry.Token;
            return true;
        }
        token = string.Empty;
        return false;
    }

    public async Task<string> GetOnBehalfOfTokenAsync(
        string subjectToken,
        string audience,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subjectToken))
        {
            throw new ServiceTokenException(
                "subject_token is required for on-behalf-of token requests.");
        }
        if (string.IsNullOrWhiteSpace(audience))
        {
            throw new ServiceTokenException(
                "Audience is required for on-behalf-of token requests.");
        }

        var key = new DelegatedCacheKey(HashSubject(subjectToken), audience);

        // Fast path: cached and not within refresh skew.
        if (TryGetFreshCachedDelegated(key, out var fast))
        {
            return fast;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring the gate.
            if (TryGetFreshCachedDelegated(key, out var current))
            {
                return current;
            }

            var fresh = await MintOnBehalfOfAsync(subjectToken, audience, ct).ConfigureAwait(false);
            var expiry = _timeProvider.GetUtcNow() + TimeSpan.FromSeconds(fresh.ExpiresInSeconds);
            _delegatedCache[key] = new CacheEntry(fresh.AccessToken!, expiry);
            return fresh.AccessToken!;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryGetFreshCachedDelegated(DelegatedCacheKey key, out string token)
    {
        if (_delegatedCache.TryGetValue(key, out var entry)
            && _timeProvider.GetUtcNow() + RefreshSkew < entry.Expiry)
        {
            token = entry.Token;
            return true;
        }
        token = string.Empty;
        return false;
    }

    private async Task<TokenResponse> MintOnBehalfOfAsync(
        string subjectToken,
        string audience,
        CancellationToken ct)
    {
        var opts = _options.Value;
        if (string.IsNullOrWhiteSpace(opts.TokenEndpoint))
        {
            throw new ServiceTokenException(
                "ServiceAuth:TokenEndpoint is not configured. Cannot mint on-behalf-of tokens.");
        }
        if (string.IsNullOrWhiteSpace(opts.ClientId))
        {
            throw new ServiceTokenException(
                "ServiceAuth:ClientId is not configured. Cannot mint on-behalf-of tokens.");
        }

        var http = _httpFactory.CreateClient(HttpClientName);

        // RFC 8693 §2.1 wire shape. The grant URN and token-type URN
        // are normative; the `resource` parameter names the downstream
        // audience. The `scope` parameter is included so OpenIddict's
        // resource validator can resolve the audience through the seeded
        // scope→resource mapping (registration-manifest-driven); without
        // it, the embedded andy-auth — which has no `OpenIddict:Resources`
        // config — rejects the request with `invalid_target`.
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["client_id"] = opts.ClientId,
            ["client_secret"] = opts.ClientSecret ?? string.Empty,
            ["subject_token"] = subjectToken,
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["resource"] = audience,
            ["scope"] = audience,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, opts.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceTokenException(
                $"Token endpoint unreachable for OBO exchange: {opts.TokenEndpoint}", ex);
        }

        using var _ = response;
        if (!response.IsSuccessStatusCode)
        {
            var bodyPreview = await SafeReadPreviewAsync(response, ct).ConfigureAwait(false);
            throw new ServiceTokenException(
                $"Token endpoint returned HTTP {(int)response.StatusCode} for OBO exchange (audience={audience}). " +
                $"Body preview: {bodyPreview}");
        }

        TokenResponse? parsed;
        try
        {
            parsed = await response.Content.ReadFromJsonAsync<TokenResponse>(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new ServiceTokenException(
                "Token endpoint OBO response was not valid JSON.", ex);
        }
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.AccessToken))
        {
            throw new ServiceTokenException(
                "Token endpoint OBO response is missing the `access_token` field.");
        }

        _logger.LogInformation(
            "Minted OBO token (clientId={ClientId} audience={Audience} expiresInSeconds={ExpiresInSeconds})",
            opts.ClientId, audience, parsed.ExpiresInSeconds);
        return parsed;
    }

    private static string HashSubject(string subjectToken)
    {
        var bytes = Encoding.UTF8.GetBytes(subjectToken);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Cache key for OBO tokens. Hashing the subject token keeps the
    /// raw user JWT out of the dictionary key.
    /// </summary>
    private readonly record struct DelegatedCacheKey(string SubjectTokenHash, string Audience);

    private async Task<TokenResponse> MintAsync(string audience, CancellationToken ct)
    {
        var opts = _options.Value;
        if (string.IsNullOrWhiteSpace(opts.TokenEndpoint))
        {
            throw new ServiceTokenException(
                "ServiceAuth:TokenEndpoint is not configured. Cannot mint service-to-service tokens.");
        }
        if (string.IsNullOrWhiteSpace(opts.ClientId))
        {
            throw new ServiceTokenException(
                "ServiceAuth:ClientId is not configured. Cannot mint service-to-service tokens.");
        }

        var http = _httpFactory.CreateClient(HttpClientName);

        // application/x-www-form-urlencoded body per OAuth 2.0
        // (RFC 6749 §4.4.2). The audience field is non-standard but
        // accepted by OpenIddict (which andy-auth uses).
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = opts.ClientId,
            ["client_secret"] = opts.ClientSecret ?? string.Empty,
            // The `scope` request parameter takes the unprefixed scope
            // name (== the audience, the way andy-auth's seeder
            // registers it via `OpenIddictScopeDescriptor.Name = audience`).
            // The `scp:` prefix is reserved for the client-side
            // *permission* on the OpenIddict application descriptor
            // (`Permissions.Add("scp:urn:andy-containers-api")`), not
            // the request body. Sending `scp:urn:…` here gets
            // rejected as `invalid_scope` (OpenIddict ID2052).
            ["scope"] = audience,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, opts.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceTokenException(
                $"Token endpoint unreachable: {opts.TokenEndpoint}", ex);
        }

        using var _ = response;
        if (!response.IsSuccessStatusCode)
        {
            var bodyPreview = await SafeReadPreviewAsync(response, ct).ConfigureAwait(false);
            // Status code first so the typical failures (401 from
            // wrong secret, 404 from wrong path) are obvious in the
            // logs without a JSON parse step.
            throw new ServiceTokenException(
                $"Token endpoint returned HTTP {(int)response.StatusCode}. Body preview: {bodyPreview}");
        }

        TokenResponse? parsed;
        try
        {
            parsed = await response.Content.ReadFromJsonAsync<TokenResponse>(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new ServiceTokenException(
                "Token endpoint response was not valid JSON.", ex);
        }
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.AccessToken))
        {
            throw new ServiceTokenException(
                "Token endpoint response is missing the `access_token` field.");
        }

        _logger.LogInformation(
            "Minted service token (clientId={ClientId} audience={Audience} expiresInSeconds={ExpiresInSeconds})",
            opts.ClientId, audience, parsed.ExpiresInSeconds);
        return parsed;
    }

    /// <summary>Per-audience cache slot.</summary>
    private readonly record struct CacheEntry(string Token, DateTimeOffset Expiry);

    private static async Task<string> SafeReadPreviewAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return raw.Length <= 200 ? raw : raw[..200] + "…";
        }
        catch
        {
            return "<failed to read response body>";
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <summary>
    /// Wire shape of <c>POST /connect/token</c> response per RFC 6749 §5.1.
    /// </summary>
    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresInSeconds { get; init; }

        [JsonPropertyName("scope")]
        public string? Scope { get; init; }
    }
}

/// <summary>
/// Configuration for the M2M token consumer (#944).
///
/// Bound from <c>ServiceAuth:</c> in <c>appsettings.json</c> /
/// environment overrides. Conductor injects these via
/// <c>ContainersServiceConfig.environmentOverrides</c>.
/// </summary>
public sealed class ServiceAuthOptions
{
    public const string SectionName = "ServiceAuth";

    /// <summary>
    /// Full URL to <c>andy-auth</c>'s <c>/connect/token</c> endpoint —
    /// e.g. <c>http://localhost:9100/auth/connect/token</c>.
    /// </summary>
    public string? TokenEndpoint { get; set; }

    /// <summary>
    /// OAuth 2.0 client_id. Defaults to <c>andy-containers-api</c>
    /// (matching the manifest entry added in #944).
    /// </summary>
    public string ClientId { get; set; } = "andy-containers-api";

    /// <summary>
    /// OAuth 2.0 client_secret. Falls back to the legacy
    /// <c>{clientId}-secret-change-in-production</c> shape that
    /// andy-auth's <c>DbSeeder.ResolveClientSecret</c> generates when
    /// no <c>clientSecretEnvVar</c> value is available — handy for
    /// dev / embedded mode without touching env vars.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Audience to request, included as <c>scope=scp:&lt;audience&gt;</c>
    /// per the existing OpenIddict scope shape used in this codebase.
    /// Defaults to the service's own audience so a token minted here
    /// is acceptable to <c>andy-containers</c> itself; for inter-
    /// service calls (e.g. talking to <c>andy-models</c>) the caller
    /// can override this per-mint in a future extension.
    /// </summary>
    public string Audience { get; set; } = "urn:andy-containers-api";
}
