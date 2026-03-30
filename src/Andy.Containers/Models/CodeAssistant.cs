using System.Text.Json.Serialization;

namespace Andy.Containers.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
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
    public string? ApiBaseUrl { get; set; }
    public string? ApiBaseUrlEnvVar { get; set; }
    public string? ModelName { get; set; }
    public string? ModelEnvVar { get; set; }
}
