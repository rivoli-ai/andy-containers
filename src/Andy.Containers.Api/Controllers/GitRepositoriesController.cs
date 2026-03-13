using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Controllers;

[ApiController]
[Route("api/containers/{containerId:guid}/repositories")]
[Authorize]
public class GitRepositoriesController : ControllerBase
{
    private readonly IGitCloneService _gitCloneService;
    private readonly ICurrentUserService _currentUser;
    private readonly ContainersDbContext _db;

    public GitRepositoriesController(IGitCloneService gitCloneService, ICurrentUserService currentUser, ContainersDbContext db)
    {
        _gitCloneService = gitCloneService;
        _currentUser = currentUser;
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> List(Guid containerId, CancellationToken ct)
    {
        var container = await _db.Containers.FirstOrDefaultAsync(c => c.Id == containerId, ct);
        if (container is null) return NotFound();
        if (!CanAccess(container.OwnerId)) return Forbid();

        var repos = await _gitCloneService.ListRepositoriesAsync(containerId, ct);
        return Ok(repos);
    }

    [HttpPost]
    public async Task<IActionResult> Clone(Guid containerId, [FromBody] GitCloneRequest request, CancellationToken ct)
    {
        var container = await _db.Containers.FirstOrDefaultAsync(c => c.Id == containerId, ct);
        if (container is null) return NotFound();
        if (!CanAccess(container.OwnerId)) return Forbid();

        try
        {
            var repo = await _gitCloneService.AddRepositoryAsync(containerId, request, ct);
            return CreatedAtAction(nameof(List), new { containerId }, repo);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpDelete("{repoId:guid}")]
    public async Task<IActionResult> Remove(Guid containerId, Guid repoId, CancellationToken ct)
    {
        var container = await _db.Containers.FirstOrDefaultAsync(c => c.Id == containerId, ct);
        if (container is null) return NotFound();
        if (!CanAccess(container.OwnerId)) return Forbid();

        var repo = await _gitCloneService.GetRepositoryAsync(containerId, repoId, ct);
        if (repo is null) return NotFound();

        _db.ContainerGitRepositories.Remove(repo);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private bool CanAccess(string ownerId)
    {
        if (_currentUser.IsAdmin()) return true;
        return ownerId == _currentUser.GetUserId();
    }
}
