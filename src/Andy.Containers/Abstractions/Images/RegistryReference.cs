namespace Andy.Containers.Abstractions.Images;

/// <summary>
/// A pointer to a built artifact in a specific registry under a specific
/// repo path and tag. Many <see cref="RegistryReference"/>s can map to a
/// single <see cref="BuildArtifact"/> (same image pushed to multiple
/// registries, or tagged multiple ways in the same registry).
/// </summary>
/// <param name="Id">Stable identifier for this reference row.</param>
/// <param name="RegistryId">
/// The id of the registry in <see cref="IRegistryConfiguration"/> this
/// reference was pushed to.
/// </param>
/// <param name="RepoPath">
/// Repo path within the registry, e.g.
/// <c>conductor-terminal-claude-code</c>.
/// </param>
/// <param name="Tag">
/// Tag under the repo path, e.g. <c>sha256-abc12345</c> or <c>v1.2.3</c>.
/// </param>
/// <param name="Digest">
/// OCI manifest digest the tag currently resolves to. Same as
/// <see cref="BuildArtifact.Digest"/> at push time. Tags are mutable;
/// the digest is authoritative.
/// </param>
/// <param name="PushedAt">When this reference was published to the registry.</param>
/// <param name="PushedBy">
/// Identifier of the principal that pushed (user id or service identity).
/// </param>
public sealed record RegistryReference(
    Guid Id,
    string RegistryId,
    string RepoPath,
    string Tag,
    string Digest,
    DateTimeOffset PushedAt,
    string PushedBy);
