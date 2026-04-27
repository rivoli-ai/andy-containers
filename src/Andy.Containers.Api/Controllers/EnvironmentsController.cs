using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Andy.Rbac.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Controllers;

/// <summary>
/// X3 (rivoli-ai/andy-containers#92). Read-only catalog of
/// <see cref="EnvironmentProfile"/>s. Profiles are governance artifacts —
/// seeded by <see cref="Data.EnvironmentProfileSeeder"/> at startup and
/// not user-creatable today. CRUD lands when an operator UI requires
/// it.
/// </summary>
/// <remarks>
/// The epic spec writes the path as <c>/containers/api/environments</c>;
/// the <c>containers/</c> prefix is a gateway/ingress concern, so the
/// controller route is the bare <c>api/environments</c>.
/// </remarks>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class EnvironmentsController : ControllerBase
{
    private readonly ContainersDbContext _db;

    public EnvironmentsController(ContainersDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    [RequirePermission(Permissions.EnvironmentRead)]
    public async Task<IActionResult> List(
        [FromQuery] string? kind,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (skip < 0) skip = 0;
        if (take <= 0 || take > 200) take = 50;

        EnvironmentKind? kindFilter = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!Enum.TryParse<EnvironmentKind>(kind, ignoreCase: true, out var parsed))
            {
                return BadRequest(new
                {
                    error = $"Unknown environment kind '{kind}'. " +
                            $"Expected one of: {string.Join(", ", Enum.GetNames<EnvironmentKind>())}.",
                });
            }
            kindFilter = parsed;
        }

        var query = _db.EnvironmentProfiles.AsNoTracking().AsQueryable();
        if (kindFilter.HasValue)
        {
            query = query.Where(p => p.Kind == kindFilter.Value);
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderBy(p => p.Name)
            .Skip(skip).Take(take)
            .ToListAsync(ct);

        var items = rows.Select(EnvironmentProfileDto.FromEntity).ToList();
        return Ok(new { items, totalCount = total });
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.EnvironmentRead)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var profile = await _db.EnvironmentProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null) return NotFound();
        return Ok(EnvironmentProfileDto.FromEntity(profile));
    }

    [HttpGet("by-code/{code}")]
    [RequirePermission(Permissions.EnvironmentRead)]
    public async Task<IActionResult> GetByCode(string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) return NotFound();

        var profile = await _db.EnvironmentProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Name == code, ct);
        if (profile is null) return NotFound();
        return Ok(EnvironmentProfileDto.FromEntity(profile));
    }
}
