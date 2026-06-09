using System.Net;
using System.Text;
using Andy.Containers.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Andy.Containers.Api.Tests.Configurator;

// AX.1 (rivoli-ai/conductor#2088). The real andy-agents resolver replaces the
// AP3 stub: andy-agents is the source of truth for agent INSTRUCTIONS + MODEL.
// Tools are NOT sourced here (built into the in-container assistant → empty).
//
// The mapping rules (forced provider/key, model-id derivation, empty tools,
// instructions, limits) are unit-tested via the pure MapToAgentSpec/DeriveModelId
// methods; the HTTP failure-mode contract (404→null, success-roundtrip,
// 5xx→throw, unparseable→throw) is tested against a stubbed HttpMessageHandler.
public class AndyAgentsHttpClientTests
{
    private static readonly AndyAgentsOptions DefaultOptions = new();

    // ---- Pure mapping: instructions ----------------------------------------

    [Fact]
    public void Map_SystemPrompt_BecomesInstructions()
    {
        var dto = Dto(systemPrompt: "You are the coding agent. Edit files.");

        var spec = AndyAgentsHttpClient.MapToAgentSpec(dto, revision: 3, DefaultOptions);

        spec.Should().NotBeNull();
        spec!.Instructions.Should().Be("You are the coding agent. Edit files.");
        spec.Slug.Should().Be("coding", "Slug is mapped from AgentDto.Name");
        spec.Revision.Should().Be(3, "the caller's revision pin is propagated");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Map_EmptySystemPrompt_ReturnsNull(string? prompt)
    {
        var dto = Dto(systemPrompt: prompt);

        var spec = AndyAgentsHttpClient.MapToAgentSpec(dto, revision: null, DefaultOptions);

        spec.Should().BeNull("an instruction-less agent is not runnable");
    }

    // ---- Pure mapping: forced model wiring ---------------------------------

    [Fact]
    public void Map_Model_ProviderForcedOpenAi_ApiKeyForcedOpenAiKey()
    {
        // Even though andy-agents records a non-OpenAI ModelName/preference, the
        // in-container assistant talks the OpenAI dialect to the andy-models
        // proxy and reads OPENAI_API_KEY — so both are FORCED.
        var dto = Dto(modelName: "claude-3-5-sonnet", prefSlugs: new[] { "anthropic/claude-3-5-sonnet" });

        var spec = AndyAgentsHttpClient.MapToAgentSpec(dto, revision: null, DefaultOptions);

        spec!.Model.Provider.Should().Be("openai");
        spec.Model.ApiKeyRef.Should().Be("env:OPENAI_API_KEY");
    }

    // ---- Pure mapping: model-id derivation ---------------------------------

    [Fact]
    public void Map_ModelId_FromPreferenceSlug_StripsProviderPrefix()
    {
        var dto = Dto(modelName: "ignored", prefSlugs: new[] { "deepseek/deepseek-v4-flash" });

        var spec = AndyAgentsHttpClient.MapToAgentSpec(dto, revision: null, DefaultOptions);

        spec!.Model.Id.Should().Be("deepseek-v4-flash",
            "the segment after the last '/' is what the proxy registers");
    }

    [Fact]
    public void Map_ModelId_FromPreferenceSlug_NoPrefix_UsesWholeSlug()
    {
        var dto = Dto(modelName: "ignored", prefSlugs: new[] { "gpt-4o" });

        var spec = AndyAgentsHttpClient.MapToAgentSpec(dto, revision: null, DefaultOptions);

        spec!.Model.Id.Should().Be("gpt-4o");
    }

    [Fact]
    public void Map_ModelId_NoPreferences_FallsBackToModelName()
    {
        var dto = Dto(modelName: "deepseek-v4-flash", prefSlugs: null);

        var spec = AndyAgentsHttpClient.MapToAgentSpec(dto, revision: null, DefaultOptions);

        spec!.Model.Id.Should().Be("deepseek-v4-flash");
    }

    [Fact]
    public void Map_ModelId_EmptyPreferenceList_FallsBackToModelName()
    {
        var dto = Dto(modelName: "gpt-4o-mini", prefSlugs: System.Array.Empty<string>());

        var spec = AndyAgentsHttpClient.MapToAgentSpec(dto, revision: null, DefaultOptions);

        spec!.Model.Id.Should().Be("gpt-4o-mini");
    }

    [Fact]
    public void Map_ModelId_FirstUsablePreferenceWins_SkipsBlankSlugs()
    {
        // A hint-only preference (no Slug) is skipped; the first pinned slug wins.
        var dto = Dto(modelName: "ignored", prefSlugs: new string?[] { null, "x/cerebras-llama-70b" });

        var spec = AndyAgentsHttpClient.MapToAgentSpec(dto, revision: null, DefaultOptions);

        spec!.Model.Id.Should().Be("cerebras-llama-70b");
    }

    // ---- Pure mapping: tools empty + limits/boundaries ---------------------

    [Fact]
    public void Map_Tools_AreEmpty()
    {
        var dto = Dto(systemPrompt: "anything");

        var spec = AndyAgentsHttpClient.MapToAgentSpec(dto, revision: null, DefaultOptions);

        spec!.Tools.Should().BeEmpty(
            "tools are built into the in-container assistant; the allow-list is AX.3/AX.4");
        spec.Boundaries.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Map_Limits_ComeFromOptions()
    {
        var options = new AndyAgentsOptions { DefaultMaxIterations = 42, DefaultTimeoutSeconds = 99 };
        var dto = Dto(systemPrompt: "anything");

        var spec = AndyAgentsHttpClient.MapToAgentSpec(dto, revision: null, options);

        spec!.Limits.MaxIterations.Should().Be(42);
        spec.Limits.TimeoutSeconds.Should().Be(99);
    }

    // ---- HTTP failure-mode contract ----------------------------------------

    [Fact]
    public async Task GetAgentAsync_404_ReturnsNull()
    {
        var client = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var spec = await client.GetAgentAsync("nonexistent", revision: null);

        spec.Should().BeNull("a 404 is a clean unknown-slug → caller maps to 404-equivalent");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetAgentAsync_BlankSlug_ReturnsNullWithoutCall(string? slug)
    {
        var called = false;
        var client = BuildClient(_ => { called = true; return new HttpResponseMessage(HttpStatusCode.OK); });

        var spec = await client.GetAgentAsync(slug!, revision: null);

        spec.Should().BeNull();
        called.Should().BeFalse("a blank slug short-circuits before any HTTP call");
    }

    [Fact]
    public async Task GetAgentAsync_Success_MapsFullSpec()
    {
        const string json = """
        {
          "id": "00000000-0000-0000-0000-000000000001",
          "name": "coding",
          "modelName": "fallback-model",
          "systemPrompt": "You are the coding agent.",
          "temperature": 0.2,
          "maxTokens": 4096,
          "status": "Active",
          "toolIds": ["00000000-0000-0000-0000-000000000099"],
          "modelPreferences": { "preferences": [ { "slug": "deepseek/deepseek-v4-flash" } ] }
        }
        """;
        var client = BuildClient(_ => JsonResponse(json));

        var spec = await client.GetAgentAsync("coding", revision: 5);

        spec.Should().NotBeNull();
        spec!.Slug.Should().Be("coding");
        spec.Revision.Should().Be(5);
        spec.Instructions.Should().Be("You are the coding agent.");
        spec.Model.Provider.Should().Be("openai");
        spec.Model.ApiKeyRef.Should().Be("env:OPENAI_API_KEY");
        spec.Model.Id.Should().Be("deepseek-v4-flash", "preference slug wins + prefix stripped");
        spec.Tools.Should().BeEmpty("ToolIds are ignored; tools are built into the assistant");
    }

    [Fact]
    public async Task GetAgentAsync_5xx_Throws()
    {
        var client = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom"),
        });

        var act = () => client.GetAgentAsync("coding", revision: null);

        await act.Should().ThrowAsync<AndyAgentsResolutionException>()
            .WithMessage("*500*");
    }

    [Fact]
    public async Task GetAgentAsync_UnparseableBody_Throws()
    {
        var client = BuildClient(_ => JsonResponse("not json at all <<<"));

        var act = () => client.GetAgentAsync("coding", revision: null);

        await act.Should().ThrowAsync<AndyAgentsResolutionException>();
    }

    [Fact]
    public async Task GetAgentAsync_NetworkError_Throws()
    {
        var client = BuildClient(_ => throw new HttpRequestException("connection refused"));

        var act = () => client.GetAgentAsync("coding", revision: null);

        await act.Should().ThrowAsync<AndyAgentsResolutionException>();
    }

    // ---- helpers -----------------------------------------------------------

    private static AndyAgentsHttpClient.AgentDtoWire Dto(
        string? name = "coding",
        string modelName = "deepseek-v4-flash",
        string? systemPrompt = "You are an agent.",
        string?[]? prefSlugs = null)
    {
        AndyAgentsHttpClient.AgentModelPreferencesWire? prefs = null;
        if (prefSlugs is not null)
        {
            var items = prefSlugs
                .Select(s => (AndyAgentsHttpClient.AgentModelPreferenceWire?)
                    new AndyAgentsHttpClient.AgentModelPreferenceWire(s))
                .ToList();
            prefs = new AndyAgentsHttpClient.AgentModelPreferencesWire(items);
        }
        return new AndyAgentsHttpClient.AgentDtoWire(name, modelName, systemPrompt, prefs);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static AndyAgentsHttpClient BuildClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://andy-agents.test/") };
        var factory = new SingleClientFactory(http);
        return new AndyAgentsHttpClient(
            factory,
            Options.Create(new AndyAgentsOptions()),
            NullLogger<AndyAgentsHttpClient>.Instance);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingleClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }
}
