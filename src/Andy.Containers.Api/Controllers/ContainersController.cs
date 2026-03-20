using Andy.Containers.Abstractions;
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
public class ContainersController : ControllerBase
{
    private readonly IContainerService _containerService;
    private readonly ICurrentUserService _currentUser;
    private readonly IContainerPermissionService _permissions;
    private readonly ContainersDbContext _db;
    private readonly ISshKeyService _sshKeyService;
    private readonly ISshProvisioningService _sshProvisioning;

    public ContainersController(IContainerService containerService, ICurrentUserService currentUser,
        IContainerPermissionService permissions, ContainersDbContext db,
        ISshKeyService sshKeyService, ISshProvisioningService sshProvisioning)
    {
        _containerService = containerService;
        _currentUser = currentUser;
        _permissions = permissions;
        _db = db;
        _sshKeyService = sshKeyService;
        _sshProvisioning = sshProvisioning;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? ownerId,
        [FromQuery] Guid? organizationId,
        [FromQuery] Guid? teamId,
        [FromQuery] Guid? workspaceId,
        [FromQuery] ContainerStatus? status,
        [FromQuery] Guid? templateId,
        [FromQuery] Guid? providerId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        CancellationToken ct = default)
    {
        // Non-admins can only see their own containers
        var effectiveOwnerId = ownerId;
        if (!_currentUser.IsAdmin())
            effectiveOwnerId = _currentUser.GetUserId();

        var containers = await _containerService.ListContainersAsync(new ContainerFilter
        {
            OwnerId = effectiveOwnerId,
            OrganizationId = organizationId,
            TeamId = teamId,
            WorkspaceId = workspaceId,
            Status = status,
            TemplateId = templateId,
            ProviderId = providerId,
            Skip = skip,
            Take = take
        }, ct);

        return Ok(new { items = containers, totalCount = containers.Count });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        try
        {
            var container = await _containerService.GetContainerAsync(id, ct);
            if (!CanAccess(container))
                return Forbid();
            return Ok(container);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateContainerRequest request, CancellationToken ct)
    {
        request.OwnerId = _currentUser.GetUserId();
        var container = await _containerService.CreateContainerAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = container.Id }, container);
    }

    [HttpPost("{id:guid}/start")]
    public async Task<IActionResult> Start(Guid id, CancellationToken ct)
    {
        var container = await _containerService.GetContainerAsync(id, ct);
        if (!CanAccess(container)) return Forbid();

        await _containerService.StartContainerAsync(id, ct);
        container = await _containerService.GetContainerAsync(id, ct);
        return Ok(container);
    }

    [HttpPost("{id:guid}/stop")]
    public async Task<IActionResult> Stop(Guid id, CancellationToken ct)
    {
        var container = await _containerService.GetContainerAsync(id, ct);
        if (!CanAccess(container)) return Forbid();

        await _containerService.StopContainerAsync(id, ct);
        container = await _containerService.GetContainerAsync(id, ct);
        return Ok(container);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Destroy(Guid id, CancellationToken ct)
    {
        var container = await _containerService.GetContainerAsync(id, ct);
        if (!CanAccess(container)) return Forbid();

        await _containerService.DestroyContainerAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/exec")]
    public async Task<IActionResult> Exec(Guid id, [FromBody] ExecRequest request, CancellationToken ct)
    {
        var container = await _containerService.GetContainerAsync(id, ct);
        if (!CanAccess(container)) return Forbid();

        var result = await _containerService.ExecAsync(id, request.Command, ct);
        return Ok(result);
    }

    [HttpGet("{id:guid}/connection")]
    public async Task<IActionResult> GetConnectionInfo(Guid id, CancellationToken ct)
    {
        var container = await _containerService.GetContainerAsync(id, ct);
        if (!CanAccess(container)) return Forbid();

        var info = await _containerService.GetConnectionInfoAsync(id, ct);
        return Ok(info);
    }

    [HttpGet("{id:guid}/events")]
    public async Task<IActionResult> GetEvents(Guid id, CancellationToken ct)
    {
        var container = await _containerService.GetContainerAsync(id, ct);
        if (!CanAccess(container)) return Forbid();

        var events = await _db.Events
            .Where(e => e.ContainerId == id)
            .OrderByDescending(e => e.Timestamp)
            .Take(50)
            .ToListAsync(ct);
        return Ok(events);
    }

    // === Story 3: SSH Access ===

    [HttpPost("{id:guid}/ssh/enable")]
    public async Task<IActionResult> EnableSsh(Guid id, CancellationToken ct)
    {
        var container = await _containerService.GetContainerAsync(id, ct);
        if (!await _permissions.HasPermissionAsync(_currentUser.GetUserId(), id, "container:connect", ct))
            return Forbid();

        var dbContainer = await _db.Containers.FindAsync([id], ct);
        if (dbContainer is null) return NotFound();

        var keys = await _sshKeyService.ListKeysAsync(_currentUser.GetUserId(), ct);
        if (keys.Count == 0)
            return BadRequest(new { error = "You must have at least one SSH key registered to enable SSH access" });

        dbContainer.SshEnabled = true;
        await _db.SaveChangesAsync(ct);

        var publicKeys = keys.Select(k => k.PublicKey).ToList();
        var config = new Models.SshConfig();
        var script = _sshProvisioning.GenerateSetupScript(config, publicKeys);

        return Ok(new { enabled = true, setupScript = script });
    }

    [HttpPost("{id:guid}/ssh/disable")]
    public async Task<IActionResult> DisableSsh(Guid id, CancellationToken ct)
    {
        var container = await _containerService.GetContainerAsync(id, ct);
        if (!await _permissions.HasPermissionAsync(_currentUser.GetUserId(), id, "container:connect", ct))
            return Forbid();

        var dbContainer = await _db.Containers.FindAsync([id], ct);
        if (dbContainer is null) return NotFound();

        dbContainer.SshEnabled = false;
        await _db.SaveChangesAsync(ct);
        return Ok(new { enabled = false });
    }

    private bool CanAccess(Container container)
    {
        if (_currentUser.IsAdmin()) return true;
        return container.OwnerId == _currentUser.GetUserId();
    }
}

public class ExecRequest
{
    public required string Command { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}
