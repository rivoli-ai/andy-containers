using Andy.Containers.Abstractions.Images;
using Andy.Containers.Models.ImageManagement;

namespace Andy.Containers.Storage;

/// <summary>
/// Mappers between persisted EF entities and the abstraction-layer
/// records returned to consumers of <c>IRegistryAdapter</c> /
/// <c>IBuildBackend</c>. Kept separate from the entities and the
/// abstraction records so the two layers can evolve independently.
/// </summary>
public static class BuildArtifactMappers
{
    /// <summary>
    /// Project an EF entity onto the abstraction-layer record. The
    /// <see cref="BuildArtifact.LocalReference"/> field has no
    /// persisted equivalent — it's a build-time hint between the
    /// build backend and the registry adapter — so it's left empty
    /// when reading from the DB.
    /// </summary>
    public static BuildArtifact ToAbstraction(this BuildArtifactEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new BuildArtifact(
            Digest: entity.Digest,
            MediaType: entity.MediaType,
            SizeBytes: entity.SizeBytes,
            SpecHash: entity.SpecHash,
            LocalReference: string.Empty);
    }

    /// <summary>
    /// Build a fresh EF entity from an abstraction-layer record plus
    /// the storage-only fields. Sets <c>BuiltAt</c> to the current
    /// time when not supplied; callers in tests can pass an explicit
    /// timestamp via <paramref name="builtAt"/>.
    /// </summary>
    public static BuildArtifactEntity ToEntity(
        this BuildArtifact artifact,
        Guid templateId,
        string buildBackendId,
        string builtBy,
        DateTime? builtAt = null,
        string? buildLog = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return new BuildArtifactEntity
        {
            Id = Guid.NewGuid(),
            Digest = artifact.Digest,
            MediaType = artifact.MediaType,
            SizeBytes = artifact.SizeBytes,
            SpecHash = artifact.SpecHash,
            TemplateId = templateId,
            BuildBackendId = buildBackendId,
            BuiltBy = builtBy,
            BuiltAt = builtAt ?? DateTime.UtcNow,
            BuildLog = buildLog,
        };
    }

    /// <summary>
    /// Project a reference row onto the abstraction-layer record.
    /// </summary>
    public static RegistryReference ToAbstraction(this RegistryReferenceEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new RegistryReference(
            Id: entity.Id,
            RegistryId: entity.RegistryId,
            RepoPath: entity.RepoPath,
            Tag: entity.Tag,
            Digest: entity.BuildArtifact?.Digest ?? string.Empty,
            PushedAt: new DateTimeOffset(entity.PushedAt, TimeSpan.Zero),
            PushedBy: entity.PushedBy);
    }
}
