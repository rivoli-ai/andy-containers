using System.Net;
using System.Text;
using Andy.Containers.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

/// <summary>
/// rivoli-ai/conductor#2242. The andy-settings client resolves the source-control
/// GitHub PAT (sourceControl.github.pat) used as a fallback container credential.
/// Covers the failure-mode contract against a stubbed HttpMessageHandler:
/// value→token, 404→null (no PAT set, NOT an error), 5xx→throw (a settings
/// outage must NOT masquerade as "no PAT"), unparseable→throw, empty→null.
/// </summary>
public class AndySettingsHttpClientTests
{
    [Fact]
    public async Task SecretPresent_ReturnsToken()
    {
        var client = BuildClient(_ => JsonResponse(
            """{"definitionKey":"sourceControl.github.pat","value":"ghp_realtoken"}"""));

        var pat = await client.GetGitHubPatAsync();

        pat.Should().Be("ghp_realtoken");
    }

    [Fact]
    public async Task RequestsMachineScopedSecretByConfiguredKey()
    {
        HttpRequestMessage? captured = null;
        var client = BuildClient(req =>
        {
            captured = req;
            return JsonResponse("""{"definitionKey":"sourceControl.github.pat","value":"x"}""");
        });

        await client.GetGitHubPatAsync();

        captured.Should().NotBeNull();
        captured!.RequestUri!.ToString().Should().Contain("api/secrets/sourceControl.github.pat");
        captured.RequestUri.ToString().Should().Contain("scopeType=Machine");
    }

    [Fact]
    public async Task NotFound_ReturnsNull_NotError()
    {
        var client = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var pat = await client.GetGitHubPatAsync();

        pat.Should().BeNull("a 404 means no PAT is configured — a clean 'no credential' path");
    }

    [Fact]
    public async Task EmptyValue_ReturnsNull()
    {
        var client = BuildClient(_ => JsonResponse(
            """{"definitionKey":"sourceControl.github.pat","value":""}"""));

        var pat = await client.GetGitHubPatAsync();

        pat.Should().BeNull();
    }

    [Fact]
    public async Task ServerError_Throws_NotSilentlyNull()
    {
        var client = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var act = () => client.GetGitHubPatAsync();

        await act.Should().ThrowAsync<AndySettingsResolutionException>(
            "a settings outage must be distinguishable from 'no PAT set'");
    }

    [Fact]
    public async Task UnparseableBody_Throws()
    {
        var client = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json", Encoding.UTF8, "application/json"),
        });

        var act = () => client.GetGitHubPatAsync();

        await act.Should().ThrowAsync<AndySettingsResolutionException>();
    }

    [Fact]
    public async Task TransportError_Throws()
    {
        var handler = new ThrowingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://andy-settings.test/") };
        var client = new AndySettingsHttpClient(
            new SingleClientFactory(http),
            Options.Create(new AndySettingsOptions()),
            NullLogger<AndySettingsHttpClient>.Instance);

        var act = () => client.GetGitHubPatAsync();

        await act.Should().ThrowAsync<AndySettingsResolutionException>();
    }

    [Fact]
    public async Task NullResolver_AlwaysReturnsNull()
    {
        var resolver = new NullSourceControlSecretResolver();
        (await resolver.GetGitHubPatAsync()).Should().BeNull();
    }

    // ---- harness ------------------------------------------------------------

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static AndySettingsHttpClient BuildClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://andy-settings.test/") };
        return new AndySettingsHttpClient(
            new SingleClientFactory(http),
            Options.Create(new AndySettingsOptions()),
            NullLogger<AndySettingsHttpClient>.Instance);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingleClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }
}
