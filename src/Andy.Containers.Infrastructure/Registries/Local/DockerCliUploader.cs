using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Infrastructure.Registries.Local;

/// <summary>
/// Push images to a registry by shelling out to the Docker CLI.
/// Production implementation of <see cref="IRegistryUploader"/>
/// for the embedded mode where the build engine and the registry
/// are both on the user's machine.
/// </summary>
/// <remarks>
/// IM6 (rivoli-ai/andy-containers#260). The Docker daemon already
/// has the locally-built image after a build (per IM7); this uploader
/// re-tags it under the registry's hostname and runs <c>docker push</c>.
/// The actual digest is read by <see cref="LocalZotAdapter"/> via a
/// post-push HEAD against the registry's HTTP API — parsing it out
/// of <c>docker push</c> stderr is brittle across CLI versions.
/// </remarks>
public sealed class DockerCliUploader : IRegistryUploader
{
    private readonly ILogger<DockerCliUploader> _logger;
    private readonly DockerCliUploaderOptions _options;

    public DockerCliUploader(
        ILogger<DockerCliUploader> logger,
        DockerCliUploaderOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new DockerCliUploaderOptions();
    }

    public async Task PushAsync(
        string localReference,
        string remoteReference,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteReference);

        // 1) Tag the local image under the remote ref so the push
        //    target is unambiguous. `docker tag` is idempotent — if
        //    the tag already exists it's overwritten without error.
        await RunAsync(
            "DockerCliUploader.Tag",
            new[] { "tag", localReference, remoteReference },
            ct);

        // 2) Push. ProcessStartInfo.ArgumentList bypasses the
        //    Win32-style tokeniser so any shell metacharacter that
        //    snuck into the spec or the build-time tag generator
        //    can't smuggle extra docker flags.
        await RunAsync(
            "DockerCliUploader.Push",
            new[] { "push", remoteReference },
            ct);
    }

    private async Task RunAsync(
        string operationCode,
        string[] arguments,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.DockerExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        _logger.LogDebug(
            "{OpCode} starting docker {Args}",
            operationCode, string.Join(' ', arguments));

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            // The most common cause is "docker not on PATH"; surface
            // that with a clear code so IM10 maps it to a 503 with
            // an actionable message.
            throw new RegistryUploadException(
                code: $"{operationCode}.LaunchFailed",
                message: $"failed to launch '{_options.DockerExecutablePath}' — is the Docker CLI installed and on PATH?",
                innerException: ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            // Stderr from `docker push` carries the registry's error
            // body (auth, quota, network). Pass it through verbatim
            // so the API caller can surface the cause; truncation is
            // applied at the response boundary in IM10.
            var combined = stdout + (string.IsNullOrWhiteSpace(stderr) ? string.Empty : Environment.NewLine + stderr);
            throw new RegistryUploadException(
                code: $"{operationCode}.NonZeroExit{process.ExitCode}",
                message: $"docker exited with code {process.ExitCode} during {operationCode.ToLowerInvariant()}: {Truncate(stderr, 200)}",
                capturedOutput: combined);
        }

        _logger.LogDebug("{OpCode} succeeded.", operationCode);
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// Configuration knobs for <see cref="DockerCliUploader"/>. Default
/// resolves <c>docker</c> from PATH; tests override the executable
/// path to point at a stub script.
/// </summary>
public sealed class DockerCliUploaderOptions
{
    public string DockerExecutablePath { get; init; } = "docker";
}
