using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Services;

public interface IApiKeyService
{
    Task<IReadOnlyList<ApiKeyRegistration>> ListAsync(
        string ownerId,
        CancellationToken ct = default);

    Task<ApiKeyRegistration> CreateAsync(
        string ownerId,
        CreateApiKeyCommand command,
        CancellationToken ct = default);

    Task<ApiKeyRegistration> UpdateAsync(
        Guid id,
        string ownerId,
        UpdateApiKeyCommand command,
        CancellationToken ct = default);

    Task DeleteAsync(Guid id, string ownerId, CancellationToken ct = default);

    Task<ApiKeyValidationOutcome> ValidateAsync(
        Guid id,
        string ownerId,
        CancellationToken ct = default);

    Task<IReadOnlyList<ApiKeyAuditRecord>> HistoryAsync(
        Guid id,
        string ownerId,
        CancellationToken ct = default);
}

public sealed record CreateApiKeyCommand(
    string Name,
    string Provider,
    string Value,
    string? Model,
    string? BaseUrl);

public sealed record UpdateApiKeyCommand(
    string? Name,
    string? Value,
    string? Model,
    string? BaseUrl);

public sealed class ApiKeyService : IApiKeyService
{
    private readonly ContainersDbContext _db;
    private readonly IApiKeySecretStore _secretStore;
    private readonly IApiKeyValidator _validator;

    public ApiKeyService(
        ContainersDbContext db,
        IApiKeySecretStore secretStore,
        IApiKeyValidator validator)
    {
        _db = db;
        _secretStore = secretStore;
        _validator = validator;
    }

    public async Task<IReadOnlyList<ApiKeyRegistration>> ListAsync(
        string ownerId,
        CancellationToken ct = default)
        => await _db.ApiKeyRegistrations
            .AsNoTracking()
            .Where(k => k.OwnerId == ownerId)
            .OrderBy(k => k.Provider)
            .ThenBy(k => k.Name)
            .ToListAsync(ct);

    public async Task<ApiKeyRegistration> CreateAsync(
        string ownerId,
        CreateApiKeyCommand command,
        CancellationToken ct = default)
    {
        var name = ValidateName(command.Name);
        var value = ValidateValue(command.Value);
        var provider = ResolveProvider(command.Provider);
        var baseUrl = NormalizeOptional(command.BaseUrl);
        _ = ApiKeyValidator.BuildValidationUri(provider, baseUrl);

        if (await _db.ApiKeyRegistrations.AnyAsync(
                k => k.OwnerId == ownerId && k.Provider == provider.WireName,
                ct))
        {
            throw new ApiKeyConflictException(
                $"A key for provider '{provider.WireName}' already exists.");
        }

        var now = DateTimeOffset.UtcNow;
        var registration = new ApiKeyRegistration
        {
            OwnerId = ownerId,
            Name = name,
            Provider = provider.WireName,
            SecretDefinitionKey = provider.SecretDefinitionKey,
            MaskedValue = Mask(value),
            Model = NormalizeOptional(command.Model),
            BaseUrl = baseUrl,
            CreatedAt = now,
        };

        var previousValue = await _secretStore.GetAsync(
            provider.SecretDefinitionKey,
            ownerId,
            ct);
        await _secretStore.SetAsync(
            provider.SecretDefinitionKey,
            ownerId,
            value,
            ct);

        _db.ApiKeyRegistrations.Add(registration);
        AppendAudit(
            registration.Id,
            ownerId,
            "created",
            $"Provider: {provider.WireName}.",
            now);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            await RestoreSecretAsync(
                provider.SecretDefinitionKey,
                ownerId,
                previousValue,
                CancellationToken.None);
            throw;
        }

