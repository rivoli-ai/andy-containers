using Andy.Containers.Abstractions.Images;
using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Andy.Containers.Storage;
using Andy.Rbac.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ImagesController : ControllerBase
{
    private readonly ContainersDbContext _db;
    private readonly IImageManifestService _manifestService;
    private readonly IImageDiffService _diffService;
    private readonly ICurrentUserService _currentUser;
    private readonly IOrganizationMembershipService _orgMembership;
    private readonly IImageBuildOrchestrator _orchestrator;
    private readonly IAsyncBuildExecutor _executor;
    private readonly IBuildEventBus _eventBus;
    private readonly IBuildExecutionRegistry _executionRegistry;

    public ImagesController(
        ContainersDbContext db,
        IImageManifestService manifestService,
        IImageDiffService diffService,
        ICurrentUserService currentUser,
        IOrganizationMembershipService orgMembership,
        IImageBuildOrchestrator orchestrator,
        IAsyncBuildExecutor executor,
        IBuildEventBus eventBus,
        IBuildExecutionRegistry executionRegistry)
    {
        _db = db;
        _manifestService = manifestService;
        _diffService = diffService;
        _currentUser = currentUser;
        _orgMembership = orgMembership;
        _orchestrator = orchestrator;
        _executor = executor;
        _eventBus = eventBus;
        _executionRegistry = executionRegistry;
    }

    [RequirePermission("image:read")]
    [HttpGet("{templateId:guid}")]
    public async Task<IActionResult> List(Guid templateId, [FromQuery] Guid? organizationId = null, CancellationToken ct = default)
    {
        // Validate org membership if org filter specified
        if (organizationId.HasValue && !_currentUser.IsAdmin())
        {
            var isMember = await _orgMembership.IsMemberAsync(_currentUser.GetUserId(), organizationId.Value, ct);
            if (!isMember) return Forbid();
        }

        var query = _db.Images.Where(i => i.TemplateId == templateId);

        if (organizationId.HasValue)
        {
            // Show global images + org-specific images
            query = query.Where(i => i.OrganizationId == null || i.OrganizationId == organizationId);
        }
        else if (!_currentUser.IsAdmin())
        {
            // Non-admin without org filter: show only global images
            query = query.Where(i => i.OrganizationId == null);
        }

        var images = await query.OrderByDescending(i => i.BuildNumber).ToListAsync(ct);
        return Ok(images);
    }

    [RequirePermission("image:read")]
    [HttpGet("{templateId:guid}/latest")]
    public async Task<IActionResult> GetLatest(Guid templateId, [FromQuery] Guid? organizationId = null, CancellationToken ct = default)
    {
        if (organizationId.HasValue && !_currentUser.IsAdmin())
        {
            var isMember = await _orgMembership.IsMemberAsync(_currentUser.GetUserId(), organizationId.Value, ct);
            if (!isMember) return Forbid();
        }

        var query = _db.Images
            .Where(i => i.TemplateId == templateId && i.BuildStatus == ImageBuildStatus.Succeeded);

        if (organizationId.HasValue)
            query = query.Where(i => i.OrganizationId == null || i.OrganizationId == organizationId);
        else if (!_currentUser.IsAdmin())
            query = query.Where(i => i.OrganizationId == null);

        var image = await query.OrderByDescending(i => i.BuildNumber).FirstOrDefaultAsync(ct);
        return image is null ? NotFound() : Ok(image);
    }

    [RequirePermission("image:write")]
    [HttpPost("{templateId:guid}/build")]
    public async Task<IActionResult> Build(Guid templateId, [FromBody] BuildRequest? request, CancellationToken ct)
    {
        // IM8 (#262). Replaced the legacy ContainerImage path with
        // a delegation to IImageBuildOrchestrator. The orchestrator
        // owns cache-hit short-circuit, build invocation via
        // IBuildBackend, push via IRegistryAdapter, and persistence
        // of BuildArtifactEntity + RegistryReferenceEntity rows.
        // Response shape is BuildHandle per the IM5 OpenAPI.
        var template = await _db.Templates.FindAsync([templateId], ct);
        if (template is null) return NotFound();

        var organizationId = request?.OrganizationId;
        if (organizationId.HasValue && !_currentUser.IsAdmin())
        {
            var hasPermission = await _orgMembership.HasPermissionAsync(
                _currentUser.GetUserId(), organizationId.Value, Permissions.ImageBuild, ct);
            if (!hasPermission) return Forbid();
        }

        // IM9 (#263). Cache hits resolve synchronously through the
        // orchestrator's TryCacheHitAsync; cache misses queue a
        // background task and return immediately with status=queued.
        // Subscribers attach via /api/images/build/{buildId}/events
        // for the SSE stream of BuildProgressEvents.
        var ibuildRequest = new ImageBuildRequest(
            TemplateId: templateId,
            RegistryId: request?.RegistryId,
            Force: request?.Force ?? false,
            RequestedBy: _currentUser.GetUserId());

        var asyncHandle = await _executor.StartAsync(ibuildRequest, ct);

        return asyncHandle.Status switch
        {
            AsyncBuildHandleStatus.Cached =>
                Ok(MapCachedHandle(asyncHandle.Result!)),
            AsyncBuildHandleStatus.Queued =>
                AcceptedAtAction(
                    nameof(GetBuildStatus),
                    new { buildId = asyncHandle.BuildId },
                    new BuildHandle(
                        BuildId: asyncHandle.BuildId,
                        Status: "queued",
                        Digest: null,
                        References: [])),
            AsyncBuildHandleStatus.Failed =>
                BuildFailureResponse(asyncHandle.Result!),
            _ => StatusCode(500, "unexpected build status"),
        };
    }

    private BuildHandle MapCachedHandle(BuildResult result)
        => new(
            BuildId: result.BuildId,
            Status: "cached",
            Digest: result.Digest,
            References: result.References
                .Select(r => new BuildHandleReference(
                    RegistryId: r.RegistryId,
                    Ref: $"{r.RegistryId}/{r.RepoPath}:{r.Tag}",
                    PushedAt: r.PushedAt))
                .ToList());

    /// <summary>
    /// IM9 (#263). Build status snapshot. Reads from the in-memory
    /// execution registry; falls back to the persisted
    /// <see cref="Andy.Containers.Models.ImageManagement.BuildArtifactEntity"/>
    /// when a build is no longer in the registry (host restart, or
    /// the build completed long enough ago to be evicted).
    /// </summary>
    [RequirePermission("image:read")]
    [HttpGet("build/{buildId:guid}")]
    public Task<IActionResult> GetBuildStatus(Guid buildId, CancellationToken ct)
    {
        var state = _executionRegistry.TryGet(buildId);
        if (state is null)
        {
            return Task.FromResult<IActionResult>(
                ImageManagementProblemDetailsFactory.NotFound(
                    ImageManagementErrors.BuildNotFound,
                    $"no build with id {buildId} — either it never started or its registry record was evicted."));
        }

        return Task.FromResult<IActionResult>(Ok(new
        {
            buildId = state.BuildId,
            status = state.Status.ToString().ToLowerInvariant(),
            templateId = state.TemplateId,
            digest = state.Digest,
            references = state.References.Select(r => new
            {
                registryId = r.RegistryId,
                repoPath = r.RepoPath,
                tag = r.Tag,
                pushedAt = r.PushedAt,
            }).ToList(),
            startedAt = state.StartedAt,
            completedAt = state.CompletedAt,
            errorCode = state.ErrorCode,
            errorMessage = state.ErrorMessage,
        }));
    }

    /// <summary>
    /// IM9 (#263). Server-Sent Events stream of build progress.
    /// Reads from <see cref="IBuildEventBus"/>; events are emitted
    /// in publish order, including a buffered replay of any events
    /// that fired before the subscriber attached. The stream closes
    /// on the terminal <see cref="BuildCompletedEvent"/> or on
    /// client disconnect.
    /// </summary>
    [RequirePermission("image:read")]
    [HttpGet("build/{buildId:guid}/events")]
    public async Task BuildEvents(Guid buildId, CancellationToken ct)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Accel-Buffering"] = "no";

        // Honour Last-Event-ID for reconnection — the bus's buffered
        // replay will pick up after the supplied id (or restart from
        // the oldest buffered event if the id has aged out).
        long? lastEventId = null;
        if (Request.Headers.TryGetValue("Last-Event-ID", out var headerValue) &&
            long.TryParse(headerValue.ToString(), out var parsed))
        {
            lastEventId = parsed;
        }

        await foreach (var envelope in _eventBus.SubscribeAsync(buildId, lastEventId, ct))
        {
            await WriteSseAsync(envelope, ct);
        }
    }

    private async Task WriteSseAsync(BuildEventEnvelope envelope, CancellationToken ct)
    {
        // SSE wire format: id:, event:, data: lines, terminated by
        // a blank line. The event name is lowercase-kebab to match
        // the IM5 OpenAPI BuildEvent.type discriminator.
        var name = envelope.Event switch
        {
            BuildStepStartedEvent => "step-start",
            BuildStepStdoutEvent => "step-stdout",
            BuildStepErrorEvent => "step-error",
            BuildCompletedEvent => "complete",
            _ => "unknown",
        };
        var json = System.Text.Json.JsonSerializer.Serialize<object>(envelope.Event);

        // Manually compose the SSE frame so we control the trailing
        // \n\n. WriteAsync flushes the underlying response stream
        // implicitly; we also flush after each event so subscribers
        // see events in real time.
        var frame =
            $"id: {envelope.SequenceNumber}\n" +
            $"event: {name}\n" +
            $"data: {json}\n\n";
        await Response.WriteAsync(frame, ct);
        await Response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// IM10 (#264). Map a synchronous-failure
    /// <see cref="BuildResult"/> to the IM5
    /// <c>ImageManagementError</c> response shape via the shared
    /// <see cref="ImageManagementProblemDetailsFactory"/>. Replaces
    /// the inline mapping that was carried in IM8.
    /// </summary>
    private IActionResult BuildFailureResponse(BuildResult result)
        => ImageManagementProblemDetailsFactory.FromOrchestratorErrorCode(
            result.ErrorCode,
            result.ErrorMessage,
            result.FailureLog);

    /// <summary>
    /// IM5 BuildHandle response shape — async-build acknowledgement.
    /// </summary>
    public sealed record BuildHandle(
        Guid BuildId,
        string Status,
        string? Digest,
        IReadOnlyList<BuildHandleReference> References);

    public sealed record BuildHandleReference(
        string RegistryId,
        string Ref,
        DateTimeOffset PushedAt);

    [RequirePermission("image:read")]
    [HttpGet("diff")]
    public async Task<IActionResult> Diff([FromQuery] Guid fromImageId, [FromQuery] Guid toImageId, CancellationToken ct)
    {
        // Validate user can access both images
        if (!_currentUser.IsAdmin())
        {
            var userId = _currentUser.GetUserId();
            var fromImage = await _db.Images.AsNoTracking().FirstOrDefaultAsync(i => i.Id == fromImageId, ct);
            var toImage = await _db.Images.AsNoTracking().FirstOrDefaultAsync(i => i.Id == toImageId, ct);

            if (fromImage?.OrganizationId != null)
            {
                var canAccess = await _orgMembership.IsMemberAsync(userId, fromImage.OrganizationId.Value, ct);
                if (!canAccess) return Forbid();
            }
            if (toImage?.OrganizationId != null)
            {
                var canAccess = await _orgMembership.IsMemberAsync(userId, toImage.OrganizationId.Value, ct);
                if (!canAccess) return Forbid();
            }
        }

        try
        {
            var diff = await _diffService.DiffAsync(fromImageId, toImageId, ct);
            return Ok(diff);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [RequirePermission("image:read")]
    [HttpGet("{imageId:guid}/manifest")]
    public async Task<IActionResult> GetManifest(Guid imageId, CancellationToken ct)
    {
        var image = await _db.Images.FindAsync([imageId], ct);
        if (image is null) return NotFound();

        var manifest = await _manifestService.GetManifestAsync(imageId, ct);
        if (manifest is null) return NotFound("Image has not been introspected");

        return Ok(manifest);
    }

    [RequirePermission("image:read")]
    [HttpGet("{imageId:guid}/tools")]
    public async Task<IActionResult> GetTools(Guid imageId, CancellationToken ct)
    {
        var manifest = await _manifestService.GetManifestAsync(imageId, ct);
        if (manifest is null) return NotFound("Image has not been introspected");

        return Ok(manifest.Tools);
    }

    [RequirePermission("image:read")]
    [HttpGet("{imageId:guid}/packages")]
    public async Task<IActionResult> GetPackages(Guid imageId, CancellationToken ct)
    {
        var manifest = await _manifestService.GetManifestAsync(imageId, ct);
        if (manifest is null) return NotFound("Image has not been introspected");

        return Ok(manifest.OsPackages);
    }

    [RequirePermission("image:write")]
    [HttpPost("{imageId:guid}/introspect")]
    public async Task<IActionResult> Introspect(Guid imageId, CancellationToken ct)
    {
        var image = await _db.Images.FindAsync([imageId], ct);
        if (image is null) return NotFound();

        try
        {
            var (manifest, updatedImage) = await _manifestService.RefreshManifestAsync(imageId, ct);
            return Ok(new { Manifest = manifest, Image = updatedImage });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }
}

public record BuildRequest(
    bool Offline = false,
    bool Force = false,
    Guid? OrganizationId = null,
    string? RegistryId = null);
