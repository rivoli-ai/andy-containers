using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Andy.Containers.Api.Services;

/// <summary>
/// rivoli-ai/conductor#943 (M1.5.1). <see cref="IProxyTokenService"/>
/// implementation that talks to andy-models'
/// <c>POST /api/proxy/tokens</c> (M1.3.3) and
/// <c>DELETE /api/proxy/tokens/{id}</c> via an authenticated
/// <see cref="HttpClient"/> using the M2M bearer from
/// <see cref="IServiceTokenService"/>.
/// </summary>
public sealed class AndyModelsProxyTokenService : IProxyTokenService
{
    /// <summary>Named HttpClient registered in DI. Tests can override.</summary>
    public const string HttpClientName = "AndyModelsProxyTokensClient";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IServiceTokenService _serviceTokens;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IOptions<AndyModelsOptions> _options;
    private readonly ILogger<AndyModelsProxyTokenService> _logger;

    public AndyModelsProxyTokenService(
        IHttpClientFactory httpFactory,
        IServiceTokenService serviceTokens,
        IHttpContextAccessor httpContextAccessor,
        IOptions<AndyModelsOptions> options,
        ILogger<AndyModelsProxyTokenService> logger)
    {
        _httpFactory = httpFactory;
        _serviceTokens = serviceTokens;
        _httpContextAccessor = httpContextAccessor;
        _options = options;
        _logger = logger;
    }

    public async Task<MintedProxyToken?> MintForContainerAsync(
        string containerId,
        string subjectId,
        IReadOnlyList<string> allowedSlugs,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        ArgumentNullException.ThrowIfNull(allowedSlugs);

        if (allowedSlugs.Count == 0)
        {
            // No slugs → no proxy token. Caller (orchestration service)
            // treats this as "this container does its own auth".
            return null;
        }

        var opts = _options.Value;
        if (string.IsNullOrWhiteSpace(opts.BaseUrl))
        {
            throw new ProxyTokenException(
                "AndyModels:BaseUrl is not configured. Cannot mint per-container proxy tokens.");
        }

        var bearer = await GetMintBearerAsync(ct).ConfigureAwait(false);
        var http = _httpFactory.CreateClient(HttpClientName);

        // Resolve against the configured base. Path matches the
        // controller's [Route("api/proxy/tokens")] in andy-models.
        var url = CombinePathStrict(opts.BaseUrl!, "api/proxy/tokens");
        // Normalise the lifetime hint: 0 / negative means "omit"
        // (let andy-models apply its own default). The controller
        // already coerces null + non-positive to null, but being
        // explicit here keeps the wire shape clean for ops who tail
        // the request bodies.
        var lifetimeHint = opts.TokenLifetimeSeconds is { } seconds && seconds > 0
            ? (int?)seconds
            : null;
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new MintProxyTokenRequest(
                ContainerId: containerId,
                SubjectId: subjectId,
                AllowedSlugs: allowedSlugs,
                LifetimeSeconds: lifetimeHint)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProxyTokenException(
                $"andy-models unreachable at {url}", ex);
        }
        using var _ = response;
        if (!response.IsSuccessStatusCode)
        {
            var preview = await SafeReadPreviewAsync(response, ct).ConfigureAwait(false);
            throw new ProxyTokenException(
                $"andy-models returned HTTP {(int)response.StatusCode} for token mint. Body preview: {preview}");
        }

        MintProxyTokenResponse? parsed;
        try
        {
            parsed = await response.Content
                .ReadFromJsonAsync<MintProxyTokenResponse>(ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new ProxyTokenException(
                "andy-models response for token mint was not valid JSON.", ex);
        }
        if (parsed is null
            || string.IsNullOrWhiteSpace(parsed.Jwt)
            || parsed.TokenId == Guid.Empty)
        {
            throw new ProxyTokenException(
                "andy-models response for token mint is missing tokenId or jwt.");
        }

        _logger.LogInformation(
            "Minted proxy token {TokenId} for container {ContainerId} (subject {SubjectId}, slugs {Slugs}, expiresAt {ExpiresAt})",
            parsed.TokenId, containerId, subjectId, string.Join(",", allowedSlugs), parsed.ExpiresAt);

        return new MintedProxyToken(parsed.TokenId, parsed.Jwt, parsed.ExpiresAt);
    }

