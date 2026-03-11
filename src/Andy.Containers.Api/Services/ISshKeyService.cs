using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public interface ISshKeyService
{
    Task<UserSshKey> AddKeyAsync(string userId, string label, string publicKey, CancellationToken ct = default);
    Task<IReadOnlyList<UserSshKey>> ListKeysAsync(string userId, CancellationToken ct = default);
    Task<bool> RemoveKeyAsync(string userId, Guid keyId, CancellationToken ct = default);
    bool IsValidPublicKey(string publicKey);
    string ComputeFingerprint(string publicKey);
    string DetectKeyType(string publicKey);
}
