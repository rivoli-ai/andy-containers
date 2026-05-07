using Andy.Containers.Abstractions.Images;

namespace Andy.Containers.Infrastructure.Registries;

/// <summary>
/// Pushes the bytes for a built image from the build engine's local
/// cache into a remote registry. Concrete implementations cover
/// "shell out to docker push" (the embedded mode), "use the build
/// engine's own push" (Apple Containers), or "no-op because the
/// build pipeline already pushed" (cloud build backends).
/// </summary>
/// <remarks>
/// IM6 (rivoli-ai/andy-containers#260). Split from <see cref="IRegistryAdapter"/>
/// so the registry adapter can stay focused on the OCI Distribution
/// v1.1 read/check surface and the *push* mechanics — which depend on
/// the build engine — live in a substitutable component.
/// </remarks>
public interface IRegistryUploader
{
    /// <summary>
    /// Push a locally-built image to the remote registry. The
    /// <paramref name="localReference"/> must point at an image the
    /// host's build engine can resolve (e.g. the Docker daemon's
    /// local cache); the <paramref name="remoteReference"/> is the
    /// fully qualified target (e.g.
    /// <c>localhost:5050/conductor-terminal-claude-code:sha256-abc</c>).
    /// </summary>
    /// <remarks>
    /// The uploader does not return a digest — parsing it out of
    /// CLI output is fragile across engines. The registry adapter
    /// reads the digest authoritatively via a post-push
    /// <c>HEAD /v2/.../manifests/{tag}</c> against the registry's
    /// HTTP API, where the <c>Docker-Content-Digest</c> response
    /// header is the contract.
    /// </remarks>
    /// <exception cref="RegistryUploadException">
    /// Thrown when the underlying push fails (network, auth,
    /// quota). Carries a stable <see cref="RegistryUploadException.Code"/>
    /// for the API error mapping in IM10.
    /// </exception>
    Task PushAsync(
        string localReference,
        string remoteReference,
        CancellationToken ct);
}

/// <summary>
/// Failure surfaced by <see cref="IRegistryUploader.PushAsync"/>.
/// </summary>
public sealed class RegistryUploadException : Exception
{
    /// <summary>
    /// Stable greppable code identifying the failure mode. Maps onto
    /// <c>ImageManagementError.code</c> in the API response per
    /// IM10.
    /// </summary>
    public string Code { get; }

    /// <summary>Captured stdout/stderr from the underlying push process, if any.</summary>
    public string? CapturedOutput { get; }

    public RegistryUploadException(
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