    public async Task RevokeAsync(Guid tokenId, CancellationToken ct = default)
    {
        if (tokenId == Guid.Empty)
        {
            // Nothing to revoke. Caller should not pass empty but
            // guard anyway — we don't want to round-trip to andy-models
            // for a no-op.
            return;
        }

        var opts = _options.Value;
        if (string.IsNullOrWhiteSpace(opts.BaseUrl))
        {
            _logger.LogWarning(
                "AndyModels:BaseUrl is not configured; cannot revoke proxy token {TokenId}.",
                tokenId);
            return;
        }

        string bearer;
        try
        {
            bearer = await GetBearerAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to obtain service bearer for revoke of proxy token {TokenId}; token will expire naturally.",
                tokenId);
            return;
        }

        var http = _httpFactory.CreateClient(HttpClientName);
        var url = CombinePathStrict(opts.BaseUrl!, $"api/proxy/tokens/{tokenId}");
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var preview = await SafeReadPreviewAsync(response, ct).ConfigureAwait(false);
                _logger.LogWarning(
                    "andy-models returned HTTP {Status} for revoke of proxy token {TokenId}. Body preview: {Body}",
                    (int)response.StatusCode, tokenId, preview);
            }
            else
            {
                _logger.LogInformation("Revoked proxy token {TokenId}", tokenId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Transport error revoking proxy token {TokenId}; token will expire naturally.",
                tokenId);
        }
    }

    /// <summary>
    /// Bearer used on the mint path. Prefers an RFC 8693 OBO-exchanged
    /// token (sub=user, act=andy-containers-api) when the current HTTP
    /// request carries a user bearer; falls back to the pure-M2M token
    /// (sub=andy-containers-api) when no inbound user token is
    /// available (background callers).
    ///
    /// This is the consumer-side half of Epic IDP — paired with
    /// rivoli-ai/andy-models#68's OBO-aware
    /// <c>ProxyTokensController.CallerHasAsync</c>, it makes andy-rbac
    /// see the originating user as the subject of the
    /// <c>proxy:tokens:mint</c> check instead of <c>anonymous</c>.
    /// Closes rivoli-ai/andy-containers#305.
    /// </summary>
    private async Task<string> GetMintBearerAsync(CancellationToken ct)
    {
        var userJwt = TryExtractInboundUserJwt();
        if (!string.IsNullOrWhiteSpace(userJwt))
        {
            try
            {
                _logger.LogDebug(
                    "Using OBO bearer (audience={Audience}) for proxy-token mint",
                    AndyModelsApiAudience);
                return await _serviceTokens
                    .GetOnBehalfOfTokenAsync(userJwt, AndyModelsApiAudience, ct)
                    .ConfigureAwait(false);
            }
            catch (ServiceTokenException ex)
            {
                // OBO exchange is a user-ATTRIBUTION optimization (sub=user,
                // act=andy-containers-api) so andy-rbac sees the originating
                // user on the proxy:tokens:mint check. When it fails — most
                // commonly because the inbound user access token has expired,
                // or a TokenExchange policy/issuer mismatch — DON'T hard-fail
                // the whole container creation. Fall back to the pure-M2M
                // bearer (sub=andy-containers-api), which still mints a valid
                // proxy token; the only loss is user attribution on the mint.
                // Logged loudly + greppably so the degradation is visible.
                // (conductor#1973 — a launch must not break on an attribution
                //  optimization.)
                _logger.LogWarning(
                    ex,
                    "[PROXY-OBO-FALLBACK] OBO exchange for andy-models (audience={Audience}) failed; "
                        + "falling back to the M2M bearer for the proxy-token mint. The token will carry "
                        + "sub=andy-containers-api instead of the originating user. Likely cause: the inbound "
                        + "user token is expired or not exchange-eligible. Underlying: {Reason}",
                    AndyModelsApiAudience, ex.Message);
                try
                {
                    return await GetBearerAsync(ct).ConfigureAwait(false);
                }
                catch (ProxyTokenException fallbackEx)
                {
                    throw new ProxyTokenException(
                        "OBO exchange for andy-models failed and the M2M bearer fallback could not be obtained. " +
                        "Check token-exchange policy, ServiceAuth:* configuration, and the andy-models API scope.",
                        new AggregateException(ex, fallbackEx));
                }
            }
        }

        _logger.LogDebug(
            "No inbound user token; falling back to M2M bearer (audience={Audience}) for proxy-token mint",
            AndyModelsApiAudience);
        return await GetBearerAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pure M2M bearer (sub=andy-containers-api). Used on the revoke
    /// path (no user context in destroy hooks) and as fallback on the
    /// mint path when no inbound user token is available (background
    /// callers).
    /// </summary>
    private async Task<string> GetBearerAsync(CancellationToken ct)
    {
        try
        {
            // rivoli-ai/conductor#1055. andy-models's [Authorize]
            // attribute requires audience `urn:andy-models-api` —
            // distinct from the default `urn:andy-containers-api` the
            // M2M client identifies as. Request a cross-audience token
            // explicitly so andy-models accepts the bearer. The client
            // must have `scp:urn:andy-models-api` in its manifest
            // scopes (see andy-containers/config/registration.json).
            return await _serviceTokens
                .GetAccessTokenAsync(AndyModelsApiAudience, ct)
                .ConfigureAwait(false);
        }
        catch (ServiceTokenException ex)
        {
            throw new ProxyTokenException(
                "Could not obtain andy-containers service bearer to call andy-models. " +
                "Check ServiceAuth:* configuration and that the andy-containers-api " +
                $"OAuth client is permitted to request scope '{AndyModelsApiAudience}' " +
                "(see andy-containers/config/registration.json apiClient.scopes).", ex);
        }
    }

    /// <summary>
    /// Extracts the inbound user's bearer token from the current HTTP
    /// request, if any. Returns null when called outside an HTTP
    /// context (background workers, hosted services), or when the
    /// inbound request didn't carry a bearer header. Strips the
    /// "Bearer " prefix; returns the raw JWT.
    /// </summary>
    private string? TryExtractInboundUserJwt()
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx is null)
        {
            return null;
        }
        var header = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }
        // Tolerate "Bearer xyz" (canonical) and "bearer xyz" (some clients).
        const string prefix = "Bearer ";
        if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return header.Substring(prefix.Length).Trim();
        }
        return null;
    }

    /// <summary>
    /// rivoli-ai/conductor#1055. Audience that andy-models'
    /// <c>[Authorize]</c>-protected endpoints (including
    /// <c>POST /api/proxy/tokens</c>) require — see
    /// <c>andy-models/src/Andy.Models.Api/Program.cs:31</c>.
    /// </summary>
    private const string AndyModelsApiAudience = "urn:andy-models-api";

    /// <summary>
    /// Combine <paramref name="baseUrl"/> + <paramref name="path"/>
    /// without losing a path prefix on the base (e.g. embedded mode
    /// where the base is <c>http://localhost:9100/models</c>). The
    /// out-of-the-box <see cref="Uri"/> constructor with a relative
    /// path drops the base path; we use an explicit join instead.
    /// </summary>
    private static string CombinePathStrict(string baseUrl, string relativePath)
    {
        var b = baseUrl.TrimEnd('/');
        var p = relativePath.TrimStart('/');
        return $"{b}/{p}";
    }

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

    // Wire shapes — names match andy-models'
    // Andy.Models.Api.Controllers.ProxyTokensController. Kept private
    // and JSON-only; we do not depend on a shared package.

    private sealed record MintProxyTokenRequest(
        [property: JsonPropertyName("containerId")] string ContainerId,
        [property: JsonPropertyName("subjectId")] string SubjectId,
        [property: JsonPropertyName("allowedSlugs")] IReadOnlyList<string> AllowedSlugs,
        [property: JsonPropertyName("lifetimeSeconds")] int? LifetimeSeconds);

    private sealed record MintProxyTokenResponse(
        [property: JsonPropertyName("tokenId")] Guid TokenId,
        [property: JsonPropertyName("jwt")] string Jwt,
        [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);
}

