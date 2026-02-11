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
public class WorkspacesController : ControllerBase
{
    private readonly ContainersDbContext _db;
    private readonly ICurrentUserService _currentUser;

    public WorkspacesController(ContainersDbContext db, ICurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
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

        // Non-admins can only see their own workspaces
        var effectiveOwnerId = _currentUser.IsAdmin() ? ownerId : _currentUser.GetUserId();
        if (!string.IsNullOrEmpty(effectiveOwnerId))
            query = query.Where(w => w.OwnerId == effectiveOwnerId);

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
        if (ws is null) return NotFound();
        if (!CanAccess(ws)) return Forbid();
        return Ok(ws);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWorkspaceDto dto, CancellationToken ct)
    {
        var workspace = new Workspace
        {
            Name = dto.Name,
            Description = dto.Description,
            OwnerId = _currentUser.GetUserId(),
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
        if (!CanAccess(ws)) return Forbid();

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
        if (!CanAccess(ws)) return Forbid();

        _db.Workspaces.Remove(ws);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private bool CanAccess(Workspace ws)
    {
        if (_currentUser.IsAdmin()) return true;
        return ws.OwnerId == _currentUser.GetUserId();
    }
}

public record CreateWorkspaceDto(string Name, string? Description, Guid? OrganizationId, Guid? TeamId, string? GitRepositoryUrl, string? GitBranch);
public record UpdateWorkspaceDto(string? Name, string? Description, string? GitBranch);
