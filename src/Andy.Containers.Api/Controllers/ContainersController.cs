using Andy.Containers.Abstractions;
using Andy.Containers.Models;
using Microsoft.AspNetCore.Mvc;

namespace Andy.Containers.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ContainersController : ControllerBase
{
    private readonly IContainerService _containerService;

    public ContainersController(IContainerService containerService)
    {
        _containerService = containerService;
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
        var containers = await _containerService.ListContainersAsync(new ContainerFilter
        {
            OwnerId = ownerId,
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
        var container = await _containerService.CreateContainerAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = container.Id }, container);
    }

    [HttpPost("{id:guid}/start")]
    public async Task<IActionResult> Start(Guid id, CancellationToken ct)
    {
        await _containerService.StartContainerAsync(id, ct);
        var container = await _containerService.GetContainerAsync(id, ct);
        return Ok(container);
    }

    [HttpPost("{id:guid}/stop")]
    public async Task<IActionResult> Stop(Guid id, CancellationToken ct)
    {
        await _containerService.StopContainerAsync(id, ct);
        var container = await _containerService.GetContainerAsync(id, ct);
        return Ok(container);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Destroy(Guid id, CancellationToken ct)
    {
        await _containerService.DestroyContainerAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/exec")]
    public async Task<IActionResult> Exec(Guid id, [FromBody] ExecRequest request, CancellationToken ct)
    {
        var result = await _containerService.ExecAsync(id, request.Command, ct);
        return Ok(result);
    }

    [HttpGet("{id:guid}/connection")]
    public async Task<IActionResult> GetConnectionInfo(Guid id, CancellationToken ct)
    {
        var info = await _containerService.GetConnectionInfoAsync(id, ct);
        return Ok(info);
    }
}

public class ExecRequest
{
    public required string Command { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}
