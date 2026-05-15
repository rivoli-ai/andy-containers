using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Andy.Containers.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

/// <summary>
/// rivoli-ai/conductor#943 (M1.5.1). Covers mint + revoke request
/// shape, the empty-slugs short-circuit, error mapping, and bearer
/// propagation from <see cref="IServiceTokenService"/>.
/// </summary>
public class AndyModelsProxyTokenServiceTests
{
    // -----------------------------------------------------------------
    // Mint — happy path
    // -----------------------------------------------------------------

    [Fact]
    public async Task Mint_HappyPath_ReturnsTokenIdAndJwt()
    {
        var tokenId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var (service, handler, _) = MakeService(opts =>
        {
            opts.BaseUrl = "http://andy-models.test";
        });
        handler.SetSuccessJsonResponse(
            $"{{\"tokenId\":\"{tokenId}\",\"jwt\":\"eyJhbGc.test.jwt\",\"expiresAt\":\"2026-12-01T00:00:00Z\"}}");

        var minted = await service.MintForContainerAsync(
            containerId: "ctr-abc",
            subjectId: "user-42",
            allowedSlugs: new[] { "anthropic/claude-sonnet-4-6" });

        minted.Should().NotBeNull();
        minted!.TokenId.Should().Be(tokenId);
        minted.Jwt.Should().Be("eyJhbGc.test.jwt");
        minted.ExpiresAt.Should().Be(DateTimeOffset.Parse("2026-12-01T00:00:00Z"));
    }

    [Fact]
    public async Task Mint_PostsToCorrectUrlWithBearerAndJsonBody()
    {
        var (service, handler, _) = MakeService(opts => opts.BaseUrl = "http://andy-models.test/models");
        handler.SetSuccessJsonResponse(
            "{\"tokenId\":\"22222222-2222-2222-2222-222222222222\",\"jwt\":\"j.w.t\",\"expiresAt\":\"2026-12-01T00:00:00Z\"}");

        _ = await service.MintForContainerAsync("ctr-xyz", "user-1", new[] { "openai/gpt-4o" });

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Be(
            new Uri("http://andy-models.test/models/api/proxy/tokens"),
            "base path prefix must be preserved when joining api/proxy/tokens.");
        handler.LastRequest!.Headers.Authorization.Should().Be(
            new AuthenticationHeaderValue("Bearer", "m2m-bearer-stub"));
        handler.LastBody.Should().Contain("\"containerId\":\"ctr-xyz\"");
        handler.LastBody.Should().Contain("\"subjectId\":\"user-1\"");
        handler.LastBody.Should().Contain("\"allowedSlugs\":[\"openai/gpt-4o\"]");
    }

    [Fact]
    public async Task Mint_BaseUrlWithoutPath_StillJoinsCorrectly()
    {
        var (service, handler, _) = MakeService(opts => opts.BaseUrl = "http://andy-models.test");
        handler.SetSuccessJsonResponse(
            "{\"tokenId\":\"33333333-3333-3333-3333-333333333333\",\"jwt\":\"j.w.t\",\"expiresAt\":\"2026-12-01T00:00:00Z\"}");

        _ = await service.MintForContainerAsync("ctr", "user", new[] { "anthropic/claude-sonnet-4-6" });

        handler.LastRequestUri.Should().Be(new Uri("http://andy-models.test/api/proxy/tokens"));
    }

    [Fact]
    public async Task Mint_RequestsCrossAudienceBearerForAndyModels()
    {
        // rivoli-ai/conductor#1055. andy-models's [Authorize] requires
        // audience `urn:andy-models-api` — distinct from the M2M
        // client's own `urn:andy-containers-api`. Without the
        // cross-audience request, the bearer is rejected with 401
        // before any controller logic runs.
        var bearer = new StubTokenService("cross-aud-jwt");
        var (service, handler, _) = MakeService(
            opts => opts.BaseUrl = "http://andy-models.test",
            bearerProvider: bearer);
        handler.SetSuccessJsonResponse(
            "{\"tokenId\":\"44444444-4444-4444-4444-444444444444\",\"jwt\":\"j.w.t\",\"expiresAt\":\"2026-12-01T00:00:00Z\"}");

        _ = await service.MintForContainerAsync("ctr", "user", new[] { "anthropic/claude-sonnet-4-6" });

        bearer.LastRequestedAudience.Should().Be("urn:andy-models-api",
            "the consumer must ask IServiceTokenService for a token whose `aud` claim andy-models will accept.");
    }

