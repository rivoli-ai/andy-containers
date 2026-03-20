using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Data;
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

    public ImagesController(ContainersDbContext db, IImageManifestService manifestService, IImageDiffService diffService)
    {
        _db = db;
        _manifestService = manifestService;
        _diffService = diffService;
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

        // Create the image record with a temporary content hash
        var image = new Models.ContainerImage
        {
            TemplateId = templateId,
            ContentHash = $"sha256:{Guid.NewGuid():N}",
            Tag = $"{template.Code}:{template.Version}-{DateTime.UtcNow:yyyyMMddHHmmss}",
            ImageReference = $"andy-containers/{template.Code}:{template.Version}",
            BaseImageDigest = $"sha256:{Guid.NewGuid():N}",
            DependencyManifest = "{}",
            DependencyLock = "{}",
            BuildNumber = await _db.Images.CountAsync(i => i.TemplateId == templateId, ct) + 1,
            BuildStatus = Models.ImageBuildStatus.Building,
            BuildStartedAt = DateTime.UtcNow,
            BuiltOffline = request?.Offline ?? false
        };

        _db.Images.Add(image);
        await _db.SaveChangesAsync(ct);

        try
        {
            // Run introspection to populate the real manifest
            var (manifest, finalImage) = await _manifestService.GenerateManifestAsync(image.Id, ct);

            finalImage.BuildStatus = Models.ImageBuildStatus.Succeeded;
            finalImage.BuildCompletedAt = DateTime.UtcNow;
            finalImage.Changelog = "Build with introspection";
            await _db.SaveChangesAsync(ct);

            return Accepted(finalImage);
        }
        catch (Exception)
        {
            // If introspection fails, the image still succeeds but without manifest data
            image.BuildStatus = Models.ImageBuildStatus.Succeeded;
            image.BuildCompletedAt = DateTime.UtcNow;
            image.Changelog = "Build completed (introspection unavailable)";
            await _db.SaveChangesAsync(ct);

            return Accepted(image);
        }
    }

    [HttpGet("diff")]
    public async Task<IActionResult> Diff([FromQuery] Guid fromImageId, [FromQuery] Guid toImageId, CancellationToken ct)
    {
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

    [HttpGet("{imageId:guid}/manifest")]
    public async Task<IActionResult> GetManifest(Guid imageId, CancellationToken ct)
    {
        var image = await _db.Images.FindAsync([imageId], ct);
        if (image is null) return NotFound();

        var manifest = await _manifestService.GetManifestAsync(imageId, ct);
        if (manifest is null) return NotFound("Image has not been introspected");

        return Ok(manifest);
    }

    [HttpGet("{imageId:guid}/tools")]
    public async Task<IActionResult> GetTools(Guid imageId, CancellationToken ct)
    {
        var manifest = await _manifestService.GetManifestAsync(imageId, ct);
        if (manifest is null) return NotFound("Image has not been introspected");

        return Ok(manifest.Tools);
    }

    [HttpGet("{imageId:guid}/packages")]
    public async Task<IActionResult> GetPackages(Guid imageId, CancellationToken ct)
    {
        var manifest = await _manifestService.GetManifestAsync(imageId, ct);
        if (manifest is null) return NotFound("Image has not been introspected");

        return Ok(manifest.OsPackages);
    }

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

public record BuildRequest(bool Offline = false, bool Force = false);
