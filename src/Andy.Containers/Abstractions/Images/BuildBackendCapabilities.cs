namespace Andy.Containers.Abstractions.Images;

/// <summary>
/// Self-describing capability set for an <see cref="IBuildBackend"/>.
/// Lets the orchestrator pick a compatible backend for a given spec.
/// </summary>
/// <param name="SupportsMultiArch">
/// True if the backend can produce a multi-architecture manifest list
/// in one build (e.g. Docker BuildKit with QEMU, ACR Tasks
/// <c>--platform</c>, Cloud Build with multiple steps).
/// </param>
/// <param name="SupportedArchitectures">
/// Architecture identifiers the backend can target, in OCI format
/// (<c>amd64</c>, <c>arm64</c>, etc.). Empty list means "host only".
/// </param>
/// <param name="SupportsCacheImport">
/// True if the backend honours <c>--cache-from</c> / equivalent to
/// reuse layers from a prior build.
/// </param>
/// <param name="SupportsRemoteContext">
/// True if the backend can fetch a build context from a remote URL
/// (e.g. git repo) instead of requiring all files to be uploaded.
/// </param>
/// <param name="SupportsSecrets">
/// True if the backend can mount build-time secrets that don't end up
/// in the resulting image (e.g. <c>--secret</c> in BuildKit).
/// </param>
public sealed record BuildBackendCapabilities(
    bool SupportsMultiArch,
    IReadOnlyList<string> SupportedArchitectures,
    bool SupportsCacheImport,
    bool SupportsRemoteContext,
    bool SupportsSecrets);
