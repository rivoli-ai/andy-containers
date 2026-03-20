using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

public interface IGitCloneService
{
    Task CloneRepositoriesAsync(Guid containerId, CancellationToken ct = default);
    Task<ContainerGitRepository> CloneRepositoryAsync(Guid containerId, Guid repoId, CancellationToken ct = default);
    Task<ContainerGitRepository> PullRepositoryAsync(Guid containerId, Guid repoId, CancellationToken ct = default);
}
