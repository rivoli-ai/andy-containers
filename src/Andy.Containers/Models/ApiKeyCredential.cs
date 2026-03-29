namespace Andy.Containers.Models;

public class ApiKeyCredential
{
    public Guid Id { get; set; }
    public required string OwnerId { get; set; }
    public Guid? OrganizationId { get; set; }
    public required string Label { get; set; }
    public ApiKeyProvider Provider { get; set; }
    public required string EncryptedValue { get; set; }
    public required string EnvVarName { get; set; }
    public string? MaskedValue { get; set; }
    public bool IsValid { get; set; }
    public DateTime? LastValidatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public string? ChangeHistory { get; set; }
    public string? BaseUrl { get; set; }
}

public enum ApiKeyProvider
{
    Anthropic,
    OpenAI,
    Google,
    Dashscope,
    Custom,
    OpenRouter,
    Ollama,
    OpenAiCompatible
}
