using System.Text.Json;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Andy.Rbac.Authorization;
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
    private readonly IOrganizationMembershipService _orgMembership;
    private readonly IAgentCapabilityService _agentCapabilities;
    private readonly ILogger<WorkspacesController> _logger;

    public WorkspacesController(
        ContainersDbContext db,
        ICurrentUserService currentUser,
        IOrganizationMembershipService orgMembership,
        IAgentCapabilityService agentCapabilities,
        ILogger<WorkspacesController> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _orgMembership = orgMembership;
        _agentCapabilities = agentCapabilities;
        _logger = logger;
    }

    [RequirePermission("workspace:read")]
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

        if (organizationId.HasValue && !_currentUser.IsAdmin())
        {
            var isMember = await _orgMembership.IsMemberAsync(_currentUser.GetUserId(), organizationId.Value, ct);
            if (!isMember) return Forbid();
        }

        if (organizationId.HasValue)
            query = query.Where(w => w.OrganizationId == organizationId);
        if (status.HasValue)
            query = query.Where(w => w.Status == status);

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(w => w.CreatedAt).Skip(skip).Take(take).ToListAsync(ct);
        return Ok(new { items, totalCount = total });
    }

    [RequirePermission("workspace:read")]
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var ws = await _db.Workspaces.Include(w => w.DefaultContainer).Include(w => w.Containers).FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws is null) return NotFound();
        if (!CanAccess(ws)) return Forbid();
        return Ok(ws);
    }

    [RequirePermission("workspace:write")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWorkspaceDto dto, CancellationToken ct)
    {
        if (dto.OrganizationId.HasValue && !_currentUser.IsAdmin())
        {
            var isMember = await _orgMembership.IsMemberAsync(_currentUser.GetUserId(), dto.OrganizationId.Value, ct);
            if (!isMember) return Forbid();
        }

        // X5 (rivoli-ai/andy-containers#94). Profile binding is required at
        // create time — it's the governance anchor for every container the
        // workspace later provisions (X4 substitutes image + GuiType from
        // the bound profile). 400 on missing/unknown rather than letting
        // the row land profile-less and surfacing the gap downstream.
        if (string.IsNullOrWhiteSpace(dto.EnvironmentProfileCode))
        {
            return BadRequest(new
            {
                error = "environmentProfileCode is required (e.g. 'headless-container').",
            });
        }

        var profile = await _db.EnvironmentProfiles
            .FirstOrDefaultAsync(p => p.Name == dto.EnvironmentProfileCode, ct);
        if (profile is null)
        {
            return BadRequest(new
            {
                error = $"EnvironmentProfile '{dto.EnvironmentProfileCode}' not found in catalog.",
            });
        }

        // X9 (rivoli-ai/andy-containers#99). When the workspace targets a
        // specific agent, enforce the agent's allowed_environments
        // declaration. Agents without a policy stay open (null
        // allowlist); agents with one are enforced strictly. Service
        // outage fails closed — better to refuse provisioning than
        // bind a workspace to an unverifiable policy.
        if (!string.IsNullOrWhiteSpace(dto.AgentId))
        {
            IReadOnlyList<string>? allowed;
            try
            {
                allowed = await _agentCapabilities.GetAllowedEnvironmentsAsync(dto.AgentId, ct);
            }
            catch (AgentCapabilityServiceUnavailableException ex)
            {
                _logger.LogError(ex,
                    "Agent capability service unavailable while creating workspace for agent {AgentId}",
                    dto.AgentId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    error = "Agent capability service unavailable; cannot validate allowed_environments.",
                });
            }

            if (allowed is not null
                && !allowed.Any(code => string.Equals(code, profile.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    error = $"Profile '{profile.Name}' is not in agent '{dto.AgentId}' allowed_environments.",
                });
            }
        }

        // Validate uniqueness of git repo URLs
        var repos = dto.GitRepositories ?? [];
        var duplicateUrl = repos.GroupBy(r => r.Url, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateUrl is not null)
            return BadRequest(new { error = $"Duplicate repository URL: {duplicateUrl.Key}" });

        var workspace = new Workspace
        {
            Name = dto.Name,
            Description = dto.Description,
            OwnerId = _currentUser.GetUserId(),
            OrganizationId = dto.OrganizationId,
            TeamId = dto.TeamId,
            GitRepositoryUrl = dto.GitRepositoryUrl,
            GitBranch = dto.GitBranch,
            GitRepositories = repos.Count > 0 ? JsonSerializer.Serialize(repos) : null,
            EnvironmentProfileId = profile.Id,
        };
        _db.Workspaces.Add(workspace);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = workspace.Id }, workspace);
    }

    [RequirePermission("workspace:write")]
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateWorkspaceDto dto, CancellationToken ct)
    {
        var ws = await _db.Workspaces.FindAsync([id], ct);
        if (ws is null) return NotFound();
        if (!CanAccess(ws)) return Forbid();

        if (dto.Name is not null) ws.Name = dto.Name;
        if (dto.Description is not null) ws.Description = dto.Description;
        if (dto.GitBranch is not null) ws.GitBranch = dto.GitBranch;

        if (dto.GitRepositories is not null)
        {
            var duplicateUrl = dto.GitRepositories.GroupBy(r => r.Url, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicateUrl is not null)
                return BadRequest(new { error = $"Duplicate repository URL: {duplicateUrl.Key}" });

            ws.GitRepositories = dto.GitRepositories.Count > 0 ? JsonSerializer.Serialize(dto.GitRepositories) : null;
        }

        ws.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(ws);
    }

    [RequirePermission("workspace:delete")]
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

public record WorkspaceGitRepoDto(string Url, string? Branch = null, string? CredentialRef = null, string? TargetPath = null);
// X5 (rivoli-ai/andy-containers#94): EnvironmentProfileCode is the slug from
// the X3 catalog (e.g. "headless-container"). Optional in the C# signature
// so existing callers don't break at compile time, but the controller
// validates it as required and returns 400 when omitted on Create.
//
// X9 (rivoli-ai/andy-containers#99): AgentId is optional — when set, the
// controller calls IAgentCapabilityService and rejects the create with
// 403 if the workspace's profile isn't in the agent's allowlist. Omit
// to keep the workspace agent-agnostic (run-submit time becomes the
// natural enforcement point in that case).
public record CreateWorkspaceDto(string Name, string? Description, Guid? OrganizationId, Guid? TeamId, string? GitRepositoryUrl, string? GitBranch, List<WorkspaceGitRepoDto>? GitRepositories = null, string? EnvironmentProfileCode = null, string? AgentId = null);
public record UpdateWorkspaceDto(string? Name, string? Description, string? GitBranch, List<WorkspaceGitRepoDto>? GitRepositories = null);
