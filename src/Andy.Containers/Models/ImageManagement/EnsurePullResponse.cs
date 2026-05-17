namespace Andy.Containers.Models.ImageManagement;

/// <summary>
/// Response payload for <c>POST /api/images/ensure-pull</c>.
/// rivoli-ai/conductor#1014. Reports whether the operation
/// re-used an existing artifact or actually pulled bytes, plus
/// the resulting registry reference.
/// </summary>
public sealed class EnsurePullResponse
{
    /// <summary>
    /// True when the destination already had the requested artifact
    /// and no bytes were transferred. False when the puller actually
    /// pulled from the upstream and pushed into the destination.
    /// Conductor uses this to keep its progress UI honest (a no-op
    /// pull doesn't deserve a "Pulled X" toast).
    /// </summary>
    public required bool AlreadyPresent { get; init; }

    /// <summary>
    /// Destination registry id this artifact now lives in.
    /// </summary>
    public required string RegistryId { get; init; }

    /// <summary>
    /// Destination repo path (e.g. <c>conductor-terminal-claude-code</c>).
    /// </summary>
    public required string RepoPath { get; init; }

    /// <summary>
    /// Destination tag (e.g. <c>v1</c>).
    /// </summary>
    public required string Tag { get; init; }

    /// <summary>
    /// OCI manifest digest of the artifact after the push, e.g.
    /// <c>sha256:abc123...</c>. Authoritative — clients should
    /// trust this over the tag for pin-by-digest semantics.
    /// </summary>
    public required string Digest { get; init; }

    /// <summary>
    /// Total uncompressed manifest size in bytes, as reported by
    /// the destination registry's <c>HEAD /v2/.../manifests/{tag}</c>
    /// response. Surfaced so the UI can render a sensible progress
    /// estimate the next time the same upstream is pulled.
    /// </summary>
    public required long SizeBytes { get; init; }
}
