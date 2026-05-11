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
    GeminiCode,
    GitHubCopilot,
    AmazonQ,
    Cline
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

    /// <summary>
    /// rivoli-ai/conductor#943 (M1.5.1). Model slugs the per-container
    /// proxy token should grant access to. When null, the orchestration
    /// service falls back to <see cref="ToolSlugDefaults"/> for this
    /// tool. When set to an empty list, no proxy token is minted — the
    /// container is presumed to talk to its model surface directly
    /// (Ollama, OpenAI-compatible self-hosted, etc.).
    /// </summary>
    public List<string>? RequiredModelSlugs { get; set; }
}
