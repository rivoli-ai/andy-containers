using Andy.Containers.Infrastructure.Build;

namespace Andy.Containers.Infrastructure.Registries.Local;

/// <summary>
/// Routes <see cref="IRegistryUploader.PushAsync"/> calls to the
/// uploader matching the host's detected build engine. Production
/// composition root for the embedded-mode push path so the
/// orchestrator never has to know which engine is in play.
/// </summary>
/// <remarks>
/// P1F3 (rivoli-ai/andy-containers#276). Engine detection is async
/// and one-shot per process; we cache the choice after the first
/// PushAsync so subsequent pushes don't re-probe.
/// <para>
/// <see cref="BuildEngineKind.None"/> is treated as an explicit
/// configuration error (build never ran or detector returned an
/// indeterminate result). <see cref="BuildEngineKind.DockerBuildKit"/>
/// dispatches to <see cref="DockerCliUploader"/>;
/// <see cref="BuildEngineKind.AppleContainers"/> to
/// <see cref="AppleContainersUploader"/>.
/// </para>
/// </remarks>
public sealed class EngineAwareRegistryUploader : IRegistryUploader
{
    private readonly IBuildEngineDetector _detector;
    private readonly DockerCliUploader _docker;
    private readonly AppleContainersUploader _apple;

    private IRegistryUploader? _resolved;
    private readonly SemaphoreSlim _resolveGate = new(1, 1);

    public EngineAwareRegistryUploader(
        IBuildEngineDetector detector,
        DockerCliUploader docker,
        AppleContainersUploader apple)
    {
        _detector = detector;
        _docker = docker;
        _apple = apple;
    }

    public async Task PushAsync(
        string localReference,
        string remoteReference,
        CancellationToken ct)
    {
        var inner = await ResolveAsync(ct).ConfigureAwait(false);
        await inner.PushAsync(localReference, remoteReference, ct).ConfigureAwait(false);
    }

    private async Task<IRegistryUploader> ResolveAsync(CancellationToken ct)
    {
        if (_resolved is not null)
        {
            return _resolved;
        }

        await _resolveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_resolved is not null)
            {
                return _resolved;
            }

            var engine = await _detector.DetectAsync(ct).ConfigureAwait(false);
            _resolved = engine.Kind switch
            {
                BuildEngineKind.DockerBuildKit => _docker,
                BuildEngineKind.AppleContainers => _apple,
                BuildEngineKind.None => throw new RegistryUploadException(
                    code: "EngineAwareRegistryUploader.NoEngine",
                    message: "no container build engine detected on host; cannot push — install Apple Containers (macOS 26+) or Docker Desktop."),
                _ => throw new RegistryUploadException(
                    code: "EngineAwareRegistryUploader.UnknownEngine",
                    message: $"detected engine kind '{engine.Kind}' has no IRegistryUploader implementation."),
            };
            return _resolved;
        }
        finally
        {
            _resolveGate.Release();
        }
    }
}
