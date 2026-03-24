using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class CodeAssistantInstallServiceTests
{
    private readonly CodeAssistantInstallService _service = new();

    [Theory]
    [InlineData(CodeAssistantType.ClaudeCode, "@anthropic-ai/claude-code")]
    [InlineData(CodeAssistantType.CodexCli, "@openai/codex")]
    [InlineData(CodeAssistantType.Aider, "aider-chat")]
    [InlineData(CodeAssistantType.Continue, "Continue extension")]
    [InlineData(CodeAssistantType.OpenCode, "open-code")]
    [InlineData(CodeAssistantType.QwenCoder, "qwen-coder-cli")]
    [InlineData(CodeAssistantType.GeminiCode, "gemini-code")]
    public void GenerateInstallScript_ReturnsScriptContainingPackageName(CodeAssistantType tool, string expectedFragment)
    {
        var config = new CodeAssistantConfig { Tool = tool };
        var script = _service.GenerateInstallScript(config);
        script.Should().Contain(expectedFragment);
    }

    [Theory]
    [InlineData(CodeAssistantType.ClaudeCode, "npm install")]
    [InlineData(CodeAssistantType.CodexCli, "npm install")]
    [InlineData(CodeAssistantType.Aider, "pip install")]
    [InlineData(CodeAssistantType.OpenCode, "npm install")]
    [InlineData(CodeAssistantType.QwenCoder, "pip install")]
    [InlineData(CodeAssistantType.GeminiCode, "npm install")]
    public void GenerateInstallScript_UsesCorrectPackageManager(CodeAssistantType tool, string expectedManager)
    {
        var config = new CodeAssistantConfig { Tool = tool };
        var script = _service.GenerateInstallScript(config);
        script.Should().Contain(expectedManager);
    }

    [Theory]
    [InlineData(CodeAssistantType.ClaudeCode, "ANTHROPIC_API_KEY")]
    [InlineData(CodeAssistantType.CodexCli, "OPENAI_API_KEY")]
    [InlineData(CodeAssistantType.Aider, "OPENAI_API_KEY")]
    [InlineData(CodeAssistantType.Continue, "CONTINUE_API_KEY")]
    [InlineData(CodeAssistantType.OpenCode, "OPENAI_API_KEY")]
    [InlineData(CodeAssistantType.QwenCoder, "DASHSCOPE_API_KEY")]
    [InlineData(CodeAssistantType.GeminiCode, "GOOGLE_API_KEY")]
    public void GetDefaultApiKeyEnvVar_ReturnsCorrectVariable(CodeAssistantType tool, string expectedVar)
    {
        _service.GetDefaultApiKeyEnvVar(tool).Should().Be(expectedVar);
    }

    [Fact]
    public void GenerateInstallScript_NpmToolsHaveFallbackNodeInstall()
    {
        var npmTools = new[] { CodeAssistantType.ClaudeCode, CodeAssistantType.CodexCli, CodeAssistantType.OpenCode, CodeAssistantType.GeminiCode };
        foreach (var tool in npmTools)
        {
            var script = _service.GenerateInstallScript(new CodeAssistantConfig { Tool = tool });
            script.Should().Contain("nodesource", $"{tool} should have Node.js fallback install");
        }
    }

    [Fact]
    public void GenerateInstallScript_PipToolsHaveFallbackPipInstall()
    {
        var pipTools = new[] { CodeAssistantType.Aider, CodeAssistantType.QwenCoder };
        foreach (var tool in pipTools)
        {
            var script = _service.GenerateInstallScript(new CodeAssistantConfig { Tool = tool });
            script.Should().Contain("python3-pip", $"{tool} should have pip fallback install");
        }
    }
}
