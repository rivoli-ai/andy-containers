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
public class TemplatesController : ControllerBase
{
    private readonly ContainersDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly ICurrentUserService _currentUser;

    public TemplatesController(ContainersDbContext db, IWebHostEnvironment env, ICurrentUserService currentUser)
    {
        _db = db;
        _env = env;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] CatalogScope? scope,
        [FromQuery] Guid? organizationId,
        [FromQuery] Guid? teamId,
        [FromQuery] string? search,
        [FromQuery] bool? gpuRequired,
        [FromQuery] IdeType? ideType,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        CancellationToken ct = default)
    {
        var query = _db.Templates.AsQueryable();

        if (scope.HasValue)
            query = query.Where(t => t.CatalogScope == scope);
        if (organizationId.HasValue)
            query = query.Where(t => t.OrganizationId == organizationId || t.CatalogScope == CatalogScope.Global);
        if (teamId.HasValue)
            query = query.Where(t => t.TeamId == teamId || t.CatalogScope <= CatalogScope.Organization);
        if (!string.IsNullOrEmpty(search))
            query = query.Where(t =>
                t.Name.Contains(search)
                || (t.Description != null && t.Description.Contains(search))
                || t.Code.Contains(search)
                || (t.Tags != null && t.Tags.Contains(search)));
        if (gpuRequired.HasValue)
            query = query.Where(t => t.GpuRequired == gpuRequired);
        if (ideType.HasValue)
            query = query.Where(t => t.IdeType == ideType);

        query = query.Where(t => t.IsPublished).OrderBy(t => t.Name);
        var total = await query.CountAsync(ct);
        var items = await query.Skip(skip).Take(take).ToListAsync(ct);
        return Ok(new { items, totalCount = total });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var template = await _db.Templates.FindAsync([id], ct);
        return template is null ? NotFound() : Ok(template);
    }

    [HttpGet("by-code/{code}")]
    public async Task<IActionResult> GetByCode(string code, CancellationToken ct)
    {
        var template = await _db.Templates.FirstOrDefaultAsync(t => t.Code == code, ct);
        return template is null ? NotFound() : Ok(template);
    }

    [HttpGet("{id:guid}/definition")]
    public async Task<IActionResult> GetDefinition(Guid id, CancellationToken ct)
    {
        var template = await _db.Templates.FindAsync([id], ct);
        if (template is null) return NotFound();

        // Search for the YAML file in config/templates directories
        // Try multiple possible root locations to be resilient to different working directories
        string[] candidates = [];
        foreach (var root in GetConfigSearchPaths())
        {
            if (Directory.Exists(root))
            {
                candidates = Directory.GetFiles(root, $"{template.Code}.yaml", SearchOption.AllDirectories);
                if (candidates.Length > 0) break;
            }
        }

        if (candidates.Length > 0)
        {
            var yaml = await System.IO.File.ReadAllTextAsync(candidates[0], ct);
            return Ok(new { code = template.Code, content = yaml });
        }

        // No YAML file on disk — generate a synthetic definition from DB fields
        var syntheticYaml = GenerateSyntheticYaml(template);
        return Ok(new { code = template.Code, content = syntheticYaml });
    }

    private IEnumerable<string> GetConfigSearchPaths()
    {
        // From ContentRootPath (project dir when using dotnet run)
        yield return Path.GetFullPath(Path.Combine(_env.ContentRootPath, "..", "..", "config", "templates"));
        // From ContentRootPath (if run from repo root)
        yield return Path.Combine(_env.ContentRootPath, "config", "templates");
        // Walk up from ContentRootPath to find config/templates
        var dir = _env.ContentRootPath;
        for (var i = 0; i < 5; i++)
        {
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null) break;
            var candidate = Path.Combine(parent, "config", "templates");
            if (Directory.Exists(candidate))
            {
                yield return candidate;
                break;
            }
            dir = parent;
        }
    }

    private static string GenerateSyntheticYaml(ContainerTemplate template)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"code: {template.Code}");
        sb.AppendLine($"name: {template.Name}");
        if (!string.IsNullOrEmpty(template.Description))
            sb.AppendLine($"description: \"{template.Description}\"");
        sb.AppendLine($"version: {template.Version}");
        sb.AppendLine($"base_image: {template.BaseImage}");
        sb.AppendLine($"ide_type: {template.IdeType}");
        sb.AppendLine($"scope: {template.CatalogScope}");
        if (template.GpuRequired) sb.AppendLine("gpu_required: true");
        if (template.GpuPreferred) sb.AppendLine("gpu_preferred: true");
        if (!string.IsNullOrEmpty(template.DefaultResources))
            sb.AppendLine($"resources: {template.DefaultResources}");
        if (!string.IsNullOrEmpty(template.Ports))
            sb.AppendLine($"ports: {template.Ports}");
        if (!string.IsNullOrEmpty(template.EnvironmentVariables))
            sb.AppendLine($"environment: {template.EnvironmentVariables}");
        if (!string.IsNullOrEmpty(template.Scripts))
            sb.AppendLine($"scripts: {template.Scripts}");
        if (template.Tags is { Length: > 0 })
            sb.AppendLine($"tags: [{string.Join(", ", template.Tags)}]");
        return sb.ToString();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ContainerTemplate template, CancellationToken ct)
    {
        template.OwnerId = _currentUser.GetUserId();
        _db.Templates.Add(template);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = template.Id }, template);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] ContainerTemplate update, CancellationToken ct)
    {
        var template = await _db.Templates.FindAsync([id], ct);
        if (template is null) return NotFound();
        if (!CanModifyTemplate(template)) return Forbid();

        template.Name = update.Name;
        template.Description = update.Description;
        template.Version = update.Version;
        template.IdeType = update.IdeType;
        template.Tags = update.Tags;
        template.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(template);
    }

    [HttpPost("{id:guid}/publish")]
    public async Task<IActionResult> Publish(Guid id, CancellationToken ct)
    {
        var template = await _db.Templates.FindAsync([id], ct);
        if (template is null) return NotFound();
        if (!CanModifyTemplate(template)) return Forbid();

        template.IsPublished = true;
        template.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var template = await _db.Templates.FindAsync([id], ct);
        if (template is null) return NotFound();
        if (!CanModifyTemplate(template)) return Forbid();

        _db.Templates.Remove(template);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private bool CanModifyTemplate(ContainerTemplate template)
    {
        if (_currentUser.IsAdmin()) return true;
        // Global templates can only be modified by admins
        if (template.CatalogScope == CatalogScope.Global) return false;
        // User-scoped templates can be modified by their owner
        return template.OwnerId == _currentUser.GetUserId();
    }
}
