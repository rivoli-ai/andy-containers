using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public interface IGitCredentialService
{
    Task<GitCredential> CreateAsync(string ownerId, string label, string token, string? gitHost = null, GitCredentialType type = GitCredentialType.PersonalAccessToken, CancellationToken ct = default);
    Task<IReadOnlyList<GitCredential>> ListAsync(string ownerId, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, string ownerId, CancellationToken ct = default);
    Task<string?> ResolveTokenAsync(string ownerId, string? credentialRef, string? gitHost = null, CancellationToken ct = default);

    /// <summary>
    /// #1046. Returns every credential owned by <paramref name="ownerId"/>
    /// with the <c>EncryptedToken</c> decrypted in-place. Used by
    /// <c>ContainerProvisioningWorker</c> to materialise credentials
    /// into a freshly-provisioned container so user-initiated
    /// <c>git clone</c> commands can authenticate. Decryption stays
    /// behind the service boundary; callers never touch the protector.
    /// </summary>
    Task<IReadOnlyList<DecryptedGitCredential>> ListWithDecryptedTokensAsync(
        string ownerId,
        CancellationToken ct = default);
}

/// <summary>
/// #1046. A user's <see cref="GitCredential"/> with its token decrypted.
/// Surface only — never persisted in this shape; the DB row keeps the
/// <c>EncryptedToken</c> field unchanged.
/// </summary>
/// <param name="Id">DB identity.</param>
/// <param name="Label">User-facing label for the credential.</param>
/// <param name="GitHost">Optional host scoping (e.g. <c>github.com</c>); null = match any host.</param>
/// <param name="CredentialType">PAT / DeployKey / OAuthToken — drives the injection mechanism.</param>
/// <param name="PlaintextToken">Decrypted secret. For PAT/OAuth this is the token; for DeployKey it's the PEM-encoded private key.</param>
public sealed record DecryptedGitCredential(
    Guid Id,
    string Label,
    string? GitHost,
    GitCredentialType CredentialType,
    string PlaintextToken);
