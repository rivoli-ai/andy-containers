using Andy.Containers.Abstractions.Images;

namespace Andy.Containers.Storage;

/// <summary>
/// Owns the cache-check → build → push → persist flow for a single
/// image build request. Sits above <see cref="IBuildBackend"/>,
/// <see cref="IRegistryAdapter"/>, and <see cref="IBuildArtifactStore"/>;
/// the API controller delegates to this service so the
/// orchestration logic lives in one place.
/// </summary>
/// <remarks>
/// IM8 (rivoli-ai/andy-containers#262). The cache short-circuit is
/// the critical correctness contract: same spec hash + existing
/// reference in the target registry ⇒ <see cref="BuildResultStatus.Cached"/>
/// with no rebuild and no new DB row. Anything else falls through
/// to a real build via the backend.
/// </remarks>
public interface IImageBuildOrchestrator
{
    Task<BuildResult> BuildAsync(
        ImageBuildRequest request,
        IProgress<BuildProgressEvent> progress,
        CancellationToken ct);

    /// <summary>
    /// Cache-only fast path. Returns a
    /// <see cref="BuildResultStatus.Cached"/> result when an
    /// existing artifact + reference satisfies the request; null
    /// otherwise. Never invokes the build backend.
    /// </summary>
    /// <remarks>
    /// IM9 (rivoli-ai/andy-containers#263). The async executor uses
    /// this to decide between the synchronous (cached) and
    /// background (build) paths without duplicating the cache
    /// lookup logic.
    /// </remarks>
    Task<BuildResult?> TryCacheHitAsync(ImageBuildRequest request, CancellationToken ct);
}

/// <summary>
/// Inputs for a build invocation.
/// </summary>
/// <param name="TemplateId">Template to build.</param>
/// <param name="RegistryId">
/// Override the primary push registry. When null, the orchestrator
/// uses <see cref="IRegistryConfiguration.PrimaryRegistryId"/>.
/// </param>
/// <param name="Force">
/// Bypass the content-addressable cache and rebuild from scratch.
/// Useful when the spec is unchanged but external state has shifted
/// (for example a base image was re-tagged upstream).
/// </param>
/// <param name="RequestedBy">
/// Principal that triggered the build (user id or service identity).
/// Recorded on <see cref="BuildArtifactEntity.BuiltBy"/> /
/// <see cref="RegistryReferenceEntity.PushedBy"/>.
/// </param>
public sealed record ImageBuildRequest(
    Guid TemplateId,
    string? RegistryId,
    bool Force,
    string RequestedBy);

/// <summary>
/// Outcome of a build attempt.
/// </summary>
public sealed record BuildResult
{
    public required Guid BuildId { get; init; }
    public required BuildResultStatus Status { get; init; }

    /// <summary>OCI digest, populated on <see cref="BuildResultStatus.Cached"/> and <see cref="BuildResultStatus.Succeeded"/>.</summary>
    public string? Digest { get; init; }

    /// <summary>References created or already-present in the target registry.</summary>
    public IReadOnlyList<BuildResultReference> References { get; init; } = [];

    /// <summary>Stable error code on <see cref="BuildResultStatus.Failed"/> — maps to the API response in IM10.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable message on <see cref="BuildResultStatus.Failed"/>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Captured logs on <see cref="BuildResultStatus.Failed"/> (truncated by the API at the response boundary).</summary>
    public string? FailureLog { get; init; }
}

/// <summary>
/// Where the artifact lives in a registry. Mirrors
/// <see cref="RegistryReferenceEntity"/> but flattened for transport.
/// </summary>
public sealed record BuildResultReference(
    string RegistryId,
    string RepoPath,
    string Tag,
    DateTimeOffset PushedAt);

public enum BuildResultStatus
{
    /// <summary>Existing artifact returned without rebuilding.</summary>
    Cached,
    /// <summary>New artifact built and pushed.</summary>
    Succeeded,
    /// <summary>Build or push failed; <see cref="BuildResult.ErrorCode"/> identifies the failure mode.</summary>
    Failed,
}
