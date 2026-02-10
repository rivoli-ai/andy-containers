using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WorkspacesController : ControllerBase
{
    private readonly ContainersDbContext _db;

    public WorkspacesController(ContainersDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? ownerId,
        [FromQuery] Guid? organizationId,
        [FromQuery] WorkspaceStatus? status,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        CancellationToken ct = default)
    {
        var query = _db.Workspaces.Include(w => w.DefaultContainer).AsQueryable();
        if (!string.IsNullOrEmpty(ownerId))
            query = query.Where(w => w.OwnerId == ownerId);
        if (organizationId.HasValue)
            query = query.Where(w => w.OrganizationId == organizationId);
        if (status.HasValue)
            query = query.Where(w => w.Status == status);

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(w => w.CreatedAt).Skip(skip).Take(take).ToListAsync(ct);
        return Ok(new { items, totalCount = total });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var ws = await _db.Workspaces.Include(w => w.DefaultContainer).Include(w => w.Containers).FirstOrDefaultAsync(w => w.Id == id, ct);
        return ws is null ? NotFound() : Ok(ws);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWorkspaceDto dto, CancellationToken ct)
    {
        var workspace = new Workspace
        {
            Name = dto.Name,
            Description = dto.Description,
            OwnerId = "system", // TODO: from auth
            OrganizationId = dto.OrganizationId,
            TeamId = dto.TeamId,
            GitRepositoryUrl = dto.GitRepositoryUrl,
            GitBranch = dto.GitBranch
        };
        _db.Workspaces.Add(workspace);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = workspace.Id }, workspace);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateWorkspaceDto dto, CancellationToken ct)
    {
        var ws = await _db.Workspaces.FindAsync([id], ct);
        if (ws is null) return NotFound();
        if (dto.Name is not null) ws.Name = dto.Name;
        if (dto.Description is not null) ws.Description = dto.Description;
        if (dto.GitBranch is not null) ws.GitBranch = dto.GitBranch;
        ws.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(ws);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var ws = await _db.Workspaces.FindAsync([id], ct);
        if (ws is null) return NotFound();
        _db.Workspaces.Remove(ws);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public record CreateWorkspaceDto(string Name, string? Description, Guid? OrganizationId, Guid? TeamId, string? GitRepositoryUrl, string? GitBranch);
public record UpdateWorkspaceDto(string? Name, string? Description, string? GitBranch);
