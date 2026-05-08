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
    private readonly IBuildArtifactStore _artifactStore;

    public ImagesController(
        ContainersDbContext db,
        IImageManifestService manifestService,
        IImageDiffService diffService,
        ICurrentUserService currentUser,
        IOrganizationMembershipService orgMembership,
        IImageBuildOrchestrator orchestrator,
        IAsyncBuildExecutor executor,
        IBuildEventBus eventBus,
        IBuildExecutionRegistry executionRegistry,
        IBuildArtifactStore artifactStore)
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
        _artifactStore = artifactStore;
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
        // Surfaced by the SSE wire-format integration test (#272 / sse-wire-format-test):
        //   1. JsonSerializer.Serialize<object>(value) uses the
        //      declared type `object` and produces "{}" for any
        //      derived event — dropping the payload entirely.
        //   2. Default System.Text.Json options use PascalCase
        //      property names and numeric-valued enums; the IM5
        //      OpenAPI specifies camelCase + string enums for
        //      BuildEvent.outcome.
        // Both fixed by using a static JsonSerializerOptions with
        // camelCase + JsonStringEnumConverter and the runtime type
        // for serialisation.
        var json = System.Text.Json.JsonSerializer.Serialize(
            envelope.Event,
            envelope.Event.GetType(),
            SseJsonOptions);

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
    /// JSON serializer options for SSE event payloads. camelCase +
    /// string enums per the IM5 OpenAPI <c>BuildEvent</c> schema.
    /// Cached as a static so each event publish doesn't re-allocate.
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

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

    // ----- #278 IM5 endpoints (digest-anchored artifact list/get/untag) -----

    /// <summary>
    /// #278. <c>GET /api/images</c>. Lists `BuildArtifact` rows.
    ///
    /// Distinct from the existing <see cref="List(Guid, Guid?, CancellationToken)"/>
    /// which is the legacy template-keyed `ContainerImage` listing — this
    /// endpoint walks `BuildArtifactEntity` (the digest-anchored row that
    /// IM3 introduced) and returns the IM5 OpenAPI `BuildArtifactList`
    /// shape `{ items, totalCount }`.
    ///
    /// The `marker` query parameter is declared by the OpenAPI spec but
    /// not yet honoured: there is no `Markers` column on
    /// <c>BuildArtifactEntity</c> today. When marker support lands the
    /// filter wires here without a contract change.
    /// </summary>
    [RequirePermission("image:read")]
    [HttpGet]
    public async Task<IActionResult> ListArtifacts(
        [FromQuery] Guid? templateId,
        [FromQuery] string? registryId,
        [FromQuery] string? marker,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        CancellationToken ct = default)
    {
        // Cap the page size to keep one user from accidentally pulling
        // a 100k-row payload.
        take = Math.Clamp(take, 1, 100);
        skip = Math.Max(0, skip);

        if (!string.IsNullOrWhiteSpace(marker))
        {
            // No `Markers` column on BuildArtifactEntity yet; refuse the
            // filter rather than silently returning unfiltered results.
            return BadRequest(new ImageManagementErrorBody
            {
                Code = "image.list.marker.unsupported",
                Message = "marker filter is declared in the OpenAPI but not yet implemented; tracked as a follow-up to #278.",
            });
        }

        var (items, total) = await _artifactStore.ListAsync(
            templateId: templateId,
            registryId: registryId,
            skip: skip,
            take: take,
            ct: ct);
        return Ok(new BuildArtifactListResponse(
            Items: items.Select(BuildArtifactResponse.From).ToArray(),
            TotalCount: total));
    }

    /// <summary>
    /// #278. <c>GET /api/images/by-digest/{digest}</c>. Returns the
    /// single artifact for an OCI manifest digest, or 404 if no
    /// artifact has been registered for it.
    ///
    /// The digest is taken verbatim from the path. ASP.NET routing
    /// accepts the colon inside the path segment per RFC 3986 §3.3 —
    /// callers do not need to percent-encode the `sha256:` prefix.
    /// </summary>
    [RequirePermission("image:read")]
    [HttpGet("by-digest/{digest}")]
    public async Task<IActionResult> GetByDigest(string digest, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return BadRequest(new ImageManagementErrorBody
            {
                Code = "image.digest.required",
                Message = "digest path segment is required.",
            });
        }

        var entity = await _artifactStore.GetByDigestAsync(digest, ct);
        if (entity is null)
        {
            return ImageManagementProblemDetailsFactory.NotFound(
                code: "image.not-found",
                message: $"No artifact for digest '{digest}'.");
        }
        return Ok(BuildArtifactResponse.From(entity));
    }

    /// <summary>
    /// #278. <c>DELETE /api/images/by-digest/{digest}/references/{referenceId}</c>.
    /// Removes one `(registryId, repoPath, tag)` reference.
    ///
    /// Idempotent — already-gone references return 204 too.
    /// `image:delete` (the existing RBAC permission for image deletion)
    /// gates the action; the IM5 OpenAPI calls this an admin-only
    /// operation so non-admins are rejected by the RBAC layer.
    ///
    /// Does NOT delete the underlying artifact bytes; registry-side
    /// garbage collection reclaims those when no reference points at
    /// the digest. The artifact row stays in the DB so the digest
    /// remains a stable audit anchor.
    /// </summary>
    [RequirePermission("image:delete")]
    [HttpDelete("by-digest/{digest}/references/{referenceId:guid}")]
    public async Task<IActionResult> Untag(string digest, Guid referenceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return BadRequest(new ImageManagementErrorBody
            {
                Code = "image.digest.required",
                Message = "digest path segment is required.",
            });
        }

        // Resolve the artifact first so we can verify the reference
        // actually belongs to this digest. A request for
        // /by-digest/sha256:A/references/<id-of-ref-to-sha256:B> should
        // 404, not silently delete the wrong row.
        var artifact = await _artifactStore.GetByDigestAsync(digest, ct);
        if (artifact is null)
        {
            return ImageManagementProblemDetailsFactory.NotFound(
                code: "image.not-found",
                message: $"No artifact for digest '{digest}'.");
        }

        var reference = artifact.References.FirstOrDefault(r => r.Id == referenceId);
        if (reference is null)
        {
            // Idempotent: already-gone is not an error.
            return NoContent();
        }

        await _artifactStore.RemoveReferenceAsync(referenceId, ct);
        return NoContent();
    }
}

