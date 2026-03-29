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

    [Fact]
    public void GenerateInstallScript_NpmTools_InstallNodeBeforePackage()
    {
        // The script should check for npm first, install Node if missing, THEN install the package.
        // Previously the script tried npm first and fell back, which failed silently.
        var npmTools = new[] { CodeAssistantType.ClaudeCode, CodeAssistantType.CodexCli, CodeAssistantType.OpenCode, CodeAssistantType.GeminiCode };
        foreach (var tool in npmTools)
        {
            var script = _service.GenerateInstallScript(new CodeAssistantConfig { Tool = tool });

            // Should check for npm availability first
            script.Should().Contain("command -v npm", $"{tool} should check npm availability");

            // Node install should come before the npm install -g command
            var nodeInstallPos = script.IndexOf("nodesource");
            var npmInstallPos = script.LastIndexOf("npm install -g");
            nodeInstallPos.Should().BeLessThan(npmInstallPos,
                $"{tool}: Node.js install should come before npm install -g");
        }
    }

    [Fact]
    public void GenerateInstallScript_NpmTools_SetDebianFrontend()
    {
        // Ensure DEBIAN_FRONTEND=noninteractive is set to prevent apt-get prompts
        var npmTools = new[] { CodeAssistantType.ClaudeCode, CodeAssistantType.CodexCli, CodeAssistantType.OpenCode, CodeAssistantType.GeminiCode };
        foreach (var tool in npmTools)
        {
            var script = _service.GenerateInstallScript(new CodeAssistantConfig { Tool = tool });
            script.Should().Contain("DEBIAN_FRONTEND=noninteractive",
                $"{tool} should set DEBIAN_FRONTEND for unattended apt-get");
        }
    }

    [Fact]
    public void GenerateInstallScript_ClaudeCode_ContainsExactPackageName()
    {
        var script = _service.GenerateInstallScript(new CodeAssistantConfig { Tool = CodeAssistantType.ClaudeCode });
        script.Should().Contain("npm install -g @anthropic-ai/claude-code");
    }

    [Fact]
    public void GenerateInstallScript_NpmTools_SupportsAlpine()
    {
        // The install script should contain apk as a fallback for Alpine Linux
        var npmTools = new[] { CodeAssistantType.ClaudeCode, CodeAssistantType.CodexCli, CodeAssistantType.OpenCode, CodeAssistantType.GeminiCode };
        foreach (var tool in npmTools)
        {
            var script = _service.GenerateInstallScript(new CodeAssistantConfig { Tool = tool });
            script.Should().Contain("apk", $"{tool} should support Alpine Linux (apk)");
        }
    }

    [Fact]
    public void GenerateInstallScript_NpmTools_SupportsDnf()
    {
        var npmTools = new[] { CodeAssistantType.ClaudeCode, CodeAssistantType.CodexCli, CodeAssistantType.OpenCode, CodeAssistantType.GeminiCode };
        foreach (var tool in npmTools)
        {
            var script = _service.GenerateInstallScript(new CodeAssistantConfig { Tool = tool });
            script.Should().Contain("dnf", $"{tool} should support RHEL/Fedora (dnf)");
        }
    }

    [Fact]
    public void GenerateInstallScript_PipTools_SupportsAlpine()
    {
        var pipTools = new[] { CodeAssistantType.Aider, CodeAssistantType.QwenCoder };
        foreach (var tool in pipTools)
        {
            var script = _service.GenerateInstallScript(new CodeAssistantConfig { Tool = tool });
            script.Should().Contain("apk", $"{tool} should support Alpine Linux (apk)");
        }
    }
}
