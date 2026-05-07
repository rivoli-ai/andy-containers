using System.Diagnostics;
using Andy.Containers.Abstractions.Images;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Infrastructure.Build.Local;

/// <summary>
/// First concrete <see cref="IBuildBackend"/>. Builds images via the
/// host's container engine — Apple Containers (preferred where
/// available) or Docker BuildKit (fallback). Produces a
/// <see cref="BuildArtifact"/> whose <see cref="BuildArtifact.LocalReference"/>
/// is a tag in the engine's local cache; the registry adapter
/// (IM6 — <c>LocalZotAdapter</c>) pushes from that tag.
/// </summary>
/// <remarks>
/// IM7 (rivoli-ai/andy-containers#261). The build flow is:
/// detect engine → render Dockerfile from spec → stage build context
/// in a temp directory → invoke engine as a child process →
/// stream output as <see cref="BuildProgressEvent"/>s →
/// return <see cref="BuildArtifact"/> on success.
/// </remarks>
public sealed class LocalBuildBackend : IBuildBackend
{
    private readonly IBuildEngineDetector _detector;
    private readonly ILogger<LocalBuildBackend> _logger;
    private readonly LocalBuildBackendOptions _options;

    public LocalBuildBackend(
        IBuildEngineDetector detector,
        ILogger<LocalBuildBackend> logger,
        LocalBuildBackendOptions? options = null)
    {
        _detector = detector;
        _logger = logger;
        _options = options ?? new LocalBuildBackendOptions();
    }

    public string BackendId => "local";

    /// <summary>
    /// Honest declaration. Multi-arch support depends on the engine
    /// (BuildKit + QEMU yes; Apple Containers cross-compile yes) but
    /// requires extra flags we don't yet pass — IM7 ships single-arch
    /// builds against the host architecture.
    /// </summary>
    public BuildBackendCapabilities Capabilities => new(
        SupportsMultiArch: false,
        SupportedArchitectures: [System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()],
        SupportsCacheImport: true,
        SupportsRemoteContext: false,
        SupportsSecrets: false);

    public async Task<BuildArtifact> BuildAsync(
        TemplateSpec spec,
        IBuildContext context,
        IProgress<BuildProgressEvent> progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);

        var engine = await _detector.DetectAsync(ct);
        if (engine.Kind == BuildEngineKind.None)
        {
            throw new ImageBuildFailedException(
                backendId: BackendId,
                capturedLogs: "no build engine detected on host",
                specHash: spec.SpecHash,
                failingStepName: "engine-detect",
                message: "no container build engine is available — install Apple Containers (macOS 26+) or Docker Desktop, then restart andy-containers.");
        }

        var localTag = $"andy-containers-build-{Guid.NewGuid():N}";
        var contextDir = await StageBuildContextAsync(spec, context, ct);

        try
        {
            var startEvent = new BuildStepStartedEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                StepName = "build",
                StepIndex = 1,
                TotalSteps = 1,
            };
            progress.Report(startEvent);

            await InvokeEngineAsync(engine, contextDir, localTag, spec, progress, ct);

