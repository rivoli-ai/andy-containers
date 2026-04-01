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
                // Download and run the install script inline (same logic as scripts/container/install-opencode.sh)
                "ARCH=$(uname -m | sed 's/aarch64/arm64/' | sed 's/x86_64/x86_64/') && " +
                "cd /tmp && curl -fsSL -o oc.tar.gz https://github.com/opencode-ai/opencode/releases/latest/download/opencode-linux-${ARCH}.tar.gz && " +
                "tar xzf oc.tar.gz && mv opencode /usr/local/bin/opencode-bin && chmod +x /usr/local/bin/opencode-bin && rm -f oc.tar.gz LICENSE README.md && " +
                "cat > /usr/local/bin/opencode << 'OCWRAP'\n" +
                "#!/bin/sh\n" +
                ". /etc/profile 2>/dev/null\n" +
                "for f in /etc/profile.d/*.sh; do [ -f \"$f\" ] && . \"$f\" 2>/dev/null; done\n" +
                "M=${LLM_MODEL:-gpt-4o}\n" +
                "K=${OPENAI_API_KEY:-}\n" +
                "cat > $HOME/.opencode.json << OCCONF\n" +
                "{\"providers\":{\"openai\":{\"apiKey\":\"$K\"}},\"agents\":{\"coder\":{\"model\":\"$M\",\"maxTokens\":5000},\"task\":{\"model\":\"$M\",\"maxTokens\":5000},\"title\":{\"model\":\"$M\",\"maxTokens\":80}}}\n" +
                "OCCONF\n" +
                "exec /usr/local/bin/opencode-bin \"$@\"\n" +
                "OCWRAP\n" +
                "chmod +x /usr/local/bin/opencode",

            CodeAssistantType.QwenCoder =>
                $"{InstallPip} && (pip install qwen-coder-cli 2>/dev/null || pip3 install qwen-coder-cli)",

            CodeAssistantType.GeminiCode =>
                $"{InstallNodeJs} && npm install -g gemini-code",

            CodeAssistantType.GitHubCopilot =>
                // gh CLI installed by PostCreateScript; just add the copilot extension
                "command -v gh >/dev/null 2>&1 && gh extension install github/gh-copilot || echo 'gh CLI not found, install it first'",

            CodeAssistantType.AmazonQ =>
                // Amazon Q CLI installed via npm
                $"{InstallNodeJs} && npm install -g @aws/amazon-q-developer-cli",

            CodeAssistantType.Cline =>
                // Cline is a VS Code extension, install via code-server if available
                "command -v code-server >/dev/null 2>&1 && code-server --install-extension saoudrizwan.claude-dev || echo 'Cline requires code-server IDE'",

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
        CodeAssistantType.GitHubCopilot => "GITHUB_TOKEN",
        CodeAssistantType.AmazonQ => "AWS_ACCESS_KEY_ID",
        CodeAssistantType.Cline => "ANTHROPIC_API_KEY",
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
