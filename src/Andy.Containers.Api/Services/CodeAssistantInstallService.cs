using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public class CodeAssistantInstallService : ICodeAssistantInstallService
{
    public string GenerateInstallScript(CodeAssistantConfig config)
    {
        var install = config.Tool switch
        {
            CodeAssistantType.ClaudeCode =>
                "export DEBIAN_FRONTEND=noninteractive; " +
                "command -v npm >/dev/null 2>&1 || { curl -fsSL https://deb.nodesource.com/setup_20.x | bash - >/dev/null 2>&1 && apt-get install -y -qq nodejs >/dev/null 2>&1; } && " +
                "npm install -g @anthropic-ai/claude-code",

            CodeAssistantType.CodexCli =>
                "export DEBIAN_FRONTEND=noninteractive; " +
                "command -v npm >/dev/null 2>&1 || { curl -fsSL https://deb.nodesource.com/setup_20.x | bash - >/dev/null 2>&1 && apt-get install -y -qq nodejs >/dev/null 2>&1; } && " +
                "npm install -g @openai/codex",

            CodeAssistantType.Aider =>
                "pip install aider-chat 2>/dev/null || " +
                "(command -v pip >/dev/null 2>&1 || command -v pip3 >/dev/null 2>&1 || { apt-get update -qq && apt-get install -y -qq python3-pip >/dev/null 2>&1; }) && " +
                "(pip install aider-chat 2>/dev/null || pip3 install aider-chat)",

            CodeAssistantType.Continue =>
                "echo 'Continue extension will be installed via IDE marketplace'",

            CodeAssistantType.OpenCode =>
                "export DEBIAN_FRONTEND=noninteractive; " +
                "command -v npm >/dev/null 2>&1 || { curl -fsSL https://deb.nodesource.com/setup_20.x | bash - >/dev/null 2>&1 && apt-get install -y -qq nodejs >/dev/null 2>&1; } && " +
                "npm install -g open-code",

            CodeAssistantType.QwenCoder =>
                "pip install qwen-coder-cli 2>/dev/null || " +
                "(command -v pip >/dev/null 2>&1 || command -v pip3 >/dev/null 2>&1 || { apt-get update -qq && apt-get install -y -qq python3-pip >/dev/null 2>&1; }) && " +
                "(pip install qwen-coder-cli 2>/dev/null || pip3 install qwen-coder-cli)",

            CodeAssistantType.GeminiCode =>
                "export DEBIAN_FRONTEND=noninteractive; " +
                "command -v npm >/dev/null 2>&1 || { curl -fsSL https://deb.nodesource.com/setup_20.x | bash - >/dev/null 2>&1 && apt-get install -y -qq nodejs >/dev/null 2>&1; } && " +
                "npm install -g gemini-code",

            _ => "echo 'Unknown code assistant type'"
        };

        return install;
    }

    public string GetDefaultApiKeyEnvVar(CodeAssistantType tool) => tool switch
    {
        CodeAssistantType.ClaudeCode => "ANTHROPIC_API_KEY",
        CodeAssistantType.CodexCli => "OPENAI_API_KEY",
        CodeAssistantType.Aider => "OPENAI_API_KEY",
        CodeAssistantType.Continue => "CONTINUE_API_KEY",
        CodeAssistantType.OpenCode => "OPENAI_API_KEY",
        CodeAssistantType.QwenCoder => "DASHSCOPE_API_KEY",
        CodeAssistantType.GeminiCode => "GOOGLE_API_KEY",
        _ => "API_KEY"
    };
}
