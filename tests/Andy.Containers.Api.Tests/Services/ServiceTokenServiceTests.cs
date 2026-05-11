using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Andy.Containers.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

/// <summary>
/// #944 / M1.5.1. Coverage of the M2M token consumer's request shape,
/// caching, error mapping, and refresh-skew behaviour. Exercises the
/// full <c>HttpClient → /connect/token → cache</c> chain via a
/// stubbed <see cref="HttpMessageHandler"/>.
/// </summary>
public class ServiceTokenServiceTests
{
    // -----------------------------------------------------------------
    // Happy path
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetAccessToken_HappyPath_ReturnsAccessTokenFromResponse()
    {
        var (service, handler) = MakeService(opts =>
        {
            opts.TokenEndpoint = "http://auth.test/connect/token";
            opts.ClientId = "andy-containers-api";
            opts.ClientSecret = "s3cret";
            opts.Audience = "urn:andy-containers-api";
        });
        handler.SetSuccessJsonResponse(@"{""access_token"":""abc.def"",""token_type"":""Bearer"",""expires_in"":3600}");

        var token = await service.GetAccessTokenAsync();

        token.Should().Be("abc.def");
    }

    [Fact]
    public async Task GetAccessToken_PostsClientCredentialsFormWithExpectedFields()
    {
        var (service, handler) = MakeService(opts =>
        {
            opts.TokenEndpoint = "http://auth.test/connect/token";
            opts.ClientId = "andy-containers-api";
            opts.ClientSecret = "s3cret";
            opts.Audience = "urn:andy-containers-api";
        });
        handler.SetSuccessJsonResponse(@"{""access_token"":""tok"",""token_type"":""Bearer"",""expires_in"":600}");

        _ = await service.GetAccessTokenAsync();

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastRequestUri.Should().Be(new Uri("http://auth.test/connect/token"));
        var body = handler.LastBody;
        body.Should().Contain("grant_type=client_credentials");
        body.Should().Contain("client_id=andy-containers-api");
        body.Should().Contain("client_secret=s3cret");
        body.Should().Contain("scope=urn%3Aandy-containers-api",
            "request scope is the unprefixed audience; the `scp:` prefix is for client-side permissions only. " +
            "Sending `scp:` in the request body returns `invalid_scope` (OpenIddict ID2052).");
    }

    // -----------------------------------------------------------------
    // Caching
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetAccessToken_ConsecutiveCallsHitCache()
    {
        var (service, handler) = MakeService(opts => opts.TokenEndpoint = "http://auth.test/connect/token");
        handler.SetSuccessJsonResponse(@"{""access_token"":""tok-1"",""token_type"":""Bearer"",""expires_in"":3600}");

        var first = await service.GetAccessTokenAsync();
        var second = await service.GetAccessTokenAsync();
        var third = await service.GetAccessTokenAsync();

        first.Should().Be("tok-1");
        second.Should().Be("tok-1");
        third.Should().Be("tok-1");
        handler.RequestCount.Should().Be(1,
            "the cache should swallow N concurrent fetches into one mint call.");
    }

    [Fact]
    public async Task GetAccessToken_RefreshesWhenWithinSkewWindow()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-08T12:00:00Z"));
        var (service, handler) = MakeService(
            opts => opts.TokenEndpoint = "http://auth.test/connect/token",
            timeProvider: clock);
        handler.SetSuccessJsonResponse(@"{""access_token"":""initial"",""token_type"":""Bearer"",""expires_in"":60}");

        var first = await service.GetAccessTokenAsync();
        first.Should().Be("initial");
        handler.RequestCount.Should().Be(1);

        // Advance clock to 31s before expiry — exactly at the skew
        // boundary. Should still serve from cache (skew is *strictly* less).
        clock.Advance(TimeSpan.FromSeconds(29));
        handler.SetSuccessJsonResponse(@"{""access_token"":""refreshed"",""token_type"":""Bearer"",""expires_in"":60}");
        var second = await service.GetAccessTokenAsync();
        second.Should().Be("initial", "cache still valid at >30s before expiry");
        handler.RequestCount.Should().Be(1);

