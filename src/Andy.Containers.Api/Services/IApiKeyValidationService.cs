using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public interface IApiKeyValidationService
{
    Task<ApiKeyValidationResult> ValidateAsync(ApiKeyProvider provider, string apiKey, CancellationToken ct = default);
}

public record ApiKeyValidationResult(bool IsValid, string? Error = null);
