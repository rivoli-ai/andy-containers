using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public interface IApiKeyService
{
    Task<ApiKeyCredential> CreateAsync(string ownerId, string label, ApiKeyProvider provider, string apiKey,
        string? envVarName = null, Guid? organizationId = null, string? ipAddress = null, string? baseUrl = null, CancellationToken ct = default);
    Task<ApiKeyCredential?> GetAsync(Guid id, string ownerId, CancellationToken ct = default);
    Task<IReadOnlyList<ApiKeyCredential>> ListAsync(string ownerId, CancellationToken ct = default);
    Task<IReadOnlyList<ApiKeyCredential>> ListByOrganizationAsync(Guid organizationId, CancellationToken ct = default);
    Task<ApiKeyCredential?> UpdateAsync(Guid id, string ownerId, string? label, string? apiKey,
        string? ipAddress = null, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, string ownerId, string? ipAddress = null, CancellationToken ct = default);
    Task<ApiKeyValidationResult> ValidateExistingAsync(Guid id, string ownerId,
        string? ipAddress = null, CancellationToken ct = default);
    Task<string?> ResolveKeyAsync(string ownerId, ApiKeyProvider provider, CancellationToken ct = default);
    Task<IReadOnlyList<ApiKeyChangeEntry>> GetHistoryAsync(Guid id, string ownerId, CancellationToken ct = default);
}

public class ApiKeyChangeEntry
{
    public required string Action { get; set; }
    public required string Timestamp { get; set; }
    public string? IpAddress { get; set; }
    public string? Details { get; set; }
}
