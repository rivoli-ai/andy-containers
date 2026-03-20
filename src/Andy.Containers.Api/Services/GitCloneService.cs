using System.Diagnostics;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Telemetry;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

public class GitCloneService : IGitCloneService
{
    private readonly ContainersDbContext _db;
    private readonly IContainerService _containerService;
    private readonly IGitCredentialService _credentialService;
    private readonly ILogger<GitCloneService> _logger;

    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(10);

    public GitCloneService(
        ContainersDbContext db,
        IContainerService containerService,
        IGitCredentialService credentialService,
        ILogger<GitCloneService> logger)
    {
        _db = db;
        _containerService = containerService;
        _credentialService = credentialService;
        _logger = logger;
    }

    public async Task CloneRepositoriesAsync(Guid containerId, CancellationToken ct)
    {
        var repos = await _db.ContainerGitRepositories
            .Where(r => r.ContainerId == containerId && r.CloneStatus == GitCloneStatus.Pending)
            .ToListAsync(ct);

        foreach (var repo in repos)
        {
            try
            {
                await CloneRepositoryInternalAsync(containerId, repo, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clone repository {RepoUrl} for container {ContainerId}", repo.Url, containerId);
                // Failed clones don't fail the container
            }
        }
    }

    public async Task<ContainerGitRepository> CloneRepositoryAsync(Guid containerId, Guid repoId, CancellationToken ct)
    {
        var repo = await _db.ContainerGitRepositories
            .FirstOrDefaultAsync(r => r.Id == repoId && r.ContainerId == containerId, ct)
            ?? throw new KeyNotFoundException($"Repository {repoId} not found for container {containerId}");

        await CloneRepositoryInternalAsync(containerId, repo, ct);
        return repo;
    }

    public async Task<ContainerGitRepository> PullRepositoryAsync(Guid containerId, Guid repoId, CancellationToken ct)
    {
        var repo = await _db.ContainerGitRepositories
            .FirstOrDefaultAsync(r => r.Id == repoId && r.ContainerId == containerId, ct)
            ?? throw new KeyNotFoundException($"Repository {repoId} not found for container {containerId}");

        if (repo.CloneStatus != GitCloneStatus.Cloned)
            throw new InvalidOperationException($"Repository is {repo.CloneStatus}, cannot pull");

        repo.CloneStatus = GitCloneStatus.Pulling;
        await _db.SaveChangesAsync(ct);

        try
        {
            var container = await _db.Containers.FindAsync([containerId], ct)
                ?? throw new KeyNotFoundException($"Container {containerId} not found");

            var pullCommand = $"cd {repo.TargetPath} && git pull";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(CloneTimeout);

            var result = await _containerService.ExecAsync(containerId, pullCommand, timeoutCts.Token);

            if (result.ExitCode != 0)
            {
                repo.CloneStatus = GitCloneStatus.Cloned; // Revert to Cloned on pull failure
                repo.CloneError = $"Pull failed: {result.StdErr}";
                await _db.SaveChangesAsync(ct);

                _logger.LogWarning("Git pull failed for repo {RepoUrl} in container {ContainerId}: {Error}",
                    repo.Url, containerId, result.StdErr);

                return repo;
            }

            repo.CloneStatus = GitCloneStatus.Cloned;
            repo.CloneError = null;
            repo.CloneCompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            _db.Events.Add(new ContainerEvent
            {
                ContainerId = containerId,
                EventType = ContainerEventType.GitPulled,
                Details = System.Text.Json.JsonSerializer.Serialize(new { repoId = repo.Id, url = repo.Url })
            });
            await _db.SaveChangesAsync(ct);

            return repo;
        }
        catch (Exception ex) when (ex is not KeyNotFoundException and not InvalidOperationException)
        {
            repo.CloneStatus = GitCloneStatus.Cloned; // Revert
            repo.CloneError = $"Pull error: {ex.Message}";
            await _db.SaveChangesAsync(ct);
            throw;
        }
    }

    private async Task CloneRepositoryInternalAsync(Guid containerId, ContainerGitRepository repo, CancellationToken ct)
    {
        using var activity = ActivitySources.Git.StartActivity("GitClone");
        activity?.SetTag("url", repo.Url);
        activity?.SetTag("branch", repo.Branch);
        var sw = Stopwatch.StartNew();

        repo.CloneStatus = GitCloneStatus.Cloning;
        repo.CloneStartedAt = DateTime.UtcNow;
        repo.CloneError = null;
        await _db.SaveChangesAsync(ct);

        try
        {
            // Resolve credential
            var container = await _db.Containers.FindAsync([containerId], ct)
                ?? throw new KeyNotFoundException($"Container {containerId} not found");

            string cloneUrl = repo.Url;
            string? gitHost = null;

            if (repo.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(repo.Url);
                gitHost = uri.Host;
            }

            var token = await _credentialService.ResolveTokenAsync(
                container.OwnerId, repo.CredentialRef, gitHost, ct);

            if (token is not null && cloneUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(cloneUrl);
                cloneUrl = $"https://{Uri.EscapeDataString(token)}@{uri.Host}{uri.PathAndQuery}";
            }

            // Build clone command
            var cloneArgs = new List<string> { "git clone" };

            if (repo.CloneDepth.HasValue)
                cloneArgs.Add($"--depth {repo.CloneDepth.Value}");

            if (!string.IsNullOrEmpty(repo.Branch))
                cloneArgs.Add($"--branch {repo.Branch}");

            if (repo.Submodules)
                cloneArgs.Add("--recurse-submodules");

            cloneArgs.Add($"'{cloneUrl}'");
            cloneArgs.Add($"'{repo.TargetPath}'");

            var command = string.Join(" ", cloneArgs);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(CloneTimeout);

            _logger.LogInformation("Cloning {RepoUrl} into {TargetPath} for container {ContainerId}",
                repo.Url, repo.TargetPath, containerId);

            var result = await _containerService.ExecAsync(containerId, command, timeoutCts.Token);

            if (result.ExitCode != 0)
            {
                repo.CloneStatus = GitCloneStatus.Failed;
                repo.CloneError = result.StdErr;
                repo.CloneCompletedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);

                _db.Events.Add(new ContainerEvent
                {
                    ContainerId = containerId,
                    EventType = ContainerEventType.GitCloneFailed,
                    Details = System.Text.Json.JsonSerializer.Serialize(new { repoId = repo.Id, url = repo.Url, error = result.StdErr })
                });
                await _db.SaveChangesAsync(ct);

                _logger.LogWarning("Git clone failed for {RepoUrl} in container {ContainerId}: {Error}",
                    repo.Url, containerId, result.StdErr);
                sw.Stop();
                Meters.GitCloneDuration.Record(sw.Elapsed.TotalMilliseconds);
                Meters.GitClonesFailed.Add(1);
                activity?.SetStatus(ActivityStatusCode.Error, result.StdErr);
                return;
            }

            repo.CloneStatus = GitCloneStatus.Cloned;
            repo.CloneError = null;
            repo.CloneCompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            _db.Events.Add(new ContainerEvent
            {
                ContainerId = containerId,
                EventType = ContainerEventType.GitCloned,
                Details = System.Text.Json.JsonSerializer.Serialize(new { repoId = repo.Id, url = repo.Url, targetPath = repo.TargetPath })
            });
            await _db.SaveChangesAsync(ct);

            sw.Stop();
            Meters.GitCloneDuration.Record(sw.Elapsed.TotalMilliseconds);
            Meters.GitClonesCompleted.Add(1);
            _logger.LogInformation("Successfully cloned {RepoUrl} for container {ContainerId}", repo.Url, containerId);
        }
        catch (Exception ex) when (ex is not KeyNotFoundException)
        {
            repo.CloneStatus = GitCloneStatus.Failed;
            repo.CloneError = ex.Message;
            repo.CloneCompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            _db.Events.Add(new ContainerEvent
            {
                ContainerId = containerId,
                EventType = ContainerEventType.GitCloneFailed,
                Details = System.Text.Json.JsonSerializer.Serialize(new { repoId = repo.Id, url = repo.Url, error = ex.Message })
            });
            await _db.SaveChangesAsync(ct);

            sw.Stop();
            Meters.GitCloneDuration.Record(sw.Elapsed.TotalMilliseconds);
            Meters.GitClonesFailed.Add(1);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            throw;
        }
    }
}
