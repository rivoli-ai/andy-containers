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
    private readonly ContainersDbContext _db;
    private readonly IGitCloneService _gitCloneService;

    public ContainersController(IContainerService containerService, ICurrentUserService currentUser, ContainersDbContext db, IGitCloneService gitCloneService)
    {
        _containerService = containerService;
        _currentUser = currentUser;
        _db = db;
        _gitCloneService = gitCloneService;
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

    [HttpGet("{id:guid}/repositories")]
    public async Task<IActionResult> ListRepositories(Guid id, CancellationToken ct)
    {
        var container = await _containerService.GetContainerAsync(id, ct);
        if (!CanAccess(container)) return Forbid();

        var repos = await _db.ContainerGitRepositories
            .Where(r => r.ContainerId == id)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);

        return Ok(repos.Select(r => new ContainerGitRepositoryDto(
            r.Id, r.Url, r.Branch, r.TargetPath, r.CloneDepth, r.Submodules,
            r.IsFromTemplate, r.CloneStatus.ToString(), r.CloneError,
            r.CloneStartedAt, r.CloneCompletedAt)));
    }

    [HttpPost("{id:guid}/repositories")]
    public async Task<IActionResult> AddRepository(Guid id, [FromBody] AddRepositoryDto dto, CancellationToken ct)
    {
        var container = await _containerService.GetContainerAsync(id, ct);
        if (!CanAccess(container)) return Forbid();

        if (container.Status != ContainerStatus.Running)
            return BadRequest(new { error = $"Container is {container.Status}, must be Running to add repositories" });

        var config = new GitRepositoryConfig
        {
            Url = dto.Url,
            Branch = dto.Branch,
            TargetPath = dto.TargetPath,
            CloneDepth = dto.CloneDepth,
            Submodules = dto.Submodules
        };

        var errors = GitRepositoryValidator.Validate(config);
        if (errors.Count > 0)
            return BadRequest(new { errors });

        var repo = new ContainerGitRepository
        {
            ContainerId = id,
            Url = dto.Url,
            Branch = dto.Branch,
            TargetPath = dto.TargetPath ?? "/workspace",
            CredentialRef = dto.CredentialRef,
            CloneDepth = dto.CloneDepth,
            Submodules = dto.Submodules,
            CloneStatus = GitCloneStatus.Pending
        };
        _db.ContainerGitRepositories.Add(repo);
        await _db.SaveChangesAsync(ct);

        // Immediately clone
        var cloned = await _gitCloneService.CloneRepositoryAsync(id, repo.Id, ct);

        return CreatedAtAction(nameof(ListRepositories), new { id },
            new ContainerGitRepositoryDto(
                cloned.Id, cloned.Url, cloned.Branch, cloned.TargetPath, cloned.CloneDepth, cloned.Submodules,
                cloned.IsFromTemplate, cloned.CloneStatus.ToString(), cloned.CloneError,
                cloned.CloneStartedAt, cloned.CloneCompletedAt));
    }

    [HttpPost("{id:guid}/repositories/{repoId:guid}/pull")]
    public async Task<IActionResult> PullRepository(Guid id, Guid repoId, CancellationToken ct)
    {
        var container = await _containerService.GetContainerAsync(id, ct);
        if (!CanAccess(container)) return Forbid();

        if (container.Status != ContainerStatus.Running)
            return BadRequest(new { error = $"Container is {container.Status}, must be Running to pull" });

        try
        {
            var repo = await _gitCloneService.PullRepositoryAsync(id, repoId, ct);
            return Ok(new ContainerGitRepositoryDto(
                repo.Id, repo.Url, repo.Branch, repo.TargetPath, repo.CloneDepth, repo.Submodules,
                repo.IsFromTemplate, repo.CloneStatus.ToString(), repo.CloneError,
                repo.CloneStartedAt, repo.CloneCompletedAt));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    private bool CanAccess(Container container)
    {
        if (_currentUser.IsAdmin()) return true;
        return container.OwnerId == _currentUser.GetUserId();
    }
}

public record ContainerGitRepositoryDto(
    Guid Id, string Url, string? Branch, string TargetPath, int? CloneDepth, bool Submodules,
    bool IsFromTemplate, string CloneStatus, string? CloneError,
    DateTime? CloneStartedAt, DateTime? CloneCompletedAt);

public record AddRepositoryDto
{
    public required string Url { get; init; }
    public string? Branch { get; init; }
    public string? TargetPath { get; init; }
    public string? CredentialRef { get; init; }
    public int? CloneDepth { get; init; }
    public bool Submodules { get; init; }
}

public class ExecRequest
{
    public required string Command { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}
