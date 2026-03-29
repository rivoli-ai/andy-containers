using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Andy.Rbac.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ImagesController : ControllerBase
{
    private readonly ContainersDbContext _db;
    private readonly IImageManifestService _manifestService;
    private readonly IImageDiffService _diffService;
    private readonly ICurrentUserService _currentUser;
    private readonly IOrganizationMembershipService _orgMembership;

    public ImagesController(
        ContainersDbContext db,
        IImageManifestService manifestService,
        IImageDiffService diffService,
        ICurrentUserService currentUser,
        IOrganizationMembershipService orgMembership)
    {
        _db = db;
        _manifestService = manifestService;
        _diffService = diffService;
        _currentUser = currentUser;
        _orgMembership = orgMembership;
    }

    [RequirePermission("image:read")]
    [HttpGet("{templateId:guid}")]
    public async Task<IActionResult> List(Guid templateId, [FromQuery] Guid? organizationId = null, CancellationToken ct = default)
    {
        // Validate org membership if org filter specified
        if (organizationId.HasValue && !_currentUser.IsAdmin())
        {
            var isMember = await _orgMembership.IsMemberAsync(_currentUser.GetUserId(), organizationId.Value, ct);
            if (!isMember) return Forbid();
        }

        var query = _db.Images.Where(i => i.TemplateId == templateId);

        if (organizationId.HasValue)
        {
            // Show global images + org-specific images
            query = query.Where(i => i.OrganizationId == null || i.OrganizationId == organizationId);
        }
        else if (!_currentUser.IsAdmin())
        {
            // Non-admin without org filter: show only global images
            query = query.Where(i => i.OrganizationId == null);
        }

        var images = await query.OrderByDescending(i => i.BuildNumber).ToListAsync(ct);
        return Ok(images);
    }

    [RequirePermission("image:read")]
    [HttpGet("{templateId:guid}/latest")]
    public async Task<IActionResult> GetLatest(Guid templateId, [FromQuery] Guid? organizationId = null, CancellationToken ct = default)
    {
        if (organizationId.HasValue && !_currentUser.IsAdmin())
        {
            var isMember = await _orgMembership.IsMemberAsync(_currentUser.GetUserId(), organizationId.Value, ct);
            if (!isMember) return Forbid();
        }

        var query = _db.Images
            .Where(i => i.TemplateId == templateId && i.BuildStatus == ImageBuildStatus.Succeeded);

        if (organizationId.HasValue)
            query = query.Where(i => i.OrganizationId == null || i.OrganizationId == organizationId);
        else if (!_currentUser.IsAdmin())
            query = query.Where(i => i.OrganizationId == null);

        var image = await query.OrderByDescending(i => i.BuildNumber).FirstOrDefaultAsync(ct);
        return image is null ? NotFound() : Ok(image);
    }

    [RequirePermission("image:write")]
    [HttpPost("{templateId:guid}/build")]
    public async Task<IActionResult> Build(Guid templateId, [FromBody] BuildRequest? request, CancellationToken ct)
    {
        var template = await _db.Templates.FindAsync([templateId], ct);
        if (template is null) return NotFound();

        Guid? organizationId = request?.OrganizationId;

        // Validate org membership and permission for org-scoped builds
        if (organizationId.HasValue && !_currentUser.IsAdmin())
        {
            var hasPermission = await _orgMembership.HasPermissionAsync(
                _currentUser.GetUserId(), organizationId.Value, Permissions.ImageBuild, ct);
            if (!hasPermission) return Forbid();
        }

        // Create the image record with a temporary content hash
        var image = new ContainerImage
        {
            TemplateId = templateId,
            ContentHash = $"sha256:{Guid.NewGuid():N}",
            Tag = $"{template.Code}:{template.Version}-{DateTime.UtcNow:yyyyMMddHHmmss}",
            ImageReference = $"andy-containers/{template.Code}:{template.Version}",
            BaseImageDigest = $"sha256:{Guid.NewGuid():N}",
            DependencyManifest = "{}",
            DependencyLock = "{}",
            BuildNumber = await _db.Images.CountAsync(i => i.TemplateId == templateId, ct) + 1,
            BuildStatus = ImageBuildStatus.Building,
            BuildStartedAt = DateTime.UtcNow,
            BuiltOffline = request?.Offline ?? false,
            OrganizationId = organizationId,
            OwnerId = _currentUser.GetUserId(),
            Visibility = organizationId.HasValue ? ImageVisibility.Organization : ImageVisibility.Global
        };

        _db.Images.Add(image);
        await _db.SaveChangesAsync(ct);

        try
        {
            // Run introspection to populate the real manifest
            var (manifest, finalImage) = await _manifestService.GenerateManifestAsync(image.Id, ct);

            finalImage.BuildStatus = ImageBuildStatus.Succeeded;
            finalImage.BuildCompletedAt = DateTime.UtcNow;
            finalImage.Changelog = "Build with introspection";
            await _db.SaveChangesAsync(ct);

            return Accepted(finalImage);
        }
        catch (Exception)
        {
            // If introspection fails, the image still succeeds but without manifest data
            image.BuildStatus = ImageBuildStatus.Succeeded;
            image.BuildCompletedAt = DateTime.UtcNow;
            image.Changelog = "Build completed (introspection unavailable)";
            await _db.SaveChangesAsync(ct);

            return Accepted(image);
        }
    }

    [RequirePermission("image:read")]
    [HttpGet("diff")]
    public async Task<IActionResult> Diff([FromQuery] Guid fromImageId, [FromQuery] Guid toImageId, CancellationToken ct)
    {
        // Validate user can access both images
        if (!_currentUser.IsAdmin())
        {
            var userId = _currentUser.GetUserId();
            var fromImage = await _db.Images.AsNoTracking().FirstOrDefaultAsync(i => i.Id == fromImageId, ct);
            var toImage = await _db.Images.AsNoTracking().FirstOrDefaultAsync(i => i.Id == toImageId, ct);

            if (fromImage?.OrganizationId != null)
            {
                var canAccess = await _orgMembership.IsMemberAsync(userId, fromImage.OrganizationId.Value, ct);
                if (!canAccess) return Forbid();
            }
            if (toImage?.OrganizationId != null)
            {
                var canAccess = await _orgMembership.IsMemberAsync(userId, toImage.OrganizationId.Value, ct);
                if (!canAccess) return Forbid();
            }
        }

        try
        {
            var diff = await _diffService.DiffAsync(fromImageId, toImageId, ct);
            return Ok(diff);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [RequirePermission("image:read")]
    [HttpGet("{imageId:guid}/manifest")]
    public async Task<IActionResult> GetManifest(Guid imageId, CancellationToken ct)
    {
        var image = await _db.Images.FindAsync([imageId], ct);
        if (image is null) return NotFound();

        var manifest = await _manifestService.GetManifestAsync(imageId, ct);
        if (manifest is null) return NotFound("Image has not been introspected");

        return Ok(manifest);
    }

    [RequirePermission("image:read")]
    [HttpGet("{imageId:guid}/tools")]
    public async Task<IActionResult> GetTools(Guid imageId, CancellationToken ct)
    {
        var manifest = await _manifestService.GetManifestAsync(imageId, ct);
        if (manifest is null) return NotFound("Image has not been introspected");

        return Ok(manifest.Tools);
    }

    [RequirePermission("image:read")]
    [HttpGet("{imageId:guid}/packages")]
    public async Task<IActionResult> GetPackages(Guid imageId, CancellationToken ct)
    {
        var manifest = await _manifestService.GetManifestAsync(imageId, ct);
        if (manifest is null) return NotFound("Image has not been introspected");

        return Ok(manifest.OsPackages);
    }

    [RequirePermission("image:write")]
    [HttpPost("{imageId:guid}/introspect")]
    public async Task<IActionResult> Introspect(Guid imageId, CancellationToken ct)
    {
        var image = await _db.Images.FindAsync([imageId], ct);
        if (image is null) return NotFound();

        try
        {
            var (manifest, updatedImage) = await _manifestService.RefreshManifestAsync(imageId, ct);
            return Ok(new { Manifest = manifest, Image = updatedImage });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }
}

public record BuildRequest(bool Offline = false, bool Force = false, Guid? OrganizationId = null);
