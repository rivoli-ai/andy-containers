namespace Andy.Containers.Models;

/// <summary>
/// User-scoped metadata for a provider API key. The plaintext secret is not
/// stored in this database; <see cref="SecretDefinitionKey"/> points to the
/// encrypted, user-scoped value in andy-settings.
/// </summary>
public sealed class ApiKeyRegistration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string OwnerId { get; set; }
    public required string Name { get; set; }
    public required string Provider { get; set; }
    public required string SecretDefinitionKey { get; set; }
    public required string MaskedValue { get; set; }
    public string? Model { get; set; }
    public string? BaseUrl { get; set; }
    public bool? IsValid { get; set; }
    public DateTimeOffset? LastValidatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>
/// Append-only, user-scoped history for an API-key registration. There is no
/// foreign key to <see cref="ApiKeyRegistration"/> so the final deletion event
/// remains queryable after the registration row is removed.
/// </summary>
public sealed class ApiKeyAuditRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid KeyId { get; set; }
    public required string OwnerId { get; set; }
    public required string Kind { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Detail { get; set; }
}
