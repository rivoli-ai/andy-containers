namespace Andy.Containers.Abstractions.Images;

/// <summary>
/// Per-registry I/O. Concrete implementations cover an OCI-conformant
/// registry (zot, Artifactory, ACR, ECR, Harbor, GAR). The default OCI
/// Distribution v1.1 surface lives here; vendor-specific extensions
/// (lifecycle, scanning, signing-policy) live on subinterfaces and are
/// added as needed.
/// </summary>
public interface IRegistryAdapter
{
    /// <summary>
    /// Stable identifier matching the <see cref="RegistryConfigEntry.Id"/>
    /// this adapter handles.
    /// </summary>
    string RegistryId { get; }

    /// <summary>
    /// Push a built artifact into this registry under the given repo path
    /// and tag. Implementations transfer the bytes referenced by
    /// <see cref="BuildArtifact.LocalReference"/> to the remote registry
    /// and return the resulting <see cref="RegistryReference"/>.
    /// </summary>
    Task<RegistryReference> PushAsync(
        BuildArtifact artifact,
        string repoPath,
        string tag,
        CancellationToken ct);

    /// <summary>
    /// Check whether a manifest with the given digest already exists
    /// under the given repo path. Used for content-addressable cache
    /// hits — if the spec hash already resolved to a digest in this
    /// registry, the build can be skipped.
    /// </summary>
    Task<bool> ExistsAsync(string repoPath, string digest, CancellationToken ct);

    /// <summary>
    /// List all references currently stored under a repo path.
    /// </summary>
    Task<IReadOnlyList<RegistryReference>> ListReferencesAsync(
        string repoPath,
        CancellationToken ct);

    /// <summary>
    /// Untag a reference. Does not delete the underlying artifact bytes —
    /// registry-side garbage collection reclaims those when no reference
    /// points at the digest.
    /// </summary>
    Task DeleteAsync(RegistryReference reference, CancellationToken ct);
}
