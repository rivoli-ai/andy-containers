using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public class CodeAssistantInstallService : ICodeAssistantInstallService
{
    // Distro-agnostic Node.js install: works on Alpine (apk), Debian/Ubuntu (apt), and RHEL (dnf/yum)
    private const string InstallNodeJs =
        "command -v npm >/dev/null 2>&1 || { " +
            "if command -v apk >/dev/null 2>&1; then " +
                "apk add --quiet --no-cache nodejs npm; " +
            "elif command -v apt-get >/dev/null 2>&1; then " +
                "export DEBIAN_FRONTEND=noninteractive && " +
                "curl -fsSL https://deb.nodesource.com/setup_20.x | bash - >/dev/null 2>&1 && " +
                "apt-get install -y -qq nodejs >/dev/null 2>&1; " +
            "elif command -v dnf >/dev/null 2>&1; then " +
                "dnf install -y -q nodejs npm; " +
            "fi; }";

    // Distro-agnostic Python/pip install
    private const string InstallPip =
        "command -v pip >/dev/null 2>&1 || command -v pip3 >/dev/null 2>&1 || { " +
            "if command -v apk >/dev/null 2>&1; then " +
                "apk add --quiet --no-cache python3 py3-pip; " +
            "elif command -v apt-get >/dev/null 2>&1; then " +
                "apt-get update -qq && apt-get install -y -qq python3-pip >/dev/null 2>&1; " +
            "elif command -v dnf >/dev/null 2>&1; then " +
                "dnf install -y -q python3-pip; " +
            "fi; }";

    public string GenerateInstallScript(CodeAssistantConfig config)
    {
        var install = config.Tool switch
        {
            CodeAssistantType.ClaudeCode =>
                $"{InstallNodeJs} && npm install -g @anthropic-ai/claude-code",

            CodeAssistantType.CodexCli =>
                $"{InstallNodeJs} && npm install -g @openai/codex",

            CodeAssistantType.Aider =>
                $"{InstallPip} && (pip install aider-chat 2>/dev/null || pip3 install aider-chat)",

            CodeAssistantType.Continue =>
                "echo 'Continue extension will be installed via IDE marketplace'",

            CodeAssistantType.OpenCode =>
                "ARCH=$(uname -m | sed 's/aarch64/arm64/' | sed 's/x86_64/x86_64/') && " +
                "curl -fsSL https://github.com/opencode-ai/opencode/releases/latest/download/opencode-linux-${ARCH}.tar.gz | tar -xzf - -C /usr/local/bin opencode && " +
                "chmod +x /usr/local/bin/opencode && " +
                // Create a wrapper that writes config before launching (OpenCode overwrites on first run)
                "mv /usr/local/bin/opencode /usr/local/bin/opencode-bin && " +
                "printf '#!/bin/sh\\n" +
                "CONFIG=\"$HOME/.opencode.json\"\\n" +
                "MODEL=\"${LLM_MODEL:-gpt-4o}\"\\n" +
                "printf \\'\\'{\"providers\":{\"openai\":{\"apiKey\":\"env:OPENAI_API_KEY\"}},\"agents\":{\"coder\":{\"model\":\"%s\",\"maxTokens\":5000},\"task\":{\"model\":\"%s\",\"maxTokens\":5000},\"title\":{\"model\":\"%s\",\"maxTokens\":80}}}\\'\\'  \"$MODEL\" \"$MODEL\" \"$MODEL\" > \"$CONFIG\"\\n" +
                "exec /usr/local/bin/opencode-bin \"$@\"\\n' > /usr/local/bin/opencode && " +
                "chmod +x /usr/local/bin/opencode",

            CodeAssistantType.QwenCoder =>
                $"{InstallPip} && (pip install qwen-coder-cli 2>/dev/null || pip3 install qwen-coder-cli)",

            CodeAssistantType.GeminiCode =>
                $"{InstallNodeJs} && npm install -g gemini-code",

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

    public string GetDefaultBaseUrlEnvVar(CodeAssistantType tool) => "OPENAI_API_BASE";

    public string GetDefaultModelEnvVar(CodeAssistantType tool) => tool switch
    {
        CodeAssistantType.Aider => "AIDER_MODEL",
        CodeAssistantType.OpenCode => "LLM_MODEL",
        CodeAssistantType.CodexCli => "OPENAI_MODEL",
        _ => "LLM_MODEL"
    };
}
