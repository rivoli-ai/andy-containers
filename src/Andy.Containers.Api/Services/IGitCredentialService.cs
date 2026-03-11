namespace Andy.Containers.Api.Services;

public interface IGitCredentialService
{
    Task<GitCredentialInfo> StoreCredentialAsync(string userId, string label, string credentialType, string value, string? gitHost = null, CancellationToken ct = default);
    Task<IReadOnlyList<GitCredentialInfo>> ListCredentialsAsync(string userId, CancellationToken ct = default);
    Task<bool> RemoveCredentialAsync(string userId, Guid credentialId, CancellationToken ct = default);
    Task<bool> UpdateCredentialAsync(string userId, Guid credentialId, string newValue, CancellationToken ct = default);
}

public record GitCredentialInfo(Guid Id, string Label, string CredentialType, string? GitHost, DateTime CreatedAt);
