using System.Diagnostics;
using Andy.Containers.Abstractions.Images;
using Andy.Containers.Configuration;
using Andy.Containers.Infrastructure.Registries.Local;
using Andy.Containers.Models.ImageManagement;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Andy.Containers.Infrastructure.Images;

/// <summary>
/// rivoli-ai/conductor#1014. Implementation of
/// <see cref="IImagePullService"/> that re-hosts an upstream OCI
/// image into a local registry by shelling out to the Docker CLI:
/// <c>docker pull</c> → <c>docker tag</c> → <c>docker push</c>.
///
/// Idempotency: before pulling, the service asks the destination
/// <see cref="IRegistryAdapter"/> whether anything is already
/// stored at the target coordinate. If so the pull is skipped and
/// the response carries <see cref="EnsurePullResponse.AlreadyPresent"/>
/// = true. Conductor relies on this so a 30s monitor loop can call
/// the endpoint on every tick at near-zero cost.
///
/// Why Docker CLI instead of an in-process OCI library: the embedded
/// path already has a host Docker daemon (every other image
/// operation in the project assumes one). Shelling out reuses the
/// daemon's auth helpers, retry behaviour, and proxy configuration
/// without duplicating those concerns. The same daemon-required
/// constraint applies to the existing <c>DockerCliUploader</c>.
/// </summary>
public sealed class DockerCliImagePullService : IImagePullService
{
    private readonly IEnumerable<IRegistryAdapter> _registryAdapters;
    private readonly IOptions<RegistryConfigurationOptions> _registryConfig;
    private readonly ILogger<DockerCliImagePullService> _logger;
    private readonly DockerCliImagePullOptions _options;
    private readonly PushTargetHostOptions _pushTargetOptions;

    public DockerCliImagePullService(
        IEnumerable<IRegistryAdapter> registryAdapters,
        IOptions<RegistryConfigurationOptions> registryConfig,
        ILogger<DockerCliImagePullService> logger,
        IOptions<DockerCliImagePullOptions>? options = null,
        IOptions<PushTargetHostOptions>? pushTargetOptions = null)
    {
        _registryAdapters = registryAdapters;
        _registryConfig = registryConfig;
        _logger = logger;
        _options = options?.Value ?? new DockerCliImagePullOptions();
        _pushTargetOptions = pushTargetOptions?.Value ?? new PushTargetHostOptions();
    }

    public async Task<EnsurePullResponse> EnsurePullAsync(
        EnsurePullRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var destRepo = request.DestinationRepository ?? DefaultDestRepository(request.SourceRepository);
        var destTag = request.DestinationTag ?? request.SourceTag;

        var destAdapter = _registryAdapters.FirstOrDefault(a => a.RegistryId == request.DestinationRegistryId)
            ?? throw new ImagePullException(
                code: "ensure_pull_unknown_destination_registry",
                message: $"no IRegistryAdapter registered for destination registry id '{request.DestinationRegistryId}'");

        var destRegistryEntry = _registryConfig.Value.Registries.FirstOrDefault(r => r.Id == request.DestinationRegistryId)
            ?? throw new ImagePullException(
                code: "ensure_pull_unknown_destination_registry",
                message: $"no RegistryConfigEntry for destination registry id '{request.DestinationRegistryId}' — check Registries config");

        // Idempotency probe. If the destination already lists a
        // reference at the target coordinate we skip the pull
        // entirely and return AlreadyPresent=true.
        var existing = await TryGetExistingAsync(destAdapter, destRepo, destTag, ct);
        if (existing is not null)
        {
            _logger.LogDebug(
                "ensure-pull short-circuit: {Registry}/{Repo}:{Tag} already present (digest {Digest})",
                request.DestinationRegistryId, destRepo, destTag, existing.Digest);
            return new EnsurePullResponse
            {
                AlreadyPresent = true,
                RegistryId = destAdapter.RegistryId,
                RepoPath = destRepo,
                Tag = destTag,
                Digest = existing.Digest ?? string.Empty,
                SizeBytes = 0,
            };
        }

        // Construct the wire-form refs. The destination host token
        // comes from the registry's configured URL minus the
        // scheme — `docker pull/push` always operates on host:port,
        // not a URL.
        var sourceRef = $"{request.SourceRegistry.TrimEnd('/')}/{request.SourceRepository}:{request.SourceTag}";
        var destHost = ExtractHost(destRegistryEntry.Url);
        // Docker Desktop loopback gap: `docker push` runs inside the
        // Docker Desktop VM, where `localhost` is the VM, not the host
        // running zot. Rewrite the destination authority to a
        // VM-reachable host (host.docker.internal) on Docker Desktop;
        // on Linux the daemon shares the host network so it's left as
        // configured. See PushTargetHostResolver.
        var destResolution = PushTargetHostResolver.Resolve(destHost, _pushTargetOptions);
        var destRef = $"{destResolution.TargetAuthority}/{destRepo}:{destTag}";

        await RunDockerAsync("Pull", new[] { "pull", sourceRef }, ct, destResolution);
        await RunDockerAsync("Tag", new[] { "tag", sourceRef, destRef }, ct, destResolution);
        await RunDockerAsync("Push", new[] { "push", destRef }, ct, destResolution);

        // Re-probe to read the authoritative digest the destination
        // recorded post-push. Parsing it from `docker push` stderr
        // is brittle across CLI versions (same reason
        // DockerCliUploader leaves it to the adapter).
        var pushed = await TryGetExistingAsync(destAdapter, destRepo, destTag, ct)
            ?? throw new ImagePullException(
                code: "ensure_pull_push_succeeded_but_lookup_failed",
                message: $"pushed {destRef} but the destination registry didn't report a reference for it post-push");

        return new EnsurePullResponse
        {
            AlreadyPresent = false,
            RegistryId = destAdapter.RegistryId,
            RepoPath = destRepo,
            Tag = destTag,
            Digest = pushed.Digest ?? string.Empty,
            SizeBytes = 0,
        };
    }

