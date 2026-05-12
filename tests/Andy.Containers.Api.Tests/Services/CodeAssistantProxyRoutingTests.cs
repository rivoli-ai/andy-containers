using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

/// <summary>
/// rivoli-ai/conductor#944 (M1.5.2). Pins the per-tool env-var map
/// that lets the andy-models proxy intercept Claude Code, OpenCode,
/// Codex CLI, and Aider without those tools knowing they're being
/// proxied. Drift here is silent: the wrong env var means the tool
/// reaches upstream directly (bypassing the proxy, the UsageEvent
/// log, and the key resolver).
/// </summary>
public class CodeAssistantProxyRoutingTests
{
    [Fact]
    public void For_ClaudeCode_NoBaseUrl_ReturnsAnthropicEnvVars()
    {
        var config = new CodeAssistantConfig { Tool = CodeAssistantType.ClaudeCode };

        var routing = CodeAssistantProxyRouting.For(config);

        routing.Should().NotBeNull();
        routing!.KeyEnvVar.Should().Be("ANTHROPIC_API_KEY");
        routing.BaseUrlEnvVar.Should().Be("ANTHROPIC_BASE_URL");
        routing.DialectPath.Should().Be("anthropic/v1");
    }

    [Fact]
    public void For_OpenCode_NoBaseUrl_ReturnsOpenAIEnvVars()
    {
        var config = new CodeAssistantConfig { Tool = CodeAssistantType.OpenCode };

        var routing = CodeAssistantProxyRouting.For(config);

        routing.Should().NotBeNull();
        routing!.KeyEnvVar.Should().Be("OPENAI_API_KEY");
        routing.BaseUrlEnvVar.Should().Be("OPENAI_BASE_URL");
        routing.DialectPath.Should().Be("openai/v1");
    }

    [Theory]
    [InlineData(CodeAssistantType.CodexCli, "OPENAI_BASE_URL")]
    [InlineData(CodeAssistantType.Aider, "OPENAI_API_BASE")]
    public void For_OtherOpenAIDialectTools_PickTheCorrectBaseEnvVar(
        CodeAssistantType tool, string expectedBaseUrlEnvVar)
    {
        // CodexCli reads OPENAI_BASE_URL (newer spelling) while Aider
        // historically reads OPENAI_API_BASE. Keeping them split here
        // matches what their install scripts inject.
        var config = new CodeAssistantConfig { Tool = tool };

        var routing = CodeAssistantProxyRouting.For(config);

        routing.Should().NotBeNull();
        routing!.KeyEnvVar.Should().Be("OPENAI_API_KEY");
        routing.BaseUrlEnvVar.Should().Be(expectedBaseUrlEnvVar);
    }

    [Theory]
    [InlineData("http://host.docker.internal:11434")]
    [InlineData("https://llm.internal.example.com/v1")]
    [InlineData("  https://leading-trim.example.com  ")]
    public void For_AnyTool_WithExplicitBaseUrl_ReturnsNull(string apiBaseUrl)
    {
        // An explicit ApiBaseUrl means the user (or the launch UI's
        // sub-picker) opted out of proxy routing — Ollama, OpenAI-
        // compatible self-hosted, or any one-off override. The
        // orchestrator's existing direct-credential + ApiBaseUrl path
        // handles those.
        var config = new CodeAssistantConfig
        {
            Tool = CodeAssistantType.OpenCode,
            ApiBaseUrl = apiBaseUrl,
        };

        var routing = CodeAssistantProxyRouting.For(config);

        routing.Should().BeNull();
    }

    [Theory]
    [InlineData(CodeAssistantType.Continue)]
    [InlineData(CodeAssistantType.GitHubCopilot)]
    [InlineData(CodeAssistantType.AmazonQ)]
    [InlineData(CodeAssistantType.Cline)]
    [InlineData(CodeAssistantType.QwenCoder)]
    [InlineData(CodeAssistantType.GeminiCode)]
    public void For_UnsupportedTools_ReturnNull(CodeAssistantType tool)
    {
        var config = new CodeAssistantConfig { Tool = tool };

        var routing = CodeAssistantProxyRouting.For(config);

        routing.Should().BeNull(
            "$\"{tool}\" doesn't read OpenAI/Anthropic env vars — it has its own auth flow, so we must NOT pretend to proxy it");
    }

    [Theory]
    [InlineData("http://host.docker.internal:9100", "anthropic/v1",
        "http://host.docker.internal:9100/models/anthropic/v1")]
    [InlineData("http://host.docker.internal:9100/", "anthropic/v1",
        "http://host.docker.internal:9100/models/anthropic/v1")]
    [InlineData("http://host.docker.internal:9100", "openai/v1",
        "http://host.docker.internal:9100/models/openai/v1")]
    [InlineData("https://proxy.internal.example.com", "/anthropic/v1",
        "https://proxy.internal.example.com/models/anthropic/v1")]
    public void BuildBaseUrl_JoinsProxyBaseWithDialectPath(
        string proxyBase, string dialectPath, string expected)
    {
        // The proxy URL coming from config sometimes has a trailing
        // slash, the dialect path sometimes has a leading slash — the
        // helper normalises both so the result is always exactly one
        // slash between segments. Drift here means a 404 from the proxy.
        var url = CodeAssistantProxyRouting.BuildBaseUrl(proxyBase, dialectPath);

        url.Should().Be(expected);
    }
}
