using System.Diagnostics;
using Andy.Containers.Abstractions;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

public class GitRepositoryProbeService : IGitRepositoryProbeService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly IGitCredentialService _credentialService;
    private readonly ILogger<GitRepositoryProbeService> _logger;

    public GitRepositoryProbeService(
        IGitCredentialService credentialService,
        ILogger<GitRepositoryProbeService> logger)
    {
        _credentialService = credentialService;
        _logger = logger;
    }

    public async Task<List<string>> ProbeRepositoriesAsync(
        IReadOnlyList<GitRepositoryConfig> repos,
        string ownerId,
        bool requireCredentials,
        CancellationToken ct)
    {
        var errors = new List<string>();

        for (var i = 0; i < repos.Count; i++)
        {
            var repo = repos[i];
            var error = await ProbeRepositoryAsync(repo, ownerId, requireCredentials, ct);
            if (error is not null)
                errors.Add(repos.Count > 1 ? $"Repository [{i}]: {error}" : error);
        }

        return errors;
    }

    internal async Task<string?> ProbeRepositoryAsync(
        GitRepositoryConfig repo,
        string ownerId,
        bool requireCredentials,
        CancellationToken ct)
    {
        // Only probe HTTPS URLs — SSH requires key setup we can't do from the host
        if (!repo.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Skipping probe for non-HTTPS URL: {Url}", repo.Url);
            return null;
        }

        // Parse host from URL
        string probeUrl = repo.Url;
        string? gitHost;

        try
        {
            var uri = new Uri(repo.Url);
            gitHost = uri.Host;
        }
        catch (UriFormatException)
        {
            return $"Invalid URL: {repo.Url}";
        }

        // Resolve credential if available
        var token = await _credentialService.ResolveTokenAsync(
            ownerId, repo.CredentialRef, gitHost, ct);

        // If explicit credentialRef was given but not found, reject immediately
        if (repo.CredentialRef is not null && token is null)
        {
            return $"Credential '{repo.CredentialRef}' not found. Store a credential with that label before cloning.";
        }

        if (token is not null)
        {
            var uri = new Uri(probeUrl);
            probeUrl = $"https://{Uri.EscapeDataString(token)}@{uri.Host}{uri.PathAndQuery}";
        }

        var (result, errorMsg) = await RunGitLsRemoteAsync(probeUrl, repo.Url, ct);

        return result switch
        {
            ProbeResult.Accessible => null,
            ProbeResult.Skipped => null,
            ProbeResult.AuthRequired when token is not null =>
                $"Authentication failed for {repo.Url}. The stored credential may be expired or invalid.",
            ProbeResult.AuthRequired when requireCredentials =>
                $"Repository {repo.Url} requires authentication. Provide a credentialRef or store a credential for {gitHost}.",
            ProbeResult.AuthRequired => null, // Silent skip when not requiring credentials
            _ => errorMsg,
        };
    }

    internal async Task<(ProbeResult result, string? error)> RunGitLsRemoteAsync(
        string probeUrl, string displayUrl, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                // --heads limits output to branch refs only (faster, less output)
                Arguments = $"ls-remote --exit-code --heads \"{probeUrl}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // Prevent git from prompting for credentials
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GIT_ASKPASS"] = "echo";

            using var process = Process.Start(psi);
            if (process is null)
                return (ProbeResult.Skipped, null);

            // Read stdout and stderr concurrently to avoid deadlock from full buffers
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (ProbeResult.TimedOut, $"Repository probe timed out for {displayUrl} (10s)");
            }

            if (process.ExitCode == 0)
            {
                _logger.LogDebug("Repository probe succeeded for {Url}", displayUrl);
                return (ProbeResult.Accessible, null);
            }

            var stderr = stderrTask.Result;

            // Exit code 2 = remote found but empty (no matching refs) — still valid
            if (process.ExitCode == 2)
                return (ProbeResult.Accessible, null);

            // Authentication failures
            if (IsAuthError(stderr, process.ExitCode))
            {
                _logger.LogDebug("Repository {Url} requires authentication", displayUrl);
                return (ProbeResult.AuthRequired, null);
            }

            _logger.LogWarning("Repository probe failed for {Url}: exit={Exit} stderr={Stderr}",
                displayUrl, process.ExitCode, stderr.Trim());
            return (ProbeResult.NotFound, $"Repository not found or not accessible: {displayUrl}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Propagate caller cancellation
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Git probe unavailable, skipping URL validation for {Url}", displayUrl);
            return (ProbeResult.Skipped, null);
        }
    }

    private static bool IsAuthError(string stderr, int exitCode)
    {
        if (stderr.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase))
            return true;
        if (stderr.Contains("could not read Username", StringComparison.OrdinalIgnoreCase))
            return true;
        if (exitCode == 128 && stderr.Contains("fatal:", StringComparison.OrdinalIgnoreCase) &&
            (stderr.Contains("could not read", StringComparison.OrdinalIgnoreCase) ||
             stderr.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }
}
