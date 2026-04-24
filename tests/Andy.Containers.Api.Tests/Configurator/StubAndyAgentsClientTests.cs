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
    [InlineData("triage-agent")]
    [InlineData("planning-agent")]
    [InlineData("coding-agent")]
    public async Task GetAgentAsync_KnownSlug_ReturnsFixture(string slug)
    {
        var spec = await _client.GetAgentAsync(slug, revision: null);

        spec.Should().NotBeNull();
        spec!.Slug.Should().Be(slug);
        spec.Instructions.Should().NotBeNullOrWhiteSpace();
        spec.Model.Provider.Should().Be("anthropic");
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