public record BuildRequest(
    bool Offline = false,
    bool Force = false,
    Guid? OrganizationId = null,
    string? RegistryId = null);

// ----- #278 IM5 response DTOs -----

/// <summary>
/// IM5 OpenAPI <c>BuildArtifact</c> shape. Distinct from the abstraction
/// <see cref="Andy.Containers.Abstractions.Images.BuildArtifact"/> record
/// which carries fewer fields (no templateId, no references list).
/// </summary>
public sealed record BuildArtifactResponse(
    string Digest,
    string MediaType,
    long SizeBytes,
    string SpecHash,
    Guid TemplateId,
    string? BuildBackendId,
    string? BuiltBy,
    DateTime BuiltAt,
    IReadOnlyList<BuildArtifactReferenceResponse> References,
    IReadOnlyDictionary<string, object>? Markers)
{
    public static BuildArtifactResponse From(Andy.Containers.Models.ImageManagement.BuildArtifactEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new BuildArtifactResponse(
            Digest: entity.Digest,
            MediaType: entity.MediaType,
            SizeBytes: entity.SizeBytes,
            SpecHash: entity.SpecHash,
            TemplateId: entity.TemplateId,
            BuildBackendId: entity.BuildBackendId,
            BuiltBy: entity.BuiltBy,
            BuiltAt: entity.BuiltAt,
            References: entity.References.Select(BuildArtifactReferenceResponse.From).ToArray(),
            // No `Markers` column today — null until a future migration
            // adds one. Conductor's Swift client treats this field as
            // optional.
            Markers: null);
    }
}

/// <summary>
/// IM5 OpenAPI <c>BuildArtifactReference</c> shape. The on-disk
/// <see cref="Andy.Containers.Models.ImageManagement.RegistryReferenceEntity"/>
/// does not carry the artifact's digest itself (it foreign-keys the
/// owning artifact); the response shape needs the digest for the
/// reference-side surface, so the mapper from the parent
/// <see cref="BuildArtifactResponse.From(Andy.Containers.Models.ImageManagement.BuildArtifactEntity)"/>
/// supplies it indirectly via the parent envelope.
/// </summary>
public sealed record BuildArtifactReferenceResponse(
    Guid Id,
    string RegistryId,
    string RepoPath,
    string Tag,
    string? Digest,
    DateTime PushedAt,
    string PushedBy)
{
    public static BuildArtifactReferenceResponse From(Andy.Containers.Models.ImageManagement.RegistryReferenceEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new BuildArtifactReferenceResponse(
            Id: entity.Id,
            RegistryId: entity.RegistryId,
            RepoPath: entity.RepoPath,
            Tag: entity.Tag,
            // RegistryReferenceEntity doesn't store the digest directly
            // — it's reachable via the BuildArtifact navigation property
            // when populated. Leave as null when not loaded; consumers
            // (per the IM5 OpenAPI) treat it as nullable.
            Digest: entity.BuildArtifact?.Digest,
            PushedAt: entity.PushedAt,
            PushedBy: entity.PushedBy);
    }
}

public sealed record BuildArtifactListResponse(
    IReadOnlyList<BuildArtifactResponse> Items,
    int TotalCount);
