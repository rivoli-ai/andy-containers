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
        CancellationToken ct)
    {
        var errors = new List<string>();

        for (var i = 0; i < repos.Count; i++)
        {
            var repo = repos[i];
            var error = await ProbeRepositoryAsync(repo, ownerId, ct);
            if (error is not null)
                errors.Add(repos.Count > 1 ? $"Repository [{i}]: {error}" : error);
        }

        return errors;
    }

    internal async Task<string?> ProbeRepositoryAsync(
        GitRepositoryConfig repo,
        string ownerId,
        CancellationToken ct)
    {
        // Only probe HTTPS URLs — SSH requires key setup we can't do from the host
        if (!repo.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Skipping probe for non-HTTPS URL: {Url}", repo.Url);
            return null;
        }

        // Resolve credential if available
        string probeUrl = repo.Url;
        string? gitHost = null;

        try
        {
            var uri = new Uri(repo.Url);
            gitHost = uri.Host;
        }
        catch (UriFormatException)
        {
            return $"Invalid URL: {repo.Url}";
        }

        var token = await _credentialService.ResolveTokenAsync(
            ownerId, repo.CredentialRef, gitHost, ct);

        if (token is not null)
        {
            var uri = new Uri(probeUrl);
            probeUrl = $"https://{Uri.EscapeDataString(token)}@{uri.Host}{uri.PathAndQuery}";
        }
        else if (repo.CredentialRef is not null)
        {
            // Explicit credential was requested but not found — skip probe,
            // the clone will fail later with a clear credential error
            _logger.LogDebug("Skipping probe for {Url}: credential '{Ref}' not resolved",
                repo.Url, repo.CredentialRef);
            return null;
        }

        return await RunGitLsRemoteAsync(probeUrl, repo.Url, ct);
    }

    internal async Task<string?> RunGitLsRemoteAsync(
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
                return $"Failed to start git process for {displayUrl}";

            // Read stdout and stderr concurrently to avoid deadlock from full buffers
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            // Wait for exit with timeout
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout — not the caller's cancellation
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return $"Repository probe timed out for {displayUrl} (10s)";
            }

            if (process.ExitCode == 0)
            {
                _logger.LogDebug("Repository probe succeeded for {Url}", displayUrl);
                return null;
            }

            var stderr = stderrTask.Result;

            // Exit code 2 = remote found but empty (no matching refs) — that's still valid
            if (process.ExitCode == 2)
                return null;

            // Authentication failures — if no credential was provided, this likely means
            // it's a private repo. Skip with a warning rather than blocking.
            if (stderr.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("could not read Username", StringComparison.OrdinalIgnoreCase) ||
                process.ExitCode == 128 && stderr.Contains("fatal:", StringComparison.OrdinalIgnoreCase) &&
                    (stderr.Contains("could not read", StringComparison.OrdinalIgnoreCase) ||
                     stderr.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogDebug("Repository {Url} requires authentication, skipping probe", displayUrl);
                return null;
            }

            _logger.LogWarning("Repository probe failed for {Url}: exit={Exit} stderr={Stderr}",
                displayUrl, process.ExitCode, stderr.Trim());
            return $"Repository not found or not accessible: {displayUrl}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Propagate caller cancellation
        }
        catch (Exception ex)
        {
            // git not installed or other system error — skip probe gracefully
            _logger.LogWarning(ex, "Git probe unavailable, skipping URL validation for {Url}", displayUrl);
            return null;
        }
    }
}