            progress.Report(new BuildCompletedEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                Outcome = BuildOutcome.Succeeded,
            });

            return new BuildArtifact(
                Digest: string.Empty, // resolved by the registry adapter post-push
                MediaType: "application/vnd.oci.image.manifest.v1+json",
                SizeBytes: 0L,        // optional — the registry HEAD response carries authoritative size
                SpecHash: spec.SpecHash,
                LocalReference: localTag);
        }
        catch (ImageBuildFailedException)
        {
            progress.Report(new BuildCompletedEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                Outcome = BuildOutcome.Failed,
            });
            throw;
        }
        catch (OperationCanceledException)
        {
            progress.Report(new BuildCompletedEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                Outcome = BuildOutcome.Cancelled,
            });
            throw;
        }
        finally
        {
            if (!_options.PreserveBuildContext)
            {
                try { Directory.Delete(contextDir, recursive: true); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to clean up build context directory {Dir}.",
                        contextDir);
                }
            }
        }
    }

    private async Task<string> StageBuildContextAsync(
        TemplateSpec spec,
        IBuildContext context,
        CancellationToken ct)
    {
        var contextDir = Path.Combine(
            _options.BuildContextRoot ?? Path.GetTempPath(),
            $"andy-containers-build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contextDir);

        // Copy each uploaded file into the context directory under
        // its LogicalName. The Dockerfile renderer references files
        // by Source (== LogicalName), so this lines up.
        foreach (var file in context.Files)
        {
            var dest = Path.Combine(contextDir, file.LogicalName);
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }
            File.Copy(file.AbsolutePath, dest, overwrite: true);
        }

        // Write the Dockerfile.
        var dockerfile = DockerfileBuilder.Render(spec);
        var dockerfilePath = Path.Combine(contextDir, "Dockerfile");
        await File.WriteAllTextAsync(dockerfilePath, dockerfile, ct);

        return contextDir;
    }

    private async Task InvokeEngineAsync(
        DetectedBuildEngine engine,
        string contextDir,
        string localTag,
        TemplateSpec spec,
        IProgress<BuildProgressEvent> progress,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = engine.ExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = contextDir,
        };

        // Engine-specific argument shapes. Both consume Dockerfile
        // syntax via -f, both accept -t for the local tag, both
        // accept the context directory as the trailing positional.
        switch (engine.Kind)
        {
            case BuildEngineKind.AppleContainers:
                psi.ArgumentList.Add("build");
                psi.ArgumentList.Add("-t");
                psi.ArgumentList.Add(localTag);
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("Dockerfile");
                psi.ArgumentList.Add(".");
                break;
            case BuildEngineKind.DockerBuildKit:
                psi.ArgumentList.Add("buildx");
                psi.ArgumentList.Add("build");
                psi.ArgumentList.Add("--load"); // load result into local cache so the registry uploader can find it
                psi.ArgumentList.Add("-t");
                psi.ArgumentList.Add(localTag);
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("Dockerfile");
                psi.ArgumentList.Add(".");
                break;
            default:
                throw new InvalidOperationException($"Unsupported engine kind '{engine.Kind}'.");
        }

        _logger.LogInformation(
            "LocalBuildBackend invoking {Engine} with args [{Args}] in {Dir}",
            engine.Kind, string.Join(' ', psi.ArgumentList), contextDir);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new ImageBuildFailedException(
                backendId: BackendId,
                capturedLogs: ex.Message,
                specHash: spec.SpecHash,
                failingStepName: "engine-launch",
                message: $"failed to launch {engine.Kind} at '{engine.ExecutablePath}': {ex.Message}",
                innerException: ex);
        }

        // Stream stdout and stderr line-by-line, surfacing each as a
        // BuildStepStdoutEvent. Smarter step-boundary parsing can land
        // as a follow-up; for IM7 every output line counts as part of
        // the single 'build' step.
        var capturedLogs = new System.Text.StringBuilder();
        var stdoutTask = StreamLinesAsync(process.StandardOutput, "build", progress, capturedLogs, ct);
        var stderrTask = StreamLinesAsync(process.StandardError, "build", progress, capturedLogs, ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw;
        }

        await stdoutTask;
        await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new ImageBuildFailedException(
                backendId: BackendId,
                capturedLogs: capturedLogs.ToString(),
                specHash: spec.SpecHash,
                failingStepName: "build",
                message: $"{engine.Kind} build exited with code {process.ExitCode}.");
        }
    }

    private static async Task StreamLinesAsync(
        StreamReader reader,
        string stepName,
        IProgress<BuildProgressEvent> progress,
        System.Text.StringBuilder capture,
        CancellationToken ct)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            capture.AppendLine(line);
            progress.Report(new BuildStepStdoutEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                StepName = stepName,
                Line = line,
            });
        }
    }
}

/// <summary>
/// Configuration knobs for <see cref="LocalBuildBackend"/>.
/// </summary>
public sealed class LocalBuildBackendOptions
{
    /// <summary>
    /// Override the temp-dir root where build contexts are staged.
    /// Defaults to <see cref="Path.GetTempPath"/>.
    /// </summary>
    public string? BuildContextRoot { get; init; }

    /// <summary>
    /// When true, build context directories are kept on disk after
    /// the build. Useful for debugging; default false.
    /// </summary>
    public bool PreserveBuildContext { get; init; }
}
