namespace Andy.Containers.Models.ImageManagement;

/// <summary>
/// Persisted pointer from a registry-and-tag location back to a
/// <see cref="BuildArtifactEntity"/>. Many references can map to a
/// single artifact (same image pushed to multiple registries, or
/// tagged multiple ways in the same registry). The composite unique
/// constraint <c>(RegistryId, RepoPath, Tag)</c> prevents conflicting
/// rows for the same registry coordinate.
/// </summary>
/// <remarks>
/// Named with the <c>Entity</c> suffix to disambiguate from
/// <c>Andy.Containers.Abstractions.Images.RegistryReference</c>
/// (the transport record). The repository maps between the two at the
/// storage boundary.
/// </remarks>
public class RegistryReferenceEntity
{
    public Guid Id { get; set; }

    /// <summary>Owning artifact.</summary>
    public Guid BuildArtifactId { get; set; }
    public BuildArtifactEntity? BuildArtifact { get; set; }

    /// <summary>
    /// Registry identifier matching the corresponding
    /// <c>RegistryConfigEntry.Id</c> in
    /// <c>RegistryConfigurationOptions</c> and the corresponding
    /// <c>IRegistryAdapter.RegistryId</c>.
    /// </summary>
    public required string RegistryId { get; set; }

    /// <summary>
    /// Repo path within the registry (e.g.
    /// <c>conductor-terminal-claude-code</c>).
    /// </summary>
    public required string RepoPath { get; set; }

    /// <summary>Tag under the repo path (e.g. <c>sha256-abc12345</c>).</summary>
    public required string Tag { get; set; }

    /// <summary>When this reference was published to the registry.</summary>
    public DateTime PushedAt { get; set; }

    /// <summary>Principal that pushed (user id or service identity).</summary>
    public required string PushedBy { get; set; }
}
