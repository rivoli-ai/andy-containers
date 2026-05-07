using System.Security.Claims;

namespace Andy.Containers.Abstractions.Images;

/// <summary>
/// Translates an <c>andy-auth</c> identity into the credential form a
/// specific registry expects. Implementations cover token exchange
/// (Artifactory scoped tokens), STS assumption (ECR), AAD token
/// passthrough (ACR), workload-identity-federation (GAR), or no-op
/// passthrough (zot).
/// </summary>
public interface IIdentityBridge
{
    /// <summary>
    /// Mint a credential the caller can use to authenticate against
    /// the named registry. The credential's <see cref="RegistryCredential.ExpiresAt"/>
    /// is non-null when the underlying scheme returns one (ECR's 12 h
    /// STS token, Artifactory access tokens with expiry); null means
    /// the credential does not auto-expire.
    /// </summary>
    Task<RegistryCredential> GetCredentialAsync(
        string registryId,
        ClaimsPrincipal principal,
        CancellationToken ct);
}

/// <summary>
/// A short-lived credential authenticating a request to a registry.
/// </summary>
/// <param name="RegistryId">Registry the credential is valid for.</param>
/// <param name="Scheme">
/// HTTP auth scheme — typically <c>Bearer</c> for OIDC-style or
/// token-exchange registries, <c>Basic</c> for username+password
/// schemes.
/// </param>
/// <param name="Token">
/// Opaque token string. For <c>Basic</c>, callers must base64-encode
/// <c>username:password</c> themselves before constructing the header.
/// </param>
/// <param name="ExpiresAt">
/// When the credential stops working, or null if the credential does not
/// auto-expire.
/// </param>
public sealed record RegistryCredential(
    string RegistryId,
    string Scheme,
    string Token,
    DateTimeOffset? ExpiresAt);
