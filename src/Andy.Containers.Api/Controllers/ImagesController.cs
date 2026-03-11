using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
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
    private readonly ICurrentUserService _currentUser;
    private readonly IContainerAuthorizationService _authService;

    public ImagesController(ContainersDbContext db, ICurrentUserService currentUser, IContainerAuthorizationService authService)
    {
        _db = db;
        _currentUser = currentUser;
        _authService = authService;
    }

    [HttpGet("{templateId:guid}")]
    public async Task<IActionResult> List(Guid templateId, CancellationToken ct)
    {
        var images = await _db.Images
            .Where(i => i.TemplateId == templateId)
            .OrderByDescending(i => i.BuildNumber)
            .ToListAsync(ct);
        return Ok(images);
    }

    [HttpGet("{templateId:guid}/latest")]
    public async Task<IActionResult> GetLatest(Guid templateId, CancellationToken ct)
    {
        var image = await _db.Images
            .Where(i => i.TemplateId == templateId && i.BuildStatus == Models.ImageBuildStatus.Succeeded)
            .OrderByDescending(i => i.BuildNumber)
            .FirstOrDefaultAsync(ct);
        return image is null ? NotFound() : Ok(image);
    }

    [HttpPost("{templateId:guid}/build")]
    public async Task<IActionResult> Build(Guid templateId, [FromBody] BuildRequest? request, CancellationToken ct)
    {
        var template = await _db.Templates.FindAsync([templateId], ct);
        if (template is null) return NotFound();

        var orgId = _currentUser.GetOrganizationId();

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
            BuildStatus = ImageBuildStatus.Succeeded,
            BuildStartedAt = DateTime.UtcNow,
            BuildCompletedAt = DateTime.UtcNow,
            BuiltOffline = request?.Offline ?? false,
            Changelog = "Initial build",
            OwnerId = _currentUser.GetUserId(),
            OrganizationId = orgId,
            Visibility = orgId != null ? ImageVisibility.Organization : ImageVisibility.Global
        };

        _db.Images.Add(image);
        await _db.SaveChangesAsync(ct);
        return Accepted(image);
    }

    [HttpGet("diff")]
    public async Task<IActionResult> Diff([FromQuery] Guid fromImageId, [FromQuery] Guid toImageId, CancellationToken ct)
    {
        var from = await _db.Images.FindAsync([fromImageId], ct);
        var to = await _db.Images.FindAsync([toImageId], ct);
        if (from is null || to is null) return NotFound();

        return Ok(new
        {
            fromImageId = from.Id,
            toImageId = to.Id,
            dependencyChanges = Array.Empty<object>(),
            baseImageChanged = from.BaseImageDigest != to.BaseImageDigest ? "Base image updated" : null
        });
    }
}

public record BuildRequest(bool Offline = false, bool Force = false);