    // -- Helpers -------------------------------------------------------

    private static void Validate(EnsurePullRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceRegistry))
            throw new ImagePullException("ensure_pull_invalid_source_registry", "SourceRegistry is required");
        if (string.IsNullOrWhiteSpace(request.SourceRepository))
            throw new ImagePullException("ensure_pull_invalid_source_repository", "SourceRepository is required");
        if (string.IsNullOrWhiteSpace(request.SourceTag))
            throw new ImagePullException("ensure_pull_invalid_source_tag", "SourceTag is required");
        if (string.IsNullOrWhiteSpace(request.DestinationRegistryId))
            throw new ImagePullException("ensure_pull_invalid_destination_registry_id", "DestinationRegistryId is required");
    }

    /// <summary>
    /// The standard rehost shape: drop everything but the last path
    /// segment of the upstream repo. Upstream
    /// <c>rivoli-ai/conductor-terminal-claude-code</c> becomes local
    /// <c>conductor-terminal-claude-code</c>. Conductor's monitor
    /// keys on the latter, so this default keeps the contract aligned
    /// without callers having to spell out DestinationRepository.
    /// </summary>
    private static string DefaultDestRepository(string sourceRepository)
    {
        var trimmed = sourceRepository.Trim('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash < 0 ? trimmed : trimmed[(lastSlash + 1)..];
    }

    /// <summary>
    /// Strip the scheme from the registry URL — `docker pull/push`
    /// expects host:port, not a URL. Handles configured values like
    /// <c>http://localhost:5050</c> as well as bare <c>localhost:5050</c>.
    /// </summary>
    private static string ExtractHost(string registryUrl)
    {
        if (string.IsNullOrWhiteSpace(registryUrl))
            throw new ImagePullException("ensure_pull_invalid_destination_registry_url", "destination registry has no URL configured");

        if (Uri.TryCreate(registryUrl, UriKind.Absolute, out var uri))
        {
            return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        }
        return registryUrl.Trim();
    }

    private static async Task<RegistryReference?> TryGetExistingAsync(
        IRegistryAdapter adapter,
        string repo,
        string tag,
        CancellationToken ct)
    {
        try
        {
            var refs = await adapter.ListReferencesAsync(repo, ct);
            return refs.FirstOrDefault(r => string.Equals(r.Tag, tag, StringComparison.Ordinal));
        }
        catch (Exception)
        {
            // Defensive: missing-repo is a 404 from the registry,
            // which surfaces as an exception in some adapters. Treat
            // that the same as "nothing there".
            return null;
        }
    }

    private async Task RunDockerAsync(
        string operationCode,
        string[] arguments,
        CancellationToken ct,
        PushTargetHostResolution? pushResolution = null)
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
            "ImagePull.{OpCode} starting docker {Args}",
            operationCode, string.Join(' ', arguments));

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new ImagePullException(
                code: $"ensure_pull_docker_launch_failed.{operationCode}",
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
            var combined = stdout + (string.IsNullOrWhiteSpace(stderr) ? string.Empty : Environment.NewLine + stderr);

            // Loud, actionable Docker Desktop diagnostics on the push
            // step — a bare Go networking timeout is useless to a human.
            var hint = pushResolution is { } resolution
                ? RegistryPushFailureDiagnostics.BuildHint(resolution.TargetAuthority, combined, resolution.WasRewritten)
                : null;

            if (hint is not null)
            {
                _logger.LogError(
                    "ImagePull.{OpCode}.DockerDesktopMisconfig: {Hint}", operationCode, hint);
                throw new ImagePullException(
                    code: $"ensure_pull_docker_desktop_unreachable.{operationCode}",
                    message: $"docker exited with code {process.ExitCode} during {operationCode.ToLowerInvariant()}: {Truncate(stderr, 200)}\n\n{hint}",
                    capturedOutput: combined);
            }

            throw new ImagePullException(
                code: $"ensure_pull_docker_nonzero_exit_{process.ExitCode}.{operationCode}",
                message: $"docker exited with code {process.ExitCode} during {operationCode.ToLowerInvariant()}: {Truncate(stderr, 200)}",
                capturedOutput: combined);
        }
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// Configuration knobs for <see cref="DockerCliImagePullService"/>.
/// Default resolves <c>docker</c> from PATH; tests override the
/// executable path to point at a stub script.
/// </summary>
public sealed class DockerCliImagePullOptions
{
    public string DockerExecutablePath { get; set; } = "docker";
}