        // Advance into the skew window — within 30s of expiry.
        clock.Advance(TimeSpan.FromSeconds(2));
        var third = await service.GetAccessTokenAsync();
        third.Should().Be("refreshed");
        handler.RequestCount.Should().Be(2);
    }

    // -----------------------------------------------------------------
    // Error paths
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetAccessToken_NonSuccessStatus_ThrowsServiceTokenException()
    {
        var (service, handler) = MakeService(opts => opts.TokenEndpoint = "http://auth.test/connect/token");
        handler.SetResponse(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(@"{""error"":""invalid_client""}", Encoding.UTF8, "application/json"),
        });

        Func<Task> call = () => service.GetAccessTokenAsync();
        var ex = await call.Should().ThrowAsync<ServiceTokenException>();
        ex.Which.Message.Should().Contain("HTTP 401");
        ex.Which.Message.Should().Contain("invalid_client",
            "the body preview helps triage without leaking the secret.");
    }

    [Fact]
    public async Task GetAccessToken_TokenEndpointUnreachable_ThrowsServiceTokenException()
    {
        var (service, handler) = MakeService(opts => opts.TokenEndpoint = "http://auth.test/connect/token");
        handler.SetSendException(new HttpRequestException("connection refused"));

        Func<Task> call = () => service.GetAccessTokenAsync();
        var ex = await call.Should().ThrowAsync<ServiceTokenException>();
        ex.Which.Message.Should().Contain("Token endpoint unreachable");
        ex.Which.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task GetAccessToken_MissingTokenEndpoint_ThrowsServiceTokenException()
    {
        var (service, _) = MakeService(opts => opts.TokenEndpoint = "");

        Func<Task> call = () => service.GetAccessTokenAsync();
        var ex = await call.Should().ThrowAsync<ServiceTokenException>();
        ex.Which.Message.Should().Contain("TokenEndpoint is not configured");
    }

    [Fact]
    public async Task GetAccessToken_ResponseLacksAccessToken_ThrowsServiceTokenException()
    {
        var (service, handler) = MakeService(opts => opts.TokenEndpoint = "http://auth.test/connect/token");
        // Valid JSON, missing the `access_token` field — andy-auth
        // shouldn't ever produce this, but treat it as a hard fail
        // rather than caching an empty string and confusing later
        // consumers with a bearer-prefixed empty header.
        handler.SetSuccessJsonResponse(@"{""token_type"":""Bearer"",""expires_in"":60}");

        Func<Task> call = () => service.GetAccessTokenAsync();
        var ex = await call.Should().ThrowAsync<ServiceTokenException>();
        ex.Which.Message.Should().Contain("access_token");
    }

    // -----------------------------------------------------------------
    // Per-audience tokens (rivoli-ai/conductor#1055)
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetAccessToken_WithExplicitAudience_SendsThatAudienceInScope()
    {
        var (service, handler) = MakeService(opts =>
        {
            opts.TokenEndpoint = "http://auth.test/connect/token";
            opts.ClientId = "andy-containers-api";
            opts.ClientSecret = "s3cret";
            opts.Audience = "urn:andy-containers-api";
        });
        handler.SetSuccessJsonResponse(@"{""access_token"":""cross-aud-tok"",""token_type"":""Bearer"",""expires_in"":3600}");

        var token = await service.GetAccessTokenAsync("urn:andy-models-api");

        token.Should().Be("cross-aud-tok");
        handler.LastBody.Should().Contain("scope=urn%3Aandy-models-api",
            "explicit audience must take precedence over ServiceAuth:Audience for inter-service calls.");
    }

    [Fact]
    public async Task GetAccessToken_PerAudienceCache_DoesNotCrossPollinate()
    {
        var (service, handler) = MakeService(opts => opts.TokenEndpoint = "http://auth.test/connect/token");
        // The stub returns the same body for every call — we're
        // asserting the call COUNT and request shape, not distinct
        // bodies, because we want to know whether the service mints
        // twice (correct: once per audience) vs. once (wrong: would
        // mean it returned the cached default-audience token when
        // asked for a different audience).
        handler.SetSuccessJsonResponse(@"{""access_token"":""tok"",""token_type"":""Bearer"",""expires_in"":3600}");

        _ = await service.GetAccessTokenAsync("urn:andy-containers-api");
        _ = await service.GetAccessTokenAsync("urn:andy-models-api");
        _ = await service.GetAccessTokenAsync("urn:andy-containers-api");
        _ = await service.GetAccessTokenAsync("urn:andy-models-api");

        handler.RequestCount.Should().Be(2,
            "each distinct audience mints exactly once; repeat calls per audience hit the per-slot cache.");
    }

    [Fact]
    public async Task GetAccessToken_ParameterlessOverload_UsesConfiguredDefaultAudience()
    {
        var (service, handler) = MakeService(opts =>
        {
            opts.TokenEndpoint = "http://auth.test/connect/token";
            opts.Audience = "urn:andy-containers-api";
        });
        handler.SetSuccessJsonResponse(@"{""access_token"":""tok"",""token_type"":""Bearer"",""expires_in"":3600}");

        _ = await service.GetAccessTokenAsync();

        handler.LastBody.Should().Contain("scope=urn%3Aandy-containers-api",
            "the parameterless overload preserves pre-#1055 behaviour for callers that don't care about audience.");
    }

    [Fact]
    public async Task GetAccessToken_EmptyAudienceArg_ThrowsServiceTokenException()
    {
        var (service, _) = MakeService();

        Func<Task> call = () => service.GetAccessTokenAsync("", CancellationToken.None);

        await call.Should().ThrowAsync<ServiceTokenException>()
            .WithMessage("*Audience is required*");
    }

    [Fact]
    public async Task GetAccessToken_DefaultOverload_WithUnconfiguredAudience_ThrowsServiceTokenException()
    {
        var (service, _) = MakeService(opts => opts.Audience = string.Empty);

        Func<Task> call = () => service.GetAccessTokenAsync();

        await call.Should().ThrowAsync<ServiceTokenException>()
            .WithMessage("*ServiceAuth:Audience is not configured*");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static (ServiceTokenService service, StubHttpHandler handler) MakeService(
        Action<ServiceAuthOptions>? configure = null,
        TimeProvider? timeProvider = null)
    {
        var options = new ServiceAuthOptions
        {
            TokenEndpoint = "http://auth.test/connect/token",
            ClientId = "andy-containers-api",
            ClientSecret = "test-secret",
            Audience = "urn:andy-containers-api",
        };
        configure?.Invoke(options);

        var handler = new StubHttpHandler();
        var factory = new SingleClientHttpClientFactory(handler);
        var service = new ServiceTokenService(
            factory,
            Options.Create(options),
            NullLogger<ServiceTokenService>.Instance,
            timeProvider);
        return (service, handler);
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        // Store the JSON template separately from any built
        // HttpResponseMessage so multi-call tests (e.g. per-audience
        // cache) get fresh content per call. HttpClient disposes the
        // response content after each request, so reusing a single
        // StringContent across calls throws ObjectDisposedException
        // on the second call.
        private string? _jsonBody;
        private HttpStatusCode _statusCode = HttpStatusCode.OK;
        private HttpResponseMessage? _fixedResponse;
        private Exception? _sendException;

        public int RequestCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        public void SetSuccessJsonResponse(string json)
        {
            _sendException = null;
            _fixedResponse = null;
            _jsonBody = json;
            _statusCode = HttpStatusCode.OK;
        }

        public void SetResponse(HttpResponseMessage response)
        {
            _sendException = null;
            _jsonBody = null;
            _fixedResponse = response;
        }

        public void SetSendException(Exception ex)
        {
            _sendException = ex;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;
            LastMethod = request.Method;
            LastRequestUri = request.RequestUri;
            // Buffer the body NOW into a string the test can inspect
            // post-call — the `using` in the service disposes
            // `FormUrlEncodedContent` before the assertion runs, so
            // the test must read it before that happens.
            LastBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (_sendException is not null) throw _sendException;
            if (_fixedResponse is not null) return _fixedResponse;
            // Fresh content per call so consecutive SendAsyncs work.
            return new HttpResponseMessage(_statusCode)
            {
                Content = _jsonBody is null
                    ? new StringContent("{}")
                    : new StringContent(_jsonBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleClientHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset now) { _now = now; }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
