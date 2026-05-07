namespace Andy.Containers.Infrastructure.Build;

/// <summary>
/// Detects which container build engine is available on the host.
/// Result is cached for the process lifetime — engines don't appear
/// or disappear during the API server's lifetime in practice, and
/// re-probing on every build adds latency.
/// </summary>
/// <remarks>
/// IM7 (rivoli-ai/andy-containers#261). Detection order matches the
/// IM1 architecture memo: Apple Containers preferred where available
/// (macOS 26+), Docker BuildKit as the fallback. When neither is
/// found <c>LocalBuildBackend.Capabilities</c> reflects an unusable
/// state and the API surfaces a 503 (mapped in IM10).
/// </remarks>
public interface IBuildEngineDetector
{
    Task<DetectedBuildEngine> DetectAsync(CancellationToken ct);
}

/// <summary>
/// Result of probing the host for a build engine.
/// </summary>
/// <param name="Kind">Which engine was found, if any.</param>
/// <param name="ExecutablePath">
/// Resolved executable path for the chosen engine. Empty when
/// <paramref name="Kind"/> is <see cref="BuildEngineKind.None"/>.
/// </param>
/// <param name="ProbedVersion">
/// Engine version string captured from the probe (the first line of
/// <c>--version</c> output, trimmed). Useful for diagnostics; not
/// inspected by the build backend.
/// </param>
public sealed record DetectedBuildEngine(
    BuildEngineKind Kind,
    string ExecutablePath,
    string ProbedVersion);

public enum BuildEngineKind
{
    /// <summary>No build engine on the host.</summary>
    None,
    /// <summary>Apple's <c>container</c> CLI (macOS 26+).</summary>
    AppleContainers,
    /// <summary>Docker BuildKit via <c>docker buildx</c>.</summary>
    DockerBuildKit,
}
