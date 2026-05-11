using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

/// <summary>
/// rivoli-ai/conductor#943. Covers the Tool → default slug map and the
/// override rules (null = use default; non-null = use explicit value
/// including explicit empty).
/// </summary>
public class ToolSlugDefaultsTests
{
    [Fact]
    public void Resolve_WhenRequiredModelSlugsIsSet_ReturnsExplicitOverride()
    {
        var config = new CodeAssistantConfig
        {
            Tool = CodeAssistantType.ClaudeCode,
            RequiredModelSlugs = new List<string> { "anthropic/claude-opus-4" },
        };

        var slugs = ToolSlugDefaults.Resolve(config);

        slugs.Should().Equal("anthropic/claude-opus-4");
    }

    [Fact]
    public void Resolve_WhenRequiredModelSlugsIsExplicitlyEmpty_ReturnsEmpty()
    {
        var config = new CodeAssistantConfig
        {
            Tool = CodeAssistantType.ClaudeCode,
            // Explicit empty: "I know the default but I want no proxy token."
            RequiredModelSlugs = new List<string>(),
        };

        var slugs = ToolSlugDefaults.Resolve(config);

        slugs.Should().BeEmpty(
            "explicit empty list means the caller knows they don't need a proxy token (e.g. local-only setup).");
    }

    [Fact]
    public void Resolve_WhenRequiredModelSlugsIsNull_ClaudeCode_FallsBackToAnthropicSonnetDefault()
    {
        var config = new CodeAssistantConfig
        {
            Tool = CodeAssistantType.ClaudeCode,
            RequiredModelSlugs = null,
        };

        var slugs = ToolSlugDefaults.Resolve(config);

        slugs.Should().Equal("anthropic/claude-sonnet-4-6");
    }

    [Fact]
    public void Resolve_WhenRequiredModelSlugsIsNull_OpenCode_FallsBackToEmpty()
    {
        var config = new CodeAssistantConfig
        {
            Tool = CodeAssistantType.OpenCode,
            RequiredModelSlugs = null,
        };

        var slugs = ToolSlugDefaults.Resolve(config);

        slugs.Should().BeEmpty(
            "OpenCode is multi-provider; without an explicit slug list we don't guess.");
    }

    [Theory]
    [InlineData(CodeAssistantType.Aider)]
    [InlineData(CodeAssistantType.CodexCli)]
    [InlineData(CodeAssistantType.Continue)]
    [InlineData(CodeAssistantType.QwenCoder)]
    [InlineData(CodeAssistantType.GeminiCode)]
    [InlineData(CodeAssistantType.GitHubCopilot)]
    [InlineData(CodeAssistantType.AmazonQ)]
    [InlineData(CodeAssistantType.Cline)]
    public void Resolve_UnmappedTools_FallBackToEmpty(CodeAssistantType tool)
    {
        var config = new CodeAssistantConfig { Tool = tool };

        var slugs = ToolSlugDefaults.Resolve(config);

        slugs.Should().BeEmpty(
            "tools without a concrete default must fall back to empty rather than a wrongly-scoped guess.");
    }
}
