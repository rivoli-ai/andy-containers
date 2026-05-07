using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Infrastructure.Build;

/// <summary>
/// Production <see cref="IBuildEngineDetector"/>. Probes Apple
/// Containers first, then Docker BuildKit, caches the result.
/// </summary>
public sealed class BuildEngineDetector : IBuildEngineDetector
{
    private readonly ILogger<BuildEngineDetector> _logger;
    private readonly BuildEngineDetectorOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DetectedBuildEngine? _cached;

    public BuildEngineDetector(
        ILogger<BuildEngineDetector> logger,
        BuildEngineDetectorOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new BuildEngineDetectorOptions();
    }

    public async Task<DetectedBuildEngine> DetectAsync(CancellationToken ct)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            var apple = await ProbeAsync(_options.AppleContainerPath, ["--version"]);
            if (apple.Found)
            {
                _logger.LogInformation(
                    "BuildEngineDetector chose AppleContainers at {Path} (version: {Version}).",
                    _options.AppleContainerPath, apple.Version);
                return _cached = new DetectedBuildEngine(
                    BuildEngineKind.AppleContainers, _options.AppleContainerPath, apple.Version);
            }

            // `docker buildx version` is the canonical buildkit-on-docker
            // probe — it confirms both that docker is on PATH AND that
            // the buildx subcommand is available (older docker installs
            // lack it). Plain `docker --version` would pass for installs
            // that can't actually buildx.
            var docker = await ProbeAsync(_options.DockerPath, ["buildx", "version"]);
            if (docker.Found)
            {
                _logger.LogInformation(
                    "BuildEngineDetector chose DockerBuildKit at {Path} (version: {Version}).",
                    _options.DockerPath, docker.Version);
                return _cached = new DetectedBuildEngine(
                    BuildEngineKind.DockerBuildKit, _options.DockerPath, docker.Version);
            }

            _logger.LogWarning(
                "BuildEngineDetector found no usable build engine — neither Apple Containers ({Apple}) nor Docker BuildKit ({Docker}) responded. " +
                "Builds will fail with 503 until one is installed.",
                _options.AppleContainerPath, _options.DockerPath);

            return _cached = new DetectedBuildEngine(BuildEngineKind.None, string.Empty, string.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(bool Found, string Version)> ProbeAsync(
        string executablePath,
        IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex)
        {
            // Most common: the binary isn't on PATH. That's not an
            // error — it's the "engine not present" signal.
            _logger.LogDebug(ex,
                "Probe for {Path} failed at launch — treating as engine-not-present.",
                executablePath);
            return (false, string.Empty);
        }

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            // Cap the probe at a few seconds — a hung child process
            // shouldn't block API startup.
            var exitTask = process.WaitForExitAsync();
            var timeout = Task.Delay(_options.ProbeTimeout);
            var winner = await Task.WhenAny(exitTask, timeout);
            if (winner == timeout)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                _logger.LogWarning(
                    "Probe for {Path} timed out after {Timeout}.",
                    executablePath, _options.ProbeTimeout);
                return (false, string.Empty);
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                _logger.LogDebug(
                    "Probe for {Path} exited {Code}: {Stderr}",
                    executablePath, process.ExitCode, stderr);
                return (false, string.Empty);
            }

            // Some engines print the version on stdout, some on stderr
            // (Apple's `container --version` historically went to
            // stderr). Prefer stdout, fall back to stderr.
            var version = (string.IsNullOrWhiteSpace(stdout) ? stderr : stdout)
                .Split('\n', 2)[0]
                .Trim();
            return (true, version);
        }
        finally
        {
            process.Dispose();
        }
    }
}

/// <summary>
/// Configuration knobs for <see cref="BuildEngineDetector"/>. Defaults
/// resolve <c>container</c> and <c>docker</c> from PATH; tests
/// substitute paths to fake binaries.
/// </summary>
public sealed class BuildEngineDetectorOptions
{
    public string AppleContainerPath { get; init; } = "container";
    public string DockerPath { get; init; } = "docker";
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
