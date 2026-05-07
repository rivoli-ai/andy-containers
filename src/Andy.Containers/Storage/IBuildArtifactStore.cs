using Andy.Containers.Models.ImageManagement;

namespace Andy.Containers.Storage;

/// <summary>
/// Storage port for digest-anchored image artifacts. The default
/// implementation lives in <c>Andy.Containers.Infrastructure</c>
/// against the shared EF Core DbContext; tests can substitute an
/// in-memory implementation.
/// </summary>
public interface IBuildArtifactStore
{
    /// <summary>
    /// Look up an artifact by its OCI manifest digest. Returns null
    /// when no artifact has been recorded for the digest.
    /// </summary>
    Task<BuildArtifactEntity?> GetByDigestAsync(string digest, CancellationToken ct);

    /// <summary>
    /// Look up an artifact by template + spec-hash. Used for
    /// content-addressable cache hits — if the same template + spec
    /// has already been built, return the existing artifact instead
    /// of rebuilding.
    /// </summary>
    Task<BuildArtifactEntity?> GetBySpecHashAsync(
        Guid templateId,
        string specHash,
        CancellationToken ct);

    /// <summary>
    /// Persist a new artifact row. Throws if an artifact with the
    /// same <see cref="BuildArtifactEntity.Digest"/> already exists —
    /// callers should hit <see cref="GetByDigestAsync"/> first when
    /// implementing idempotent push.
    /// </summary>
    Task<BuildArtifactEntity> AddAsync(
        BuildArtifactEntity artifact,
        CancellationToken ct);

    /// <summary>
    /// Add a registry reference pointing at an existing artifact.
    /// The artifact must already be persisted; pass its
    /// <see cref="BuildArtifactEntity.Id"/> as
    /// <paramref name="artifactId"/>. The composite unique constraint
    /// on <c>(RegistryId, RepoPath, Tag)</c> rejects conflicting rows.
    /// </summary>
    Task<RegistryReferenceEntity> AddReferenceAsync(
        Guid artifactId,
        RegistryReferenceEntity reference,
        CancellationToken ct);

    /// <summary>
    /// Remove a registry reference. Does not delete the artifact —
    /// other references may still point at it, and registry-side GC
    /// is responsible for the underlying bytes.
    /// </summary>
    Task RemoveReferenceAsync(Guid referenceId, CancellationToken ct);

    /// <summary>
    /// List references for an artifact. Useful for the
    /// <c>GET /api/images/{digest}</c> endpoint, which surfaces every
    /// place the artifact has been pushed.
    /// </summary>
    Task<IReadOnlyList<RegistryReferenceEntity>> ListReferencesAsync(
        Guid artifactId,
        CancellationToken ct);
}
