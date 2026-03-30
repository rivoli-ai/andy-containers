using System.Diagnostics;
using System.Text.Json;
using Andy.Containers.Api.Telemetry;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

public class ApiKeyService : IApiKeyService
{
    private readonly ContainersDbContext _db;
    private readonly IDataProtector _protector;
    private readonly IApiKeyValidationService _validation;
    private readonly ICodeAssistantInstallService _installService;
    private readonly ILogger<ApiKeyService> _logger;

    private const string ProtectorPurpose = "ApiKeyCredential.Value";

    public ApiKeyService(
        ContainersDbContext db,
        IDataProtectionProvider dataProtection,
        IApiKeyValidationService validation,
        ICodeAssistantInstallService installService,
        ILogger<ApiKeyService> logger)
    {
        _db = db;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _validation = validation;
        _installService = installService;
        _logger = logger;
    }

    public async Task<ApiKeyCredential> CreateAsync(string ownerId, string label, ApiKeyProvider provider, string apiKey,
        string? envVarName = null, Guid? organizationId = null, string? ipAddress = null, string? baseUrl = null, string? modelName = null, CancellationToken ct = default)
    {
        using var activity = ActivitySources.ApiKeys.StartActivity("ApiKey.Create");
        activity?.SetTag("apiKey.provider", provider.ToString());

        // Resolve default env var name if not provided
        var resolvedEnvVar = envVarName ?? GetDefaultEnvVar(provider);

        // Validate the key
        var validationResult = await _validation.ValidateAsync(provider, apiKey, ct);

        var masked = MaskKey(apiKey);
        var credential = new ApiKeyCredential
        {
            OwnerId = ownerId,
            OrganizationId = organizationId,
            Label = label,
            Provider = provider,
            EncryptedValue = _protector.Protect(apiKey),
            EnvVarName = resolvedEnvVar,
            MaskedValue = masked,
            IsValid = validationResult.IsValid,
            LastValidatedAt = DateTime.UtcNow,
            BaseUrl = baseUrl,
            ModelName = modelName,
            ChangeHistory = JsonSerializer.Serialize(new List<ApiKeyChangeEntry>
            {
                new()
                {
                    Action = "created",
                    Timestamp = DateTime.UtcNow.ToString("O"),
                    IpAddress = ipAddress,
                    Details = validationResult.IsValid ? "Key validated successfully" : $"Validation failed: {validationResult.Error}"
                }
            })
        };

        _db.ApiKeyCredentials.Add(credential);
        await _db.SaveChangesAsync(ct);

        Meters.ApiKeysCreated.Add(1, new KeyValuePair<string, object?>("provider", provider.ToString()));
        _logger.LogInformation("API key created for user {OwnerId}, provider {Provider}, valid={IsValid}",
            ownerId, provider, validationResult.IsValid);

        return credential;
    }

    public async Task<ApiKeyCredential?> GetAsync(Guid id, string ownerId, CancellationToken ct = default)
    {
        return await _db.ApiKeyCredentials
            .FirstOrDefaultAsync(k => k.Id == id && k.OwnerId == ownerId, ct);
    }

