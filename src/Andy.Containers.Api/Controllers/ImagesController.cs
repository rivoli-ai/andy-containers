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
    private readonly IImageManifestService _manifestService;

    public ImagesController(ContainersDbContext db, IImageManifestService manifestService)
    {
        _db = db;
        _manifestService = manifestService;
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
            .Where(i => i.TemplateId == templateId && i.BuildStatus == ImageBuildStatus.Succeeded)
            .OrderByDescending(i => i.BuildNumber)
            .FirstOrDefaultAsync(ct);
        return image is null ? NotFound() : Ok(image);
    }

    [HttpPost("{templateId:guid}/build")]
    public async Task<IActionResult> Build(Guid templateId, [FromBody] BuildRequest? request, CancellationToken ct)
    {
        var template = await _db.Templates.FindAsync([templateId], ct);
        if (template is null) return NotFound();

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
            Changelog = "Initial build"
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

        var diff = await _manifestService.DiffImagesAsync(fromImageId, toImageId, ct);
        return Ok(diff);
    }

    [HttpGet("{imageId:guid}/manifest")]
    public async Task<IActionResult> GetManifest(Guid imageId, CancellationToken ct)
    {
        var image = await _db.Images.FindAsync([imageId], ct);
        if (image is null) return NotFound();

        var manifest = await _manifestService.GetManifestAsync(imageId, ct);
        if (manifest is null)
            return Ok(new { message = "No manifest available. Run POST /api/images/{imageId}/introspect to generate." });

        return Ok(manifest);
    }

    [HttpGet("{imageId:guid}/tools")]
    public async Task<IActionResult> GetTools(Guid imageId, CancellationToken ct)
    {
        var image = await _db.Images.FindAsync([imageId], ct);
        if (image is null) return NotFound();

        var manifest = await _manifestService.GetManifestAsync(imageId, ct);
        if (manifest is null) return Ok(Array.Empty<object>());

        var tools = manifest.Tools.Select(t => new { t.Name, t.Version, Type = t.Type.ToString() });
        return Ok(tools);
    }

    [HttpGet("{imageId:guid}/packages")]
    public async Task<IActionResult> GetPackages(Guid imageId, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        var image = await _db.Images.FindAsync([imageId], ct);
        if (image is null) return NotFound();

        var manifest = await _manifestService.GetManifestAsync(imageId, ct);
        if (manifest is null) return Ok(new { items = Array.Empty<object>(), total = 0 });

        var packages = manifest.OsPackages
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return Ok(new { items = packages, total = manifest.OsPackages.Count, page, pageSize });
    }

    [HttpPost("{imageId:guid}/introspect")]
    public async Task<IActionResult> Introspect(Guid imageId, CancellationToken ct)
    {
        var image = await _db.Images.FindAsync([imageId], ct);
        if (image is null) return NotFound();

        // In a real implementation, this would spin up a container and run the introspection script.
        // For now, return a placeholder manifest based on the image's declared dependencies.
        var declaredDeps = await _db.DependencySpecs
            .Where(d => d.TemplateId == image.TemplateId)
            .ToListAsync(ct);

        var manifest = new ImageToolManifest
        {
            ImageContentHash = image.ContentHash,
            BaseImage = image.ImageReference,
            BaseImageDigest = image.BaseImageDigest,
            Architecture = "amd64",
            OperatingSystem = new OsInfo
            {
                Name = "Ubuntu",
                Version = "24.04",
                Codename = "noble",
                KernelVersion = "6.5.0"
            },
            Tools = declaredDeps.Select(d => new InstalledTool
            {
                Name = d.Name,
                Version = d.VersionConstraint,
                Type = d.Type,
                DeclaredVersion = d.VersionConstraint,
                MatchesDeclared = true
            }).ToList(),
            IntrospectedAt = DateTime.UtcNow
        };

        var stored = await _manifestService.StoreManifestAsync(imageId, manifest, ct);
        return Ok(stored);
    }
}

public record BuildRequest(bool Offline = false, bool Force = false);