    // -----------------------------------------------------------------
    // Lifetime hint (#943 AC: 7-day default)
    // -----------------------------------------------------------------

    [Fact]
    public void AndyModelsOptions_DefaultLifetimeIsSevenDays()
    {
        // rivoli-ai/conductor#943 spec: "Token lifetime defaults to 7 days."
        new AndyModelsOptions().TokenLifetimeSeconds
            .Should().Be(7 * 24 * 60 * 60,
                "the default token lifetime is contractual — bumping it has billing + revocation-window implications.");
    }

    [Fact]
    public async Task Mint_SendsDefaultSevenDayLifetimeHintWhenConfigUntouched()
    {
        var (service, handler, _) = MakeService(opts =>
        {
            opts.BaseUrl = "http://andy-models.test";
            // Do NOT override TokenLifetimeSeconds — exercise the default.
        });
        handler.SetSuccessJsonResponse(
            "{\"tokenId\":\"99999999-9999-9999-9999-999999999999\",\"jwt\":\"j.w.t\",\"expiresAt\":\"2026-12-01T00:00:00Z\"}");

        _ = await service.MintForContainerAsync("ctr", "user", new[] { "anthropic/claude-sonnet-4-6" });

        handler.LastBody.Should().Contain("\"lifetimeSeconds\":604800");
    }

    [Fact]
    public async Task Mint_ExplicitLifetimeHintIsRespected()
    {
        var (service, handler, _) = MakeService(opts =>
        {
            opts.BaseUrl = "http://andy-models.test";
            opts.TokenLifetimeSeconds = 3600;
        });
        handler.SetSuccessJsonResponse(
            "{\"tokenId\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"jwt\":\"j.w.t\",\"expiresAt\":\"2026-12-01T00:00:00Z\"}");

        _ = await service.MintForContainerAsync("ctr", "user", new[] { "anthropic/claude-sonnet-4-6" });

        handler.LastBody.Should().Contain("\"lifetimeSeconds\":3600");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Mint_ZeroOrNegativeLifetime_OmitsHint(int configured)
    {
        var (service, handler, _) = MakeService(opts =>
        {
            opts.BaseUrl = "http://andy-models.test";
            opts.TokenLifetimeSeconds = configured;
        });
        handler.SetSuccessJsonResponse(
            "{\"tokenId\":\"11111111-2222-3333-4444-555555555555\",\"jwt\":\"j.w.t\",\"expiresAt\":\"2026-12-01T00:00:00Z\"}");

        _ = await service.MintForContainerAsync("ctr", "user", new[] { "anthropic/claude-sonnet-4-6" });

        handler.LastBody.Should().Contain("\"lifetimeSeconds\":null",
            "0 / negative configured values are 'omit the hint' — let andy-models apply its own default.");
    }

    // -----------------------------------------------------------------
    // Mint — short-circuits
    // -----------------------------------------------------------------

    [Fact]
    public async Task Mint_EmptyAllowedSlugs_ReturnsNullWithoutCallingAndyModels()
    {
        var (service, handler, _) = MakeService(opts => opts.BaseUrl = "http://andy-models.test");

        var minted = await service.MintForContainerAsync("ctr", "user", Array.Empty<string>());

        minted.Should().BeNull();
        handler.RequestCount.Should().Be(0,
            "empty slugs is the documented 'this container does its own auth' signal — no round trip needed.");
    }

    // -----------------------------------------------------------------
    // Mint — failure modes (all throw ProxyTokenException so caller
    // can fail container creation fast)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Mint_MissingBaseUrl_ThrowsProxyTokenException()
    {
        var (service, _, _) = MakeService(opts => opts.BaseUrl = null);

        await FluentActions.Awaiting(() =>
                service.MintForContainerAsync("ctr", "user", new[] { "anthropic/claude-sonnet-4-6" }))
            .Should().ThrowAsync<ProxyTokenException>()
            .WithMessage("*AndyModels:BaseUrl is not configured*");
    }

    [Fact]
    public async Task Mint_TransportError_ThrowsProxyTokenException()
    {
        var (service, handler, _) = MakeService(opts => opts.BaseUrl = "http://andy-models.test");
        handler.SetSendException(new HttpRequestException("connection refused"));

        await FluentActions.Awaiting(() =>
                service.MintForContainerAsync("ctr", "user", new[] { "anthropic/claude-sonnet-4-6" }))
            .Should().ThrowAsync<ProxyTokenException>()
            .WithMessage("*andy-models unreachable*");
    }

    [Fact]
    public async Task Mint_AndyModels500_ThrowsProxyTokenExceptionCarryingStatus()
    {
        var (service, handler, _) = MakeService(opts => opts.BaseUrl = "http://andy-models.test");
        handler.SetResponse(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("upstream broke", Encoding.UTF8, "text/plain"),
        });

        await FluentActions.Awaiting(() =>
                service.MintForContainerAsync("ctr", "user", new[] { "anthropic/claude-sonnet-4-6" }))
            .Should().ThrowAsync<ProxyTokenException>()
            .WithMessage("*HTTP 500*");
    }

