using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TemplatesController : ControllerBase
{
    private readonly ContainersDbContext _db;

    public TemplatesController(ContainersDbContext db)
    {
        _db = db;
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
            query = query.Where(t => t.Name.Contains(search) || (t.Description != null && t.Description.Contains(search)) || t.Code.Contains(search));
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

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ContainerTemplate template, CancellationToken ct)
    {
        _db.Templates.Add(template);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = template.Id }, template);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] ContainerTemplate update, CancellationToken ct)
    {
        var template = await _db.Templates.FindAsync([id], ct);
        if (template is null) return NotFound();

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
        _db.Templates.Remove(template);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