/// <summary>
/// Bound from <c>AndyModels:</c> in configuration. Conductor injects
/// these via <c>ContainersServiceConfig.environmentOverrides</c>.
/// </summary>
public sealed class AndyModelsOptions
{
    public const string SectionName = "AndyModels";

    /// <summary>
    /// rivoli-ai/conductor#943. 7 days. Long-lived containers normally
    /// outlive the OAuth default of 1 hour, so the token mint requests
    /// a week up front; andy-models clamps to its configured maximum
    /// if the deployment policy is shorter.
    /// </summary>
    public const int DefaultTokenLifetimeSeconds = 7 * 24 * 60 * 60;

    /// <summary>
    /// Base URL of the andy-models API — e.g.
    /// <c>http://localhost:9100/models</c> in embedded mode or
    /// <c>https://andy-models.example.com</c> in server deployments.
    /// May include a path prefix; <see cref="AndyModelsProxyTokenService"/>
    /// joins paths strictly (no <see cref="Uri"/> base-relative reset).
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Token lifetime hint sent on the mint request. Defaults to
    /// <see cref="DefaultTokenLifetimeSeconds"/> (7 days per
    /// rivoli-ai/conductor#943 AC). Set to a smaller number for
    /// short-lived workloads — andy-models clamps to its configured
    /// maximum either way. Set to <c>0</c> or a negative number to
    /// omit the hint and accept andy-models' own default.
    /// </summary>
    public int? TokenLifetimeSeconds { get; set; } = DefaultTokenLifetimeSeconds;
}