    [Fact]
    public async Task Mint_AndyModelsResponseMissingFields_ThrowsProxyTokenException()
    {
        var (service, handler, _) = MakeService(opts => opts.BaseUrl = "http://andy-models.test");
        handler.SetSuccessJsonResponse("{\"tokenId\":\"44444444-4444-4444-4444-444444444444\"}");

        await FluentActions.Awaiting(() =>
                service.MintForContainerAsync("ctr", "user", new[] { "anthropic/claude-sonnet-4-6" }))
            .Should().ThrowAsync<ProxyTokenException>()
            .WithMessage("*missing tokenId or jwt*");
    }

    [Fact]
    public async Task Mint_ServiceTokenFailure_BubblesAsProxyTokenException()
    {
        var (service, _, _) = MakeService(
            opts => opts.BaseUrl = "http://andy-models.test",
            bearerProvider: new ThrowingTokenService(new ServiceTokenException("auth down")));

        await FluentActions.Awaiting(() =>
                service.MintForContainerAsync("ctr", "user", new[] { "anthropic/claude-sonnet-4-6" }))
            .Should().ThrowAsync<ProxyTokenException>()
            .WithMessage("*service bearer*");
    }

    // -----------------------------------------------------------------
    // Revoke
    // -----------------------------------------------------------------

    [Fact]
    public async Task Revoke_HappyPath_PostsDeleteWithTokenIdAndBearer()
    {
        var (service, handler, _) = MakeService(opts => opts.BaseUrl = "http://andy-models.test");
        handler.SetResponse(new HttpResponseMessage(HttpStatusCode.NoContent));
        var tokenId = Guid.Parse("55555555-5555-5555-5555-555555555555");

        await service.RevokeAsync(tokenId);

        handler.LastMethod.Should().Be(HttpMethod.Delete);
        handler.LastRequestUri.Should().Be(new Uri($"http://andy-models.test/api/proxy/tokens/{tokenId}"));
        handler.LastRequest!.Headers.Authorization.Should().Be(
            new AuthenticationHeaderValue("Bearer", "m2m-bearer-stub"));
    }

