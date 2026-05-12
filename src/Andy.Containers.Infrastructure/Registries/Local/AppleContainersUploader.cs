using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Infrastructure.Registries.Local;

/// <summary>
/// Push images to a registry by shelling out to Apple's
/// <c>container</c> CLI. Used on macOS 26+ where
/// <see cref="BuildEngineDetector"/> selects Apple Containers ahead
/// of Docker BuildKit and the produced image lives in Apple
/// Containers' own image store — distinct from the Docker daemon's
/// cache, so <see cref="DockerCliUploader"/> cannot find it.
/// </summary>
/// <remarks>
/// P1F3 (rivoli-ai/andy-containers#276). Mirrors the
/// <see cref="DockerCliUploader"/> two-step retag-then-push shape:
/// <list type="number">
///   <item><c>container images tag &lt;local&gt; &lt;remote&gt;</c></item>
///   <item><c>container images push &lt;remote&gt;</c></item>
/// </list>
/// The digest is resolved authoritatively by
/// <see cref="LocalZotAdapter"/> via a post-push
/// <c>HEAD /v2/.../manifests/{tag}</c>; we never parse it out of the
/// CLI output.
/// </remarks>
public sealed class AppleContainersUploader : IRegistryUploader
{
    private readonly ILogger<AppleContainersUploader> _logger;
    private readonly AppleContainersUploaderOptions _options;

    public AppleContainersUploader(
        ILogger<AppleContainersUploader> logger,
        AppleContainersUploaderOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new AppleContainersUploaderOptions();
    }

    public async Task PushAsync(
        string localReference,
        string remoteReference,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteReference);

        // 1) Retag under the remote ref so the push target is
        //    unambiguous. Apple's `images tag` is idempotent — same
        //    contract as `docker tag`.
        await RunAsync(
            "AppleContainersUploader.Tag",
            new[] { "images", "tag", localReference, remoteReference },
            ct);

        // 2) Push by remote ref. ArgumentList bypasses Win32-style
        //    tokenising so any metacharacter in the spec-derived
        //    local tag can't smuggle extra `container` flags.
        await RunAsync(
            "AppleContainersUploader.Push",
            new[] { "images", "push", remoteReference },
            ct);
    }

    private async Task RunAsync(
        string operationCode,
        string[] arguments,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.ContainerExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        _logger.LogDebug(
            "{OpCode} starting container {Args}",
            operationCode, string.Join(' ', arguments));

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            // Most common cause: Apple `container` CLI not on PATH
            // (e.g., the engine detector picked Apple Containers but
            // the binary is stale or shadowed). Stable code so IM10
            // maps it to a 503 with an actionable message.
            throw new RegistryUploadException(
                code: $"{operationCode}.LaunchFailed",
                message: $"failed to launch '{_options.ContainerExecutablePath}' — is Apple's `container` CLI installed and on PATH? (macOS 26+)",
                innerException: ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var combined = stdout + (string.IsNullOrWhiteSpace(stderr) ? string.Empty : Environment.NewLine + stderr);
            throw new RegistryUploadException(
                code: $"{operationCode}.NonZeroExit{process.ExitCode}",
                message: $"container exited with code {process.ExitCode} during {operationCode.ToLowerInvariant()}: {Truncate(stderr, 200)}",
                capturedOutput: combined);
        }

        _logger.LogDebug("{OpCode} succeeded.", operationCode);
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// Configuration knobs for <see cref="AppleContainersUploader"/>.
/// Default resolves <c>container</c> from PATH; tests override the
/// path to point at a stub script.
/// </summary>
public sealed class AppleContainersUploaderOptions
{
    public string ContainerExecutablePath { get; init; } = "container";
}
