namespace Andy.Containers.Abstractions.Images;

/// <summary>
/// Per-engine image-building. Concrete implementations cover the local
/// Docker daemon, Apple Containers, ACR Tasks, Cloud Build, CodeBuild,
/// or BuildKit-on-cluster. Each backend converts a parsed
/// <see cref="TemplateSpec"/> into a <see cref="BuildArtifact"/>; the
/// registry adapter then pushes the artifact to its configured registry.
/// </summary>
public interface IBuildBackend
{
    /// <summary>
    /// Stable identifier matching the build backend selected at startup
    /// (e.g. <c>local-docker</c>, <c>apple-containers</c>,
    /// <c>acr-tasks</c>).
    /// </summary>
    string BackendId { get; }

    /// <summary>
    /// Self-described capabilities. Lets the orchestrator pick a
    /// compatible backend for a given spec (e.g. multi-arch builds need
    /// a backend with <see cref="BuildBackendCapabilities.SupportsMultiArch"/>).
    /// </summary>
    BuildBackendCapabilities Capabilities { get; }

    /// <summary>
    /// Build an image from the parsed spec, surfacing progress through
    /// <paramref name="progress"/>. Returns the resulting artifact on
    /// success.
    /// </summary>
    /// <exception cref="ImageBuildFailedException">
    /// Thrown when the build engine reports a non-zero exit. The
    /// exception carries captured logs for the API to surface as a 422.
    /// </exception>
    Task<BuildArtifact> BuildAsync(
        TemplateSpec spec,
        IBuildContext context,
        IProgress<BuildProgressEvent> progress,
        CancellationToken ct);
}
