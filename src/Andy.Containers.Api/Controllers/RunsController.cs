using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Andy.Rbac.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Controllers;

/// <summary>
/// AP2 (rivoli-ai/andy-containers#104). Entry point for agent runs:
/// submit a run spec, observe its state, request cancellation. Container
/// provisioning / selection is AP5's job — at AP2 the run lands as
/// <see cref="RunStatus.Pending"/> with <see cref="Run.ContainerId"/> null,
/// and the dispatcher (when it lands) takes over.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class RunsController : ControllerBase
{
    private readonly ContainersDbContext _db;

    public RunsController(ContainersDbContext db)
    {
        _db = db;
    }

    [HttpPost]
    [RequirePermission("run:write")]
    public async Task<IActionResult> Create(
        [FromBody] CreateRunRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        if (request.EnvironmentProfileId == Guid.Empty)
        {
            return BadRequest(new { error = "environmentProfileId must be a non-empty Guid." });
        }

        if (request.WorkspaceRef is { WorkspaceId: var wid } && wid == Guid.Empty)
        {
            return BadRequest(new { error = "workspaceRef.workspaceId must be a non-empty Guid when workspaceRef is provided." });
        }

        var now = DateTimeOffset.UtcNow;
        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = request.AgentId,
            AgentRevision = request.AgentRevision,
            Mode = request.Mode,
            EnvironmentProfileId = request.EnvironmentProfileId,
            WorkspaceRef = request.WorkspaceRef is null
                ? new WorkspaceRef()
                : new WorkspaceRef
                {
                    WorkspaceId = request.WorkspaceRef.WorkspaceId,
                    Branch = request.WorkspaceRef.Branch,
                },
            PolicyId = request.PolicyId,
            CorrelationId = request.CorrelationId ?? Guid.NewGuid(),
            Status = RunStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Runs.Add(run);
        await _db.SaveChangesAsync(ct);

        var dto = RunDto.FromEntity(run);
        return CreatedAtAction(nameof(Get), new { id = run.Id }, dto);
    }

    [HttpGet("{id:guid}")]
    [RequirePermission("run:read")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var run = await _db.Runs.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run is null)
        {
            return NotFound();
        }

        return Ok(RunDto.FromEntity(run));
    }

    [HttpPost("{id:guid}/cancel")]
    [RequirePermission("run:execute")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var run = await _db.Runs.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run is null)
        {
            return NotFound();
        }

        try
        {
            // Run.TransitionTo enforces the AP1 state-machine. Pending,
            // Provisioning, and Running can all move to Cancelled; terminal
            // states cannot — that's the 409 path below.
            run.TransitionTo(RunStatus.Cancelled);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        await _db.SaveChangesAsync(ct);

        // AP3+ also needs to publish a cancel command so the running container
        // actually stops; AP2 only flips the DB row. Document at the boundary.
        return Ok(RunDto.FromEntity(run));
    }
}
