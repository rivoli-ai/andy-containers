using Andy.Containers.Models.ImageManagement;

namespace Andy.Containers.Abstractions.Images;

/// <summary>
/// Pulls an image from an upstream registry and rehosts it in a
/// local registry. Used by the
/// <c>POST /api/images/ensure-pull</c> endpoint (rivoli-ai/conductor#1014)
/// to seed Conductor's local zot with the
/// <c>conductor-terminal-*</c> tarballs published to a public
/// registry.
///
/// Distinct from <see cref="IRegistryUploader"/> — that pushes
/// locally-*built* bytes (the build engine just produced them); this
/// pulls bytes from elsewhere first. The two could share an
/// implementation eventually; today they're separate because the
/// upload path's <c>localReference</c> contract assumes the bytes
/// are already in the host build engine's cache, which isn't true
/// for the pull case.
/// </summary>
public interface IImagePullService
{
    /// <summary>
    /// Pull <paramref name="request"/>.SourceRegistry/Repository:Tag
    /// from upstream and push it into the registry identified by
    /// <paramref name="request"/>.DestinationRegistryId.
    /// </summary>
    /// <remarks>
    /// Idempotent: if the destination already holds the artifact at
    /// the requested coordinate the implementation MUST return
    /// without re-transferring bytes and set
    /// <see cref="EnsurePullResponse.AlreadyPresent"/> to <c>true</c>.
    /// </remarks>
    /// <exception cref="ImagePullException">
    /// Thrown when the upstream is unreachable, the source coordinate
    /// doesn't exist, or the push into the destination fails. The
    /// <see cref="ImagePullException.Code"/> distinguishes the
    /// failure modes per IM10's error-code contract.
    /// </exception>
    Task<EnsurePullResponse> EnsurePullAsync(
        EnsurePullRequest request,
        CancellationToken ct);
}

/// <summary>
/// Failure surfaced by <see cref="IImagePullService.EnsurePullAsync"/>.
/// </summary>
public sealed class ImagePullException : Exception
{
    /// <summary>
    /// Stable greppable code identifying the failure mode. Maps onto
    /// <c>ImageManagementError.code</c> in the API response per IM10.
    /// </summary>
    public string Code { get; }

    /// <summary>Captured stdout/stderr from the underlying pull process, if any.</summary>
    public string? CapturedOutput { get; }

    public ImagePullException(
        string code,
        string message,
        string? capturedOutput = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        CapturedOutput = capturedOutput;
    }
}
