using System.Text;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Services;

public class GitCloneService : IGitCloneService
{
    private readonly ContainersDbContext _db;

    public GitCloneService(ContainersDbContext db)
    {
        _db = db;
    }

    public async Task<ContainerGitRepository> AddRepositoryAsync(Guid containerId, GitCloneRequest request, CancellationToken ct = default)
    {
        if (!GitUrlValidator.IsValidGitUrl(request.Url))
            throw new ArgumentException("Invalid git repository URL. Only HTTPS and SSH URLs are accepted.");

        if (GitUrlValidator.HasEmbeddedCredentials(request.Url))
            throw new ArgumentException("Git URLs must not contain embedded credentials.");

        if (!GitUrlValidator.IsValidBranchName(request.Branch))
            throw new ArgumentException($"Invalid branch name: '{request.Branch}'");

        var targetPath = request.TargetPath ?? GitUrlValidator.DeriveTargetPath(request.Url);
        if (!GitUrlValidator.IsValidTargetPath(targetPath))
            throw new ArgumentException($"Invalid target path: '{targetPath}'");

        // Check for duplicate target path within container
        var exists = await _db.ContainerGitRepositories.AnyAsync(
            r => r.ContainerId == containerId && r.TargetPath == targetPath, ct);
        if (exists)
            throw new InvalidOperationException($"Target path '{targetPath}' already in use in this container.");

        var maxOrder = await _db.ContainerGitRepositories
            .Where(r => r.ContainerId == containerId)
            .Select(r => (int?)r.SortOrder)
            .MaxAsync(ct) ?? -1;

        var repo = new ContainerGitRepository
        {
            ContainerId = containerId,
            Url = request.Url,
            Branch = request.Branch,
            TargetPath = targetPath,
            CredentialRef = request.CredentialRef,
            CloneDepth = request.CloneDepth,
            Submodules = request.Submodules,
            SortOrder = maxOrder + 1
        };

        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync(ct);
        return repo;
    }

    public async Task<IReadOnlyList<ContainerGitRepository>> ListRepositoriesAsync(Guid containerId, CancellationToken ct = default)
    {
        return await _db.ContainerGitRepositories
            .Where(r => r.ContainerId == containerId)
            .OrderBy(r => r.SortOrder)
            .ToListAsync(ct);
    }

    public async Task<ContainerGitRepository?> GetRepositoryAsync(Guid containerId, Guid repoId, CancellationToken ct = default)
    {
        return await _db.ContainerGitRepositories
            .FirstOrDefaultAsync(r => r.Id == repoId && r.ContainerId == containerId, ct);
    }

    public string GenerateCloneCommand(string url, string branch, string? targetPath, int cloneDepth, bool submodules)
    {
        var sb = new StringBuilder("git clone");
        sb.Append($" --branch {branch}");
        if (cloneDepth > 0) sb.Append($" --depth {cloneDepth}");
        if (submodules) sb.Append(" --recurse-submodules");
        sb.Append($" {url}");
        if (!string.IsNullOrEmpty(targetPath)) sb.Append($" {targetPath}");
        return sb.ToString();
    }

    public string GeneratePullCommand(string targetPath)
    {
        return $"cd {targetPath} && git pull";
    }
}