    public async Task<IReadOnlyList<ApiKeyCredential>> ListAsync(string ownerId, CancellationToken ct = default)
    {
        return await _db.ApiKeyCredentials
            .Where(k => k.OwnerId == ownerId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ApiKeyCredential>> ListByOrganizationAsync(Guid organizationId, CancellationToken ct = default)
    {
        return await _db.ApiKeyCredentials
            .Where(k => k.OrganizationId == organizationId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<ApiKeyCredential?> UpdateAsync(Guid id, string ownerId, string? label, string? apiKey,
        string? ipAddress = null, CancellationToken ct = default)
    {
        using var activity = ActivitySources.ApiKeys.StartActivity("ApiKey.Update");
        activity?.SetTag("apiKey.id", id.ToString());

        var credential = await _db.ApiKeyCredentials
            .FirstOrDefaultAsync(k => k.Id == id && k.OwnerId == ownerId, ct);
        if (credential is null) return null;

        if (!string.IsNullOrEmpty(label))
            credential.Label = label;

        var details = "Label updated";
        if (!string.IsNullOrEmpty(apiKey))
        {
            credential.EncryptedValue = _protector.Protect(apiKey);
            credential.MaskedValue = MaskKey(apiKey);

            var validationResult = await _validation.ValidateAsync(credential.Provider, apiKey, ct);
            credential.IsValid = validationResult.IsValid;
            credential.LastValidatedAt = DateTime.UtcNow;
            details = validationResult.IsValid ? "Key updated and validated" : $"Key updated, validation failed: {validationResult.Error}";
        }

        credential.UpdatedAt = DateTime.UtcNow;
        AppendHistory(credential, "updated", ipAddress, details);

        await _db.SaveChangesAsync(ct);
        return credential;
    }

    public async Task<bool> DeleteAsync(Guid id, string ownerId, string? ipAddress = null, CancellationToken ct = default)
    {
        using var activity = ActivitySources.ApiKeys.StartActivity("ApiKey.Delete");
        activity?.SetTag("apiKey.id", id.ToString());

        var credential = await _db.ApiKeyCredentials
            .FirstOrDefaultAsync(k => k.Id == id && k.OwnerId == ownerId, ct);
        if (credential is null) return false;

        _db.ApiKeyCredentials.Remove(credential);
        await _db.SaveChangesAsync(ct);

        Meters.ApiKeysDeleted.Add(1);
        return true;
    }

    public async Task<ApiKeyValidationResult> ValidateExistingAsync(Guid id, string ownerId,
        string? ipAddress = null, CancellationToken ct = default)
    {
        using var activity = ActivitySources.ApiKeys.StartActivity("ApiKey.ValidateExisting");
        activity?.SetTag("apiKey.id", id.ToString());

        var credential = await _db.ApiKeyCredentials
            .FirstOrDefaultAsync(k => k.Id == id && k.OwnerId == ownerId, ct);
        if (credential is null)
            return new ApiKeyValidationResult(false, "Key not found");

        var decryptedKey = _protector.Unprotect(credential.EncryptedValue);
        var result = await _validation.ValidateAsync(credential.Provider, decryptedKey, ct);

        credential.IsValid = result.IsValid;
        credential.LastValidatedAt = DateTime.UtcNow;
        AppendHistory(credential, "validated", ipAddress,
            result.IsValid ? "Key validated successfully" : $"Validation failed: {result.Error}");

        await _db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<string?> ResolveKeyAsync(string ownerId, ApiKeyProvider provider, CancellationToken ct = default)
    {
        using var activity = ActivitySources.ApiKeys.StartActivity("ApiKey.Resolve");
        activity?.SetTag("apiKey.provider", provider.ToString());

        var credential = await _db.ApiKeyCredentials
            .Where(k => k.OwnerId == ownerId && k.Provider == provider)
            .OrderByDescending(k => k.IsValid)
            .ThenByDescending(k => k.LastValidatedAt)
            .FirstOrDefaultAsync(ct);

        if (credential is null) return null;

        credential.LastUsedAt = DateTime.UtcNow;
        AppendHistory(credential, "used", null, "Injected into container");

        await _db.SaveChangesAsync(ct);

        Meters.ApiKeysInjected.Add(1, new KeyValuePair<string, object?>("provider", provider.ToString()));
        return _protector.Unprotect(credential.EncryptedValue);
    }

    public async Task<ResolvedApiKey?> ResolveCredentialAsync(string ownerId, ApiKeyProvider provider, CancellationToken ct = default)
    {
        var credential = await _db.ApiKeyCredentials
            .Where(k => k.OwnerId == ownerId && k.Provider == provider)
            .OrderByDescending(k => k.IsValid)
            .ThenByDescending(k => k.LastValidatedAt)
            .FirstOrDefaultAsync(ct);

        if (credential is null) return null;

        credential.LastUsedAt = DateTime.UtcNow;
        AppendHistory(credential, "used", null, "Resolved with credential details");
        await _db.SaveChangesAsync(ct);

        return new ResolvedApiKey
        {
            ApiKey = _protector.Unprotect(credential.EncryptedValue),
            BaseUrl = credential.BaseUrl,
            ModelName = credential.ModelName
        };
    }

    public async Task<IReadOnlyList<ApiKeyChangeEntry>> GetHistoryAsync(Guid id, string ownerId, CancellationToken ct = default)
    {
        var credential = await _db.ApiKeyCredentials
            .FirstOrDefaultAsync(k => k.Id == id && k.OwnerId == ownerId, ct);
        if (credential is null) return [];

        if (string.IsNullOrEmpty(credential.ChangeHistory))
            return [];

        return JsonSerializer.Deserialize<List<ApiKeyChangeEntry>>(credential.ChangeHistory) ?? [];
    }

    private static string MaskKey(string key)
    {
        if (key.Length <= 8) return "****";
        return $"****...{key[^4..]}";
    }

    private static void AppendHistory(ApiKeyCredential credential, string action, string? ipAddress, string? details)
    {
        var history = string.IsNullOrEmpty(credential.ChangeHistory)
            ? new List<ApiKeyChangeEntry>()
            : JsonSerializer.Deserialize<List<ApiKeyChangeEntry>>(credential.ChangeHistory) ?? [];

        history.Add(new ApiKeyChangeEntry
        {
            Action = action,
            Timestamp = DateTime.UtcNow.ToString("O"),
            IpAddress = ipAddress,
            Details = details
        });

        // Keep last 100 entries
        if (history.Count > 100)
            history = history.Skip(history.Count - 100).ToList();

        credential.ChangeHistory = JsonSerializer.Serialize(history);
    }

    private string GetDefaultEnvVar(ApiKeyProvider provider) => provider switch
    {
        ApiKeyProvider.Anthropic => "ANTHROPIC_API_KEY",
        ApiKeyProvider.OpenAI => "OPENAI_API_KEY",
        ApiKeyProvider.Google => "GOOGLE_API_KEY",
        ApiKeyProvider.Dashscope => "DASHSCOPE_API_KEY",
        ApiKeyProvider.OpenRouter => "OPENROUTER_API_KEY",
        ApiKeyProvider.Ollama => "OLLAMA_API_KEY",
        ApiKeyProvider.OpenAiCompatible => "OPENAI_API_KEY",
        _ => "API_KEY"
    };
}
