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
    private readonly ICurrentUserService _currentUser;
    private readonly IOrganizationMembershipService _orgMembership;

    public ContainerGrpcService(
        ContainersDbContext db,
        IGitCloneService gitCloneService,
        IImageManifestService manifestService,
        ICurrentUserService currentUser,
        IOrganizationMembershipService orgMembership)
    {
        _db = db;
        _gitCloneService = gitCloneService;
        _manifestService = manifestService;
        _currentUser = currentUser;
        _orgMembership = orgMembership;
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

    public override async Task<ListOrgImagesResponse> ListOrganizationImages(
        ListOrgImagesRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.OrganizationId, out var orgId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid organization ID"));

        if (!_currentUser.IsAdmin())
        {
            var hasPermission = await _orgMembership.HasPermissionAsync(
                _currentUser.GetUserId(), orgId, Permissions.ImageRead, context.CancellationToken);
            if (!hasPermission)
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Insufficient permissions"));
        }

        var images = await _db.Images
            .Where(i => i.OrganizationId == orgId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(context.CancellationToken);

        var response = new ListOrgImagesResponse { TotalCount = images.Count };
        response.Images.AddRange(images.Select(MapImageToGrpc));
        return response;
    }

    public override async Task<ImageResponse> BuildOrganizationImage(
        BuildOrgImageRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.OrganizationId, out var orgId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid organization ID"));

        if (!_currentUser.IsAdmin())
        {
            var hasPermission = await _orgMembership.HasPermissionAsync(
                _currentUser.GetUserId(), orgId, Permissions.ImageBuild, context.CancellationToken);
            if (!hasPermission)
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Insufficient permissions"));
        }

        ContainerTemplate? template = null;
        if (!string.IsNullOrEmpty(request.TemplateId) && Guid.TryParse(request.TemplateId, out var templateId))
            template = await _db.Templates.FindAsync([templateId], context.CancellationToken);
        else if (!string.IsNullOrEmpty(request.TemplateCode))
            template = await _db.Templates.FirstOrDefaultAsync(t => t.Code == request.TemplateCode, context.CancellationToken);

        if (template is null)
            throw new RpcException(new Status(StatusCode.NotFound, "Template not found"));

        var image = new ContainerImage
        {
            TemplateId = template.Id,
            ContentHash = $"sha256:{Guid.NewGuid():N}",
            Tag = $"{template.Code}:{template.Version}-{DateTime.UtcNow:yyyyMMddHHmmss}",
            ImageReference = $"andy-containers/{template.Code}:{template.Version}",
            BaseImageDigest = $"sha256:{Guid.NewGuid():N}",
            DependencyManifest = "{}",
            DependencyLock = "{}",
            BuildNumber = await _db.Images.CountAsync(i => i.TemplateId == template.Id, context.CancellationToken) + 1,
            BuildStatus = ImageBuildStatus.Building,
            BuildStartedAt = DateTime.UtcNow,
            BuiltOffline = request.Offline,
            OrganizationId = orgId,
            OwnerId = _currentUser.GetUserId(),
            Visibility = ImageVisibility.Organization
        };
        _db.Images.Add(image);
        await _db.SaveChangesAsync(context.CancellationToken);

        image.BuildStatus = ImageBuildStatus.Succeeded;
        image.BuildCompletedAt = DateTime.UtcNow;
        image.Changelog = "Organization-scoped build";
        await _db.SaveChangesAsync(context.CancellationToken);

        return MapImageToGrpc(image);
    }

    public override async Task<ImageResponse> PublishOrganizationImage(
        PublishOrgImageRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.OrganizationId, out var orgId) ||
            !Guid.TryParse(request.ImageId, out var imageId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid organization or image ID"));

        if (!_currentUser.IsAdmin())
        {
            var hasPermission = await _orgMembership.HasPermissionAsync(
                _currentUser.GetUserId(), orgId, Permissions.ImagePublish, context.CancellationToken);
            if (!hasPermission)
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Insufficient permissions"));
        }

        var image = await _db.Images.FirstOrDefaultAsync(
            i => i.Id == imageId && i.OrganizationId == orgId, context.CancellationToken);
        if (image is null)
            throw new RpcException(new Status(StatusCode.NotFound, "Image not found"));

        image.Visibility = ImageVisibility.Organization;
        await _db.SaveChangesAsync(context.CancellationToken);

        return MapImageToGrpc(image);
    }

    private static ImageResponse MapImageToGrpc(ContainerImage image)
    {
        return new ImageResponse
        {
            Id = image.Id.ToString(),
            ContentHash = image.ContentHash,
            Tag = image.Tag,
            TemplateId = image.TemplateId.ToString(),
            ImageReference = image.ImageReference,
            BuildNumber = image.BuildNumber,
            BuildStatus = image.BuildStatus.ToString(),
            ImageSizeBytes = image.ImageSizeBytes ?? 0,
            BuiltOffline = image.BuiltOffline,
            Changelog = image.Changelog ?? "",
            CreatedAt = image.CreatedAt.ToString("O"),
            OrganizationId = image.OrganizationId?.ToString() ?? "",
            OwnerId = image.OwnerId ?? "",
            Visibility = image.Visibility.ToString()
        };
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
