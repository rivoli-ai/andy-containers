using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public interface IGitCredentialService
{
    Task<GitCredential> CreateAsync(string ownerId, string label, string token, string? gitHost = null, GitCredentialType type = GitCredentialType.PersonalAccessToken, CancellationToken ct = default);
    Task<IReadOnlyList<GitCredential>> ListAsync(string ownerId, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, string ownerId, CancellationToken ct = default);
    Task<string?> ResolveTokenAsync(string ownerId, string? credentialRef, string? gitHost = null, CancellationToken ct = default);
}
