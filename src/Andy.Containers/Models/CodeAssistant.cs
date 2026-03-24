namespace Andy.Containers.Models;

public enum CodeAssistantType
{
    ClaudeCode,
    CodexCli,
    Aider,
    Continue,
    OpenCode,
    QwenCoder,
    GeminiCode
}

public class CodeAssistantConfig
{
    public CodeAssistantType Tool { get; set; }
    public bool AutoStart { get; set; }
    public string? ApiKeyEnvVar { get; set; }
}
