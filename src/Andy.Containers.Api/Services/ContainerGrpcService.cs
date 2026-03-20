using Andy.Containers.Grpc;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Services;

public class ContainerGrpcService : Grpc.ContainerService.ContainerServiceBase
{
    private readonly ContainersDbContext _db;
    private readonly IGitCloneService _gitCloneService;
    private readonly IImageManifestService _manifestService;

    public ContainerGrpcService(ContainersDbContext db, IGitCloneService gitCloneService, IImageManifestService manifestService)
    {
        _db = db;
        _gitCloneService = gitCloneService;
        _manifestService = manifestService;
    }

    public override async Task<ListContainerRepositoriesResponse> ListContainerRepositories(
        ContainerIdRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ContainerId, out var containerId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid container ID"));

        var repos = await _db.ContainerGitRepositories
            .Where(r => r.ContainerId == containerId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(context.CancellationToken);

        var response = new ListContainerRepositoriesResponse();
        response.Repositories.AddRange(repos.Select(MapToGrpc));
        return response;
    }

    public override async Task<GitRepositoryStatusResponse> CloneRepository(
        CloneRepositoryRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ContainerId, out var containerId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid container ID"));

        var repo = new ContainerGitRepository
        {
            ContainerId = containerId,
            Url = request.Url,
            Branch = string.IsNullOrEmpty(request.Branch) ? null : request.Branch,
            TargetPath = string.IsNullOrEmpty(request.TargetPath) ? "/workspace" : request.TargetPath,
            CredentialRef = string.IsNullOrEmpty(request.CredentialRef) ? null : request.CredentialRef,
            CloneDepth = request.CloneDepth > 0 ? request.CloneDepth : null,
            Submodules = request.Submodules,
            CloneStatus = GitCloneStatus.Pending
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync(context.CancellationToken);

        var cloned = await _gitCloneService.CloneRepositoryAsync(containerId, repo.Id, context.CancellationToken);
        return MapToGrpc(cloned);
    }

    public override async Task<GitRepositoryStatusResponse> PullRepository(
        PullRepositoryRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ContainerId, out var containerId) ||
            !Guid.TryParse(request.RepositoryId, out var repoId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid container or repository ID"));

        var repo = await _gitCloneService.PullRepositoryAsync(containerId, repoId, context.CancellationToken);
        return MapToGrpc(repo);
    }

    public override async Task<ImageManifestResponse> GetImageManifest(
        GetImageManifestRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ImageId, out var imageId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid image ID"));

        var manifest = await _manifestService.GetManifestAsync(imageId, context.CancellationToken);
        if (manifest is null)
            throw new RpcException(new Status(StatusCode.NotFound, "Image has not been introspected"));

        return MapManifestToGrpc(manifest);
    }

    public override async Task<ImageToolsResponse> GetImageTools(
        GetImageManifestRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ImageId, out var imageId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid image ID"));

        var manifest = await _manifestService.GetManifestAsync(imageId, context.CancellationToken);
        if (manifest is null)
            throw new RpcException(new Status(StatusCode.NotFound, "Image has not been introspected"));

        var response = new ImageToolsResponse();
        response.Tools.AddRange(manifest.Tools.Select(t => new InstalledToolEntry
        {
            Name = t.Name,
            Version = t.Version,
            Type = t.Type.ToString(),
            MatchesDeclared = t.MatchesDeclared
        }));
        return response;
    }

    public override async Task<ImageManifestResponse> IntrospectImage(
        GetImageManifestRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ImageId, out var imageId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid image ID"));

        var (manifest, _) = await _manifestService.RefreshManifestAsync(imageId, context.CancellationToken);
        return MapManifestToGrpc(manifest);
    }

    private static ImageManifestResponse MapManifestToGrpc(ImageToolManifest manifest)
    {
        var response = new ImageManifestResponse
        {
            ImageContentHash = manifest.ImageContentHash,
            BaseImage = manifest.BaseImage,
            BaseImageDigest = manifest.BaseImageDigest,
            Architecture = manifest.Architecture,
            OsName = manifest.OperatingSystem.Name,
            OsVersion = manifest.OperatingSystem.Version,
            PackageCount = manifest.OsPackages.Count,
            IntrospectedAt = manifest.IntrospectedAt.ToString("O")
        };
        response.Tools.AddRange(manifest.Tools.Select(t => new InstalledToolEntry
        {
            Name = t.Name,
            Version = t.Version,
            Type = t.Type.ToString(),
            MatchesDeclared = t.MatchesDeclared
        }));
        return response;
    }

    private static GitRepositoryStatusResponse MapToGrpc(ContainerGitRepository repo)
    {
        return new GitRepositoryStatusResponse
        {
            Id = repo.Id.ToString(),
            ContainerId = repo.ContainerId.ToString(),
            Url = repo.Url,
            Branch = repo.Branch ?? "",
            TargetPath = repo.TargetPath,
            CloneDepth = repo.CloneDepth ?? 0,
            Submodules = repo.Submodules,
            IsFromTemplate = repo.IsFromTemplate,
            CloneStatus = repo.CloneStatus.ToString(),
            CloneError = repo.CloneError ?? "",
            CloneStartedAt = repo.CloneStartedAt?.ToString("O") ?? "",
            CloneCompletedAt = repo.CloneCompletedAt?.ToString("O") ?? ""
        };
    }
}