    [Fact]
    public async Task Revoke_EmptyGuid_IsNoOp()
    {
        var (service, handler, _) = MakeService(opts => opts.BaseUrl = "http://andy-models.test");

        await service.RevokeAsync(Guid.Empty);

        handler.RequestCount.Should().Be(0,
            "Guid.Empty is a sentinel; round-tripping to andy-models for nothing is wasteful.");
    }

    [Fact]
    public async Task Revoke_AndyModels4xx_SwallowsError()
    {
        var (service, handler, _) = MakeService(opts => opts.BaseUrl = "http://andy-models.test");
        handler.SetResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        var tokenId = Guid.Parse("66666666-6666-6666-6666-666666666666");

        // Should not throw — destroy must not wedge on revoke failures.
        await service.RevokeAsync(tokenId);
    }

    [Fact]
    public async Task Revoke_TransportError_SwallowsError()
    {
        var (service, handler, _) = MakeService(opts => opts.BaseUrl = "http://andy-models.test");
        handler.SetSendException(new HttpRequestException("connection refused"));
        var tokenId = Guid.Parse("77777777-7777-7777-7777-777777777777");

        // Should not throw — token will expire naturally at its exp claim.
        await service.RevokeAsync(tokenId);
    }

    [Fact]
    public async Task Revoke_MissingBaseUrl_IsNoOp()
    {
        var (service, handler, _) = MakeService(opts => opts.BaseUrl = null);
        var tokenId = Guid.Parse("88888888-8888-8888-8888-888888888888");

        await service.RevokeAsync(tokenId);

        handler.RequestCount.Should().Be(0);
    }

    // -----------------------------------------------------------------
    // Epic IDP (rivoli-ai/conductor#1246) — OBO bearer on mint path
    // Closes rivoli-ai/andy-containers#305.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Mint_WithInboundUserBearer_UsesObOBearer()
    {
        // When the current HTTP request carries `Authorization: Bearer <user-jwt>`,
        // the consumer exchanges the user token via OBO instead of using
        // the pure M2M token. andy-models then sees `sub=<user>` /
        // `act=<andy-containers-api>` and gates on the user's permission.
        var bearer = new StubTokenService("obo-exchanged-jwt");
        var (service, handler, _) = MakeService(
            opts => opts.BaseUrl = "http://andy-models.test",
            bearerProvider: bearer,
            inboundUserJwt: "user.jwt.signature");
        handler.SetSuccessJsonResponse(
            "{\"tokenId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"jwt\":\"j.w.t\",\"expiresAt\":\"2026-12-01T00:00:00Z\"}");

        _ = await service.MintForContainerAsync("ctr", "user", new[] { "anthropic/claude-sonnet-4-6" });

        bearer.LastSubjectToken.Should().Be("user.jwt.signature",
            "the user's inbound token must be presented as subject_token to the OBO exchange.");
        bearer.LastRequestedAudience.Should().Be("urn:andy-models-api");
        handler.LastRequest!.Headers.Authorization.Should().Be(
            new AuthenticationHeaderValue("Bearer", "obo-exchanged-jwt"),
            "the bearer on the outbound call must be the OBO-exchanged token, not the user's raw JWT.");
    }

    [Fact]
    public async Task Mint_WithoutInboundHttpContext_FallsBackToM2M()
    {
        // Background callers (workers, hosted services) have no
        // inbound HTTP context. They keep the existing M2M behaviour.
        var bearer = new StubTokenService("m2m-bearer");
        var (service, handler, _) = MakeService(
            opts => opts.BaseUrl = "http://andy-models.test",
            bearerProvider: bearer);
        handler.SetSuccessJsonResponse(
            "{\"tokenId\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\",\"jwt\":\"j.w.t\",\"expiresAt\":\"2026-12-01T00:00:00Z\"}");

        _ = await service.MintForContainerAsync("ctr", "user", new[] { "anthropic/claude-sonnet-4-6" });

        bearer.LastSubjectToken.Should().BeNull("no inbound user → no OBO exchange.");
        bearer.LastRequestedAudience.Should().Be("urn:andy-models-api");
        handler.LastRequest!.Headers.Authorization.Should().Be(
            new AuthenticationHeaderValue("Bearer", "m2m-bearer"));
    }