        return registration;
    }

    public async Task<ApiKeyRegistration> UpdateAsync(
        Guid id,
        string ownerId,
        UpdateApiKeyCommand command,
        CancellationToken ct = default)
    {
        var registration = await FindAsync(id, ownerId, ct);
        var provider = ResolveProvider(registration.Provider);
        var newName = command.Name is null
            ? registration.Name
            : ValidateName(command.Name);
        var newBaseUrl = command.BaseUrl is null
            ? registration.BaseUrl
            : NormalizeOptional(command.BaseUrl);
        _ = ApiKeyValidator.BuildValidationUri(provider, newBaseUrl);

        string? previousValue = null;
        var keyChanged = command.Value is not null;
        if (keyChanged)
        {
            var value = ValidateValue(command.Value!);
            previousValue = await _secretStore.GetAsync(
                registration.SecretDefinitionKey,
                ownerId,
                ct);
            await _secretStore.SetAsync(
                registration.SecretDefinitionKey,
                ownerId,
                value,
                ct);
            registration.MaskedValue = Mask(value);
            registration.IsValid = null;
            registration.LastValidatedAt = null;
        }

        registration.Name = newName;
        registration.Model = command.Model is null
            ? registration.Model
            : NormalizeOptional(command.Model);
        registration.BaseUrl = newBaseUrl;
        registration.UpdatedAt = DateTimeOffset.UtcNow;
        AppendAudit(
            registration.Id,
            ownerId,
            "updated",
            keyChanged ? "Key rotated and metadata updated." : "Metadata updated.",
            registration.UpdatedAt.Value);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            if (keyChanged)
            {
                await RestoreSecretAsync(
                    registration.SecretDefinitionKey,
                    ownerId,
                    previousValue,
                    CancellationToken.None);
            }
            throw;
        }

        return registration;
    }

    public async Task DeleteAsync(
        Guid id,
        string ownerId,
        CancellationToken ct = default)
    {
        var registration = await FindAsync(id, ownerId, ct);
        var previousValue = await _secretStore.GetAsync(
            registration.SecretDefinitionKey,
            ownerId,
            ct);
        await _secretStore.ClearAsync(
            registration.SecretDefinitionKey,
            ownerId,
            ct);

        _db.ApiKeyRegistrations.Remove(registration);
        AppendAudit(
            registration.Id,
            ownerId,
            "deleted",
            $"Provider: {registration.Provider}.",
            DateTimeOffset.UtcNow);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            await RestoreSecretAsync(
                registration.SecretDefinitionKey,
                ownerId,
                previousValue,
                CancellationToken.None);
            throw;
        }
    }

    public async Task<ApiKeyValidationOutcome> ValidateAsync(
        Guid id,
        string ownerId,
        CancellationToken ct = default)
    {
        var registration = await FindAsync(id, ownerId, ct);
        var provider = ResolveProvider(registration.Provider);
        var value = await _secretStore.GetAsync(
            registration.SecretDefinitionKey,
            ownerId,
            ct);

        var outcome = value is null
            ? new ApiKeyValidationOutcome(
                false,
                "No stored key is available for this provider.",
                null)
            : await _validator.ValidateAsync(
                provider,
                value,
                registration.BaseUrl,
                ct);

        var now = DateTimeOffset.UtcNow;
        registration.IsValid = outcome.IsValid;
        registration.LastValidatedAt = now;
        registration.UpdatedAt = now;
        AppendAudit(
            registration.Id,
            ownerId,
            "validated",
            outcome.IsValid
                ? "Validation succeeded."
                : $"Validation failed: {outcome.Message}",
            now);
        await _db.SaveChangesAsync(ct);
        return outcome;
    }

    public async Task<IReadOnlyList<ApiKeyAuditRecord>> HistoryAsync(
        Guid id,
        string ownerId,
        CancellationToken ct = default)
    {
        var hasRegistration = await _db.ApiKeyRegistrations
            .AsNoTracking()
            .AnyAsync(k => k.Id == id && k.OwnerId == ownerId, ct);
        var hasHistory = await _db.ApiKeyAuditRecords
            .AsNoTracking()
            .AnyAsync(a => a.KeyId == id && a.OwnerId == ownerId, ct);
        if (!hasRegistration && !hasHistory)
        {
            throw new ApiKeyNotFoundException();
        }

        return await _db.ApiKeyAuditRecords
            .AsNoTracking()
            .Where(a => a.KeyId == id && a.OwnerId == ownerId)
            .OrderByDescending(a => a.OccurredAt)
            .ThenByDescending(a => a.Id)
            .ToListAsync(ct);
    }

    private async Task<ApiKeyRegistration> FindAsync(
        Guid id,
        string ownerId,
        CancellationToken ct)
        => await _db.ApiKeyRegistrations
               .FirstOrDefaultAsync(k => k.Id == id && k.OwnerId == ownerId, ct)
           ?? throw new ApiKeyNotFoundException();

    private void AppendAudit(
        Guid keyId,
        string ownerId,
        string kind,
        string? detail,
        DateTimeOffset occurredAt)
        => _db.ApiKeyAuditRecords.Add(new ApiKeyAuditRecord
        {
            KeyId = keyId,
            OwnerId = ownerId,
            Kind = kind,
            Detail = detail,
            OccurredAt = occurredAt,
        });

    private async Task RestoreSecretAsync(
        string definitionKey,
        string ownerId,
        string? previousValue,
        CancellationToken ct)
    {
        if (previousValue is null)
        {
            await _secretStore.ClearAsync(definitionKey, ownerId, ct);
        }
        else
        {
            await _secretStore.SetAsync(
                definitionKey,
                ownerId,
                previousValue,
                ct);
        }
    }

    private static ApiKeyProviderDefinition ResolveProvider(string provider)
    {
        if (ApiKeyProviderCatalog.TryGet(provider, out var definition))
        {
            return definition;
        }

        throw new ApiKeyValidationException(
            $"Unsupported provider. Expected one of: " +
            $"{string.Join(", ", ApiKeyProviderCatalog.SupportedProviders)}.");
    }

    private static string ValidateName(string name)
    {
        var normalized = name.Trim();
        if (normalized.Length is < 1 or > 128)
        {
            throw new ApiKeyValidationException(
                "Name must be between 1 and 128 characters.");
        }
        return normalized;
    }

    private static string ValidateValue(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length is < 1 or > 16_384)
        {
            throw new ApiKeyValidationException(
                "Value must be between 1 and 16384 characters.");
        }
        return normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    internal static string Mask(string rawValue)
    {
        var trimmed = rawValue.Trim();
        var suffix = trimmed.Length >= 4 ? trimmed[^4..] : trimmed;
        var prefix = string.Empty;

        var dash = trimmed.IndexOf('-');
        if (dash is >= 0 and <= 4)
        {
            prefix = trimmed[..(dash + 1)];
        }
        else
        {
            var underscore = trimmed.IndexOf('_');
            if (underscore is >= 0 and <= 4)
            {
                prefix = trimmed[..(underscore + 1)];
            }
        }

        return $"{prefix}••••••••{suffix}";
    }
}

public sealed class ApiKeyNotFoundException : Exception;

public sealed class ApiKeyConflictException : Exception
{
    public ApiKeyConflictException(string message) : base(message) { }
}
