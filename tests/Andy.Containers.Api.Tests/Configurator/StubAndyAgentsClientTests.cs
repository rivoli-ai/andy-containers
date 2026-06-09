using Andy.Containers.Api.Services;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Configurator;

// AP3 (rivoli-ai/andy-containers#105). The stub is the only thing standing
// between AP3 and a real andy-agents service; its fixtures are part of the
// developer-experience contract until Epic W lands.
public class StubAndyAgentsClientTests
{
    private readonly StubAndyAgentsClient _client = new();

    [Theory]
    // andy-agents roster slugs the planner actually emits…
    [InlineData("coding")]
    [InlineData("review")]
    [InlineData("planning")]
    [InlineData("triage")]
    [InlineData("research")]
    [InlineData("validation")]
    // …and the legacy "<role>-agent" aliases.
    [InlineData("triage-agent")]
    [InlineData("coding-agent")]
    public async Task GetAgentAsync_KnownSlug_ReturnsProxyRoutedSpec(string slug)
    {
        var spec = await _client.GetAgentAsync(slug, revision: null);

        spec.Should().NotBeNull();
        spec!.Slug.Should().Be(slug, "the requested slug is echoed back");
        spec.Instructions.Should().NotBeNullOrWhiteSpace();
        // The in-container agent reaches the andy-models proxy, which speaks
        // the OpenAI dialect — NOT a raw provider the HeadlessConfigBuilder
        // allow-list would reject.
        spec.Model.Provider.Should().Be("openai");
        spec.Model.Id.Should().Be("deepseek-v4-flash");
        // The OpenAI-dialect client reads its bearer from OPENAI_API_KEY (the
        // per-container aud=urn:andy-models-api proxy token), NOT the shared
        // ANDY_SERVICE_TOKEN (aud=urn:andy-containers-api, rejected 401).
        spec.Model.ApiKeyRef.Should().Be("env:OPENAI_API_KEY");
    }

    [Fact]
    public async Task GetAgentAsync_CodingRole_HasNoSynthesisedTools()
    {
        var spec = await _client.GetAgentAsync("coding", revision: null);

        // AX.5: under the corrected tools model the assistant's tools are
        // BUILT-IN (andy-cli AX.3) and gated by the injected permission
        // allow-list (andy-cli AX.4). The stub no longer synthesises a
        // `shell`(bash -c)+`git` spec for the coding role; Tools is EMPTY for
        // every role, matching AndyAgentsHttpClient.
        spec!.Tools.Should().BeEmpty(
            "tools come from the assistant built-ins now, not synthesised cli tools");

        // The coding prompt no longer references the removed `shell` tool or its
        // `bash -c` transport — it uses the agent's built-in file/edit tools.
        spec.Instructions.Should().NotContain("shell");
        spec.Instructions.Should().NotContain("bash -c");
    }

    [Fact]
    public async Task GetAgentAsync_ReviewRole_IsReadOnly()
    {
        var spec = await _client.GetAgentAsync("review", revision: null);

        spec!.Tools.Should().BeEmpty("review is read-only");
        spec.Boundaries.Should().Contain("read-only");
    }

    [Fact]
    public async Task GetAgentAsync_UnknownSlug_ReturnsNull()
    {
        var spec = await _client.GetAgentAsync("nonexistent-agent", revision: null);

        spec.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetAgentAsync_BlankSlug_ReturnsNull(string? slug)
    {
        var spec = await _client.GetAgentAsync(slug!, revision: null);

        spec.Should().BeNull();
    }

    [Fact]
    public async Task GetAgentAsync_RevisionPin_PropagatesIntoSpec()
    {
        var spec = await _client.GetAgentAsync("triage-agent", revision: 7);

        spec.Should().NotBeNull();
        spec!.Revision.Should().Be(7,
            "stub echoes the caller's pin so AP6 can verify revision propagation end-to-end");
    }
}