    [Fact]
    public async Task Mint_ObOExchangeFailure_WrappedAsProxyTokenException()
    {
        // OBO exchange failures should surface with a clear message
        // pointing at the policy / config — not crash the request with
        // a raw ServiceTokenException.
        var bearer = new ThrowingTokenService(new ServiceTokenException("policy denied"));
        var (service, _, _) = MakeService(
            opts => opts.BaseUrl = "http://andy-models.test",
            bearerProvider: bearer,
            inboundUserJwt: "user.jwt.signature");

        var act = async () => await service.MintForContainerAsync(
            "ctr", "user", new[] { "anthropic/claude-sonnet-4-6" });

        var ex = await act.Should().ThrowAsync<ProxyTokenException>();
        ex.Which.Message.Should().Contain("OBO");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static (AndyModelsProxyTokenService service, StubHttpHandler handler, IServiceTokenService bearer)
        MakeService(
            Action<AndyModelsOptions> configure,
            IServiceTokenService? bearerProvider = null,
            string? inboundUserJwt = null)
    {
        var options = new AndyModelsOptions();
        configure(options);
        var handler = new StubHttpHandler();
        var factory = new SingleClientHttpClientFactory(handler);
        var bearer = bearerProvider ?? new StubTokenService("m2m-bearer-stub");

        var httpContextAccessor = new Microsoft.AspNetCore.Http.HttpContextAccessor();
        if (inboundUserJwt is not null)
        {
            var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            ctx.Request.Headers.Authorization = $"Bearer {inboundUserJwt}";
            httpContextAccessor.HttpContext = ctx;
        }

        var service = new AndyModelsProxyTokenService(
            factory,
            bearer,
            httpContextAccessor,
            Options.Create(options),
            NullLogger<AndyModelsProxyTokenService>.Instance);
        return (service, handler, bearer);
    }

    private sealed class StubTokenService : IServiceTokenService
    {
        private readonly string _token;
        public string? LastRequestedAudience { get; private set; }
        public string? LastSubjectToken { get; private set; }
        public StubTokenService(string token) { _token = token; }
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
        {
            LastRequestedAudience = null;
            return Task.FromResult(_token);
        }
        public Task<string> GetAccessTokenAsync(string audience, CancellationToken ct = default)
        {
            LastRequestedAudience = audience;
            return Task.FromResult(_token);
        }
        public Task<string> GetOnBehalfOfTokenAsync(string subjectToken, string audience, CancellationToken ct = default)
        {
            LastRequestedAudience = audience;
            LastSubjectToken = subjectToken;
            return Task.FromResult(_token);
        }
    }

    private sealed class ThrowingTokenService : IServiceTokenService
    {
        private readonly Exception _ex;
        public ThrowingTokenService(Exception ex) { _ex = ex; }
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
            => Task.FromException<string>(_ex);
        public Task<string> GetAccessTokenAsync(string audience, CancellationToken ct = default)
            => Task.FromException<string>(_ex);
        public Task<string> GetOnBehalfOfTokenAsync(string subjectToken, string audience, CancellationToken ct = default)
            => Task.FromException<string>(_ex);
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private HttpResponseMessage _response = new(HttpStatusCode.OK)
        {
            Content = new StringContent("{}"),
        };
        private Exception? _sendException;

        public int RequestCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        public void SetSuccessJsonResponse(string json)
        {
            _sendException = null;
            _response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        public void SetResponse(HttpResponseMessage response)
        {
            _sendException = null;
            _response = response;
        }

        public void SetSendException(Exception ex) { _sendException = ex; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;
            LastMethod = request.Method;
            LastRequestUri = request.RequestUri;
            LastBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (_sendException is not null) throw _sendException;
            return _response;
        }
    }

    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleClientHttpClientFactory(HttpMessageHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
