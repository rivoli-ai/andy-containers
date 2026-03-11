using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public interface IGitCloneService
{
    Task<ContainerGitRepository> AddRepositoryAsync(Guid containerId, GitCloneRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<ContainerGitRepository>> ListRepositoriesAsync(Guid containerId, CancellationToken ct = default);
    Task<ContainerGitRepository?> GetRepositoryAsync(Guid containerId, Guid repoId, CancellationToken ct = default);
    string GenerateCloneCommand(string url, string branch, string? targetPath, int cloneDepth, bool submodules);
    string GeneratePullCommand(string targetPath);
}

public class GitCloneRequest
{
    public required string Url { get; set; }
    public string Branch { get; set; } = "main";
    public string? TargetPath { get; set; }
    public string? CredentialRef { get; set; }
    public int CloneDepth { get; set; }
    public bool Submodules { get; set; }
}
