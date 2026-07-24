using Andy.Containers.Abstractions;
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
public class ContainersController : ControllerBase
{
    private readonly IContainerService _containerService;
    private readonly ICurrentUserService _currentUser;
    private readonly ContainersDbContext _db;
    private readonly IGitCloneService _gitCloneService;
    private readonly IGitCredentialService _credentialService;
    private readonly IGitRepositoryProbeService _probeService;
    private readonly IOrganizationMembershipService _orgMembership;
    private readonly IGitDiffService _gitDiffService;
    private readonly IPortDiscoveryService _portDiscoveryService;
    private readonly IContainerLifecycleBus _lifecycleBus;
    private readonly IRunOutputBus _outputBus;

    public ContainersController(
        IContainerService containerService,
        ICurrentUserService currentUser,
        ContainersDbContext db,
        IGitCloneService gitCloneService,
        IGitCredentialService credentialService,
        IGitRepositoryProbeService probeService,
        IOrganizationMembershipService orgMembership,
        IGitDiffService gitDiffService,
        IPortDiscoveryService portDiscoveryService,
        IContainerLifecycleBus lifecycleBus,
        IRunOutputBus outputBus)
    {
        _containerService = containerService;
        _currentUser = currentUser;
        _db = db;
        _gitCloneService = gitCloneService;
        _credentialService = credentialService;
        _probeService = probeService;
        _orgMembership = orgMembership;
        _gitDiffService = gitDiffService;
        _portDiscoveryService = portDiscoveryService;
        _lifecycleBus = lifecycleBus;
        _outputBus = outputBus;
    }

    [HttpGet]
    [RequirePermission("container:read")]
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

        if (organizationId.HasValue && !_currentUser.IsAdmin())
        {
            var isMember = await _orgMembership.IsMemberAsync(_currentUser.GetUserId(), organizationId.Value, ct);
            if (!isMember) return Forbid();
        }

        var filter = new ContainerFilter
        {
            OwnerId = effectiveOwnerId,
            OrganizationId = organizationId,
            TeamId = teamId,
            WorkspaceId = workspaceId,
            Status = status,
            TemplateId = templateId,
            ProviderId = providerId,
        };

        // Build count query with same filters but no skip/take
        var countQuery = _db.Containers.AsQueryable();
        if (!string.IsNullOrEmpty(filter.OwnerId))
            countQuery = countQuery.Where(c => c.OwnerId == filter.OwnerId);
        if (filter.OrganizationId.HasValue)
            countQuery = countQuery.Where(c => c.OrganizationId == filter.OrganizationId);
        if (filter.TeamId.HasValue)
            countQuery = countQuery.Where(c => c.TeamId == filter.TeamId);
        if (filter.Status.HasValue)
            countQuery = countQuery.Where(c => c.Status == filter.Status);
        if (filter.TemplateId.HasValue)
            countQuery = countQuery.Where(c => c.TemplateId == filter.TemplateId);
        if (filter.ProviderId.HasValue)
            countQuery = countQuery.Where(c => c.ProviderId == filter.ProviderId);

        filter.Skip = skip;
        filter.Take = take;
        var containers = await _containerService.ListContainersAsync(filter, ct);
        var totalCount = await countQuery.CountAsync(ct);

        return Ok(new { items = containers, totalCount });
    }

    /// <summary>
    /// SM.2.6 (rivoli-ai/conductor#2008). Fleet-wide container lifecycle
    /// phase SSE stream. Emits one <c>event: lifecycle</c> frame per
    /// container state transition (pending → creating → running → … →
    /// failed / exited / destroyed). Heartbeat comment frames every 15 s.
    /// The stream stays open until the client disconnects.
    /// </summary>
    /// <remarks>
    /// Wire format (docs/api-contracts/andy-containers.md §lifecycle):
    /// <code>
    /// id: &lt;sequence&gt;
    /// event: lifecycle
    /// data: {"containerId":"...","phase":"running","phaseData":{},"correlationId":"...","timestamp":"..."}
    /// </code>
    /// Resume via <c>Last-Event-ID</c>; the bus replays buffered events
    /// from after the supplied id. On buffer miss (id too old) replay
    /// restarts from the oldest buffered event.
    /// RBAC: <c>container:read</c>. The bus delivers fleet-wide events
    /// internally; this endpoint filters every replayed and live event through
    /// the same owner/admin/service/same-organisation read policy used by
    /// <c>GET /api/containers/{id}</c>.
    /// </remarks>
    [HttpGet("events")]
    [RequirePermission("container:read")]
    public Task Events(CancellationToken ct)
        => ContainerLifecycleSse.StreamAsync(
            Response,
            Request,
            _lifecycleBus,
            ct,
            IsLifecycleEventVisibleAsync);

    private async ValueTask<bool> IsLifecycleEventVisibleAsync(
        ContainerLifecycleEnvelope envelope,
        CancellationToken ct)
    {
        // Fail closed when a row no longer exists. Lifecycle publishers emit
        // destroyed before removing state, so legitimate terminal events are
        // still visible; an unknown id must never become an authorization
        // bypass. AsNoTracking avoids retaining an unbounded fleet in the
        // request-scoped context during a long-lived SSE connection.
        var container = await _db.Containers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == envelope.Event.ContainerId, ct);
        return container is not null && await CanReadAsync(container, ct);
    }

    /// <summary>
    /// SM.2.6 (rivoli-ai/conductor#2008). Classified GET — returns:
    /// <list type="bullet">
    ///   <item><c>200 OK</c> — container found and accessible.</item>
    ///   <item><c>404 Not Found</c> — container confirmed deleted or never
    ///     existed (SUSTAINED; caller SHOULD NOT retry without user action).
    ///     Body: <c>{ code, message, correlationId }</c>.</item>
    ///   <item><c>503 Service Unavailable</c> — infrastructure provider
    ///     transiently unreachable; row exists but runtime state unknown
    ///     (TRANSIENT; caller SHOULD retry after <c>Retry-After</c>
    ///     seconds). Body: <c>{ code, message, correlationId }</c>.</item>
    ///   <item><c>401 Unauthorized</c> — missing / invalid bearer token
    ///     (standard HTTP auth, handled by middleware before reaching this
    ///     action, but documented for Conductor's §7.2 classifier).</item>
    /// </list>
    /// The <c>X-Correlation-Id</c> response header mirrors
    /// <c>correlationId</c> in the body so log aggregators can correlate
    /// without parsing JSON.
    /// </summary>
    [HttpGet("{id:guid}")]
    [RequirePermission("container:read")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        try
        {
            var container = await _containerService.GetContainerAsync(id, ct);
            // #366: read-scoped access (owner | admin | same-org member) so a
            // human session can poll a goal-execution container owned by the
            // goal owner. Write/lifecycle verbs keep strict owner-equality.
            if (!await CanReadAsync(container, ct))
                return Forbid();

            // SM.2.6: attach correlation id header to every 200 response so
            // the Conductor §7.2 helper can correlate status responses with
            // concurrent lifecycle SSE events.
            var correlationId = container.StoryId ?? container.Id;
            Response.Headers["X-Correlation-Id"] = correlationId.ToString();
            return Ok(container);
        }
        catch (KeyNotFoundException)
        {
            // Confirmed deletion — sustained 404.
            var correlationId = id; // no container row to derive StoryId from
            Response.Headers["X-Correlation-Id"] = correlationId.ToString();
            return NotFound(new
            {
                code = ContainerNotFoundException.ErrorCode,
                message = $"Container {id} not found.",
                correlationId,
            });
        }
        catch (ContainerRuntimeUnavailableException ex)
        {
            // Transient provider/proxy failure — 503 with Retry-After.
            Response.Headers["X-Correlation-Id"] = ex.CorrelationId.ToString();
            Response.Headers["Retry-After"] = ex.RetryAfterSeconds.ToString();
            return StatusCode(503, new
            {
                code = ContainerRuntimeUnavailableException.ErrorCode,
                message = ex.Message,
                correlationId = ex.CorrelationId,
                retryAfterSeconds = ex.RetryAfterSeconds,
            });
        }
    }

    [HttpPost]
    [RequirePermission("container:write")]
    public async Task<IActionResult> Create([FromBody] CreateContainerRequest request, CancellationToken ct)
    {
        // On-behalf-of ownership: a trusted SERVICE caller (e.g. andy-tasks
        // creating a goal-execution container via M2M) may set OwnerId to the
        // originating human so that human can see + manage the container in the
        // UI (CanAccess checks OwnerId == caller). Without this the container
        // was stamped with the SERVICE principal's id and the human's session
        // got 403 reading its own goal's container. A human caller can never
        // spoof ownership — their OwnerId is always forced to their own id.
        var requestedOwnerId = request.OwnerId;
        if (_currentUser.IsServiceAccount() && !string.IsNullOrWhiteSpace(requestedOwnerId))
        {
            request.OwnerId = requestedOwnerId;
            // The service does not carry the human's profile; leave email /
            // username unset rather than stamping the service's own.
            request.OwnerEmail = null;
            request.OwnerPreferredUsername = null;
        }
        else
        {
            request.OwnerId = _currentUser.GetUserId();
            request.OwnerEmail = _currentUser.GetEmail();
            request.OwnerPreferredUsername = _currentUser.GetDisplayName();
        }
        if (request.Source == CreationSource.Unknown)
            request.Source = CreationSource.RestApi;
        if (string.IsNullOrEmpty(request.ClientInfo) && HttpContext?.Request?.Headers.UserAgent.Count > 0)
            request.ClientInfo = Request.Headers.UserAgent.ToString();

        if (request.OrganizationId.HasValue && !_currentUser.IsAdmin())
        {
            var isMember = await _orgMembership.IsMemberAsync(_currentUser.GetUserId(), request.OrganizationId.Value, ct);
            if (!isMember) return Forbid();
        }

        try
        {
            var container = await _containerService.CreateContainerAsync(request, ct);
            return CreatedAtAction(nameof(Get), new { id = container.Id }, container);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (QuotaExceededException ex)
        {
            // Conductor #878. 422 carries a stable machine-readable
            // code so the Conductor side can switch on it rather
            // than parsing the human message. The structured payload
            // (limit + current + ownerId) lets the UI render an
            // accurate alert without a second round-trip.
            return UnprocessableEntity(new
            {
                code = QuotaExceededException.Code,
                message = $"You already have {ex.Current} containers running. Destroy one before creating another.",
                limit = ex.Limit,
                current = ex.Current,
                ownerId = ex.OwnerId
            });
        }
    }

    [HttpPost("{id:guid}/start")]
    [RequirePermission("container:execute")]
    public async Task<IActionResult> Start(Guid id, CancellationToken ct)
    {
        var container = await FindContainerAsync(id, ct);
        if (container is null) return ContainerNotFound(id);
        if (!CanAccess(container)) return Forbid();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _containerService.StartContainerAsync(id, cts.Token);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(504, new { message = "Container start timed out" });
        }
        container = await _containerService.GetContainerAsync(id, CancellationToken.None);
        return Ok(container);
    }

    [HttpPost("{id:guid}/stop")]
    [RequirePermission("container:execute")]
    public async Task<IActionResult> Stop(Guid id, CancellationToken ct)
    {
        var container = await FindContainerAsync(id, ct);
        if (container is null) return ContainerNotFound(id);
        if (!CanAccess(container)) return Forbid();

        try
        {
            // Use a separate timeout instead of the request's CancellationToken,
            // because the browser may cancel the request before the container
            // finishes stopping, leaving it in an inconsistent state.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _containerService.StopContainerAsync(id, cts.Token);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(504, new { message = "Container stop timed out" });
        }
        container = await _containerService.GetContainerAsync(id, CancellationToken.None);
        return Ok(container);
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission("container:delete")]
    public async Task<IActionResult> Destroy(Guid id, CancellationToken ct)
    {
        var container = await FindContainerAsync(id, ct);
        if (container is null) return ContainerNotFound(id);
        if (!CanAccess(container)) return Forbid();

        await _containerService.DestroyContainerAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/exec")]
    [RequirePermission("container:execute")]
    public async Task<IActionResult> Exec(Guid id, [FromBody] ExecRequest request, CancellationToken ct)
    {
        var container = await FindContainerAsync(id, ct);
        if (container is null) return ContainerNotFound(id);
        if (!CanAccess(container)) return Forbid();

        var result = await _containerService.ExecAsync(id, request.Command, ct);
        return Ok(result);
    }

    /// <summary>
    /// Streams a command's stdout and stderr as line-framed Server-Sent
    /// Events, followed by one terminal <c>done</c> event containing the
    /// process exit code. Disconnecting the HTTP client cancels the
    /// underlying provider exec.
    /// </summary>
    [HttpPost("{id:guid}/exec/stream")]
    [RequirePermission("container:execute")]
    [Produces("text/event-stream")]
    public async Task<IActionResult> ExecStream(
        Guid id,
        [FromBody] ExecRequest request,
        CancellationToken ct)
    {
        var container = await FindContainerAsync(id, ct);
        if (container is null) return ContainerNotFound(id);
        if (!CanAccess(container)) return Forbid();

        if (string.IsNullOrWhiteSpace(request.Command))
        {
            return BadRequest(new { message = "Command is required." });
        }

        if (request.TimeoutSeconds is < 1 or > 86_400)
        {
            return BadRequest(new
            {
                message = "TimeoutSeconds must be between 1 and 86400.",
            });
        }

        if (container.Status is not (ContainerStatus.Running or ContainerStatus.Creating))
        {
            return Conflict(new
            {
                message = $"Container is {container.Status}, cannot exec.",
            });
        }

        if (string.IsNullOrEmpty(container.ExternalId))
        {
            return Conflict(new { message = "Container has no external ID yet." });
        }

        await ContainerExecSse.StreamAsync(
            Response,
            _containerService,
            id,
            request.Command,
            TimeSpan.FromSeconds(request.TimeoutSeconds),
            ct);

        return new EmptyResult();
    }

    /// <summary>
    /// rivoli-ai/conductor#945 (M1.5.3). Re-run the code-assistant
    /// install for a container that surfaced a Failed or Skipped
    /// status. The container must be Running (the install script
    /// execs inside it) and have a code assistant configured (we read
    /// the config back from <c>Container.CodeAssistant</c>'s JSON
    /// blob — same source the worker uses).
    /// </summary>
    [HttpPost("{id:guid}/retry-code-assistant-install")]
    [RequirePermission("container:execute")]
    public async Task<IActionResult> RetryCodeAssistantInstall(
        Guid id,
        [FromServices] ICodeAssistantInstallExecutor executor,
        CancellationToken ct)
    {
        var container = await FindContainerAsync(id, ct);
        if (container is null) return ContainerNotFound(id);
        if (!CanAccess(container)) return Forbid();

        if (container.Status != ContainerStatus.Running)
        {
            return UnprocessableEntity(new
            {
                error = new
                {
                    type = "container_not_running",
                    message = $"Container is {container.Status}; retry requires Running."
                }
            });
        }

        if (string.IsNullOrWhiteSpace(container.CodeAssistant))
        {
            return UnprocessableEntity(new
            {
                error = new
                {
                    type = "no_code_assistant_configured",
                    message = "Container has no code assistant configured; nothing to retry."
                }
            });
        }

        CodeAssistantConfig? codeAssistant;
        try
        {
            codeAssistant = System.Text.Json.JsonSerializer.Deserialize<CodeAssistantConfig>(container.CodeAssistant);
        }
        catch (Exception ex)
        {
            return UnprocessableEntity(new
            {
                error = new
                {
                    type = "code_assistant_config_unreadable",
                    message = $"Could not parse Container.CodeAssistant: {ex.GetType().Name}: {ex.Message}"
                }
            });
        }
        if (codeAssistant is null)
        {
            return UnprocessableEntity(new
            {
                error = new
                {
                    type = "code_assistant_config_unreadable",
                    message = "Container.CodeAssistant deserialised to null."
                }
            });
        }

        await executor.RunAsync(container, codeAssistant, ct);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            container.Id,
            container.CodeAssistantStatus,
            container.CodeAssistantStatusReason,
            container.CodeAssistantStatusAt,
        });
    }

    [HttpGet("{id:guid}/connection")]
    [RequirePermission("container:read")]
    public async Task<IActionResult> GetConnectionInfo(Guid id, CancellationToken ct)
    {
        var container = await FindContainerAsync(id, ct);
        if (container is null) return ContainerNotFound(id);
        if (!CanAccess(container)) return Forbid();

        var info = await _containerService.GetConnectionInfoAsync(id, ct);
        return Ok(info);
    }

    /// <summary>
    /// Conductor #886. Sets or clears the container's preferred
    /// theme. The body is <c>{ "themeId": "..." }</c> for set,
    /// <c>{ "themeId": null }</c> for clear (resolves back to
    /// template default → user pref → hardcoded).
    ///
    /// Validates against the catalog — unknown id returns 422
    /// with a structured envelope so the client can show the
    /// rejection without a generic error.
    /// </summary>
    [HttpPatch("{id:guid}/theme")]
    [RequirePermission("container:write")]
    public async Task<IActionResult> SetTheme(Guid id, [FromBody] SetThemeRequest request, CancellationToken ct)
    {
        var container = await _db.Containers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (container is null) return NotFound();
        if (!CanAccess(container)) return Forbid();

        if (!string.IsNullOrEmpty(request.ThemeId))
        {
            var themeExists = await _db.Themes
                .AsNoTracking()
                .AnyAsync(t => t.Id == request.ThemeId, ct);
            if (!themeExists)
            {
                return UnprocessableEntity(new
                {
                    code = "unknown_theme",
                    message = $"Theme '{request.ThemeId}' is not in the catalog.",
                });
            }
        }

        container.ThemeId = string.IsNullOrEmpty(request.ThemeId) ? null : request.ThemeId;
        await _db.SaveChangesAsync(ct);
        return Ok(container);
    }

    [HttpPut("{id:guid}/resources")]
    [RequirePermission("container:execute")]
    public async Task<IActionResult> Resize(Guid id, [FromBody] ResizeRequest request, CancellationToken ct)
    {
        try
        {
            var container = await _containerService.GetContainerAsync(id, ct);
            if (!CanAccess(container)) return Forbid();

            var resources = new Andy.Containers.Abstractions.ResourceSpec
            {
                CpuCores = request.CpuCores,
                MemoryMb = request.MemoryMb,
                DiskGb = request.DiskGb
            };
            await _containerService.ResizeContainerAsync(id, resources, ct);
            container = await _containerService.GetContainerAsync(id, ct);
            return Ok(container);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [RequirePermission("container:read")]
    [HttpGet("{id:guid}/screenshot")]
    public async Task<IActionResult> GetScreenshot(Guid id, CancellationToken ct)
    {
        try
        {
            var container = await _containerService.GetContainerAsync(id, ct);
            if (!CanAccess(container)) return Forbid();

            if (string.IsNullOrEmpty(container.Metadata))
                return Ok(new { available = false });

            var metadata = System.Text.Json.JsonSerializer.Deserialize<ContainerMetadata>(
                container.Metadata,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (metadata?.Screenshot?.AnsiText == null)
                return Ok(new { available = false });

            return Ok(new
            {
                available = true,
                ansiText = metadata.Screenshot.AnsiText,
                capturedAt = metadata.Screenshot.CapturedAt,
                cols = metadata.Screenshot.Cols,
                rows = metadata.Screenshot.Rows
            });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [RequirePermission("container:read")]
    [HttpPost("screenshots")]
    public async Task<IActionResult> GetScreenshots([FromBody] Guid[] containerIds, CancellationToken ct)
    {
        if (containerIds.Length > 20)
            return BadRequest(new { error = "Maximum 20 container IDs per request" });

        var results = new Dictionary<string, object>();
        foreach (var id in containerIds)
        {
            try
            {
                var container = await _containerService.GetContainerAsync(id, ct);
                if (!CanAccess(container) || string.IsNullOrEmpty(container.Metadata))
                {
                    results[id.ToString()] = new { available = false };
                    continue;
                }
                var metadata = System.Text.Json.JsonSerializer.Deserialize<ContainerMetadata>(
                    container.Metadata,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (metadata?.Screenshot?.AnsiText == null)
                {
                    results[id.ToString()] = new { available = false };
                    continue;
                }
                results[id.ToString()] = new
                {
                    available = true,
                    ansiText = metadata.Screenshot.AnsiText,
                    capturedAt = metadata.Screenshot.CapturedAt,
                    cols = metadata.Screenshot.Cols,
                    rows = metadata.Screenshot.Rows
                };
            }
            catch { results[id.ToString()] = new { available = false }; }
        }
        return Ok(results);
    }

    [HttpGet("{id:guid}/stats")]
    [RequirePermission("container:execute")]
    public async Task<IActionResult> GetStats(Guid id, CancellationToken ct)
    {
        try
        {
            var container = await _containerService.GetContainerAsync(id, ct);
            if (!CanAccess(container)) return Forbid();

            var stats = await _containerService.GetContainerStatsAsync(id, ct);
            return Ok(stats);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("{id:guid}/events")]
    [RequirePermission("container:read")]
    public async Task<IActionResult> GetEvents(Guid id, CancellationToken ct)
    {
        var container = await FindContainerAsync(id, ct);
        if (container is null) return ContainerNotFound(id);
        if (!CanAccess(container)) return Forbid();

        var events = await _db.Events
            .Where(e => e.ContainerId == id)
            .OrderByDescending(e => e.Timestamp)
            .Take(50)
            .ToListAsync(ct);
        return Ok(events);
    }

    [HttpGet("{id:guid}/repositories")]
    [RequirePermission("container:read")]
    public async Task<IActionResult> ListRepositories(Guid id, CancellationToken ct)
    {
        var container = await FindContainerAsync(id, ct);
        if (container is null) return ContainerNotFound(id);
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
    [RequirePermission("container:write")]
    public async Task<IActionResult> AddRepository(Guid id, [FromBody] AddRepositoryDto dto, CancellationToken ct)
    {
        var container = await FindContainerAsync(id, ct);
        if (container is null) return ContainerNotFound(id);
        if (!CanAccess(container)) return Forbid();

        if (container.Status != ContainerStatus.Running)
            return BadRequest(new { error = $"Container is {container.Status}, must be Running to add repositories" });

        var config = new GitRepositoryConfig
        {
            Url = dto.Url,
            Branch = dto.Branch,
            TargetPath = dto.TargetPath,
            CredentialRef = dto.CredentialRef,
            CloneDepth = dto.CloneDepth,
            Submodules = dto.Submodules
        };

        var errors = GitRepositoryValidator.Validate(config);
        if (errors.Count > 0)
            return BadRequest(new { errors });

        // Validate credential and probe URL
        var probeErrors = await _probeService.ProbeRepositoriesAsync(
            [config], container.OwnerId, requireCredentials: true, ct);
        if (probeErrors.Count > 0)
            return UnprocessableEntity(new { error = string.Join("; ", probeErrors) });

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
    [RequirePermission("container:execute")]
    public async Task<IActionResult> PullRepository(Guid id, Guid repoId, CancellationToken ct)
    {
        var container = await FindContainerAsync(id, ct);
        if (container is null) return ContainerNotFound(id);
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

    /// <summary>
    /// F6.1 (rivoli-ai/conductor#1940). Read-only unified git diff of the
    /// container's run branch vs its base. Implemented via
    /// <c>git diff</c> through the provider exec surface (ARCHITECTURE §16.3) —
    /// NOT a Docker-Engine verb (decision #17). Conductor reaches this through
    /// the UnifiedProxy localhost surface like every other /api/containers/*
    /// call. A clean tree / no-git-repo / detached HEAD returns 200 with an
    /// empty file list, not an error. Optional <paramref name="repoId"/>
    /// scopes the diff to one repo in a multi-repo container.
    /// </summary>
    [HttpGet("{id:guid}/git/diff")]
    [RequirePermission("container:read")]
    public async Task<IActionResult> GetGitDiff(Guid id, [FromQuery] Guid? repoId, CancellationToken ct)
    {
        var container = await FindContainerAsync(id, ct);
        if (container is null) return ContainerNotFound(id);
        if (!CanAccess(container)) return Forbid();

        var diff = await _gitDiffService.GetDiffAsync(id, repoId, ct);

        return Ok(new GitDiffDto(
            diff.BaseBranch,
            diff.RunBranch,
            diff.Files.Select(f => new GitDiffFileDto(
                f.Path, f.ChangeType, f.Additions, f.Deletions, f.Patch, f.Truncated)).ToList(),
            diff.RawPatch));
    }

    /// <summary>
    /// rivoli-ai/conductor#2236. Server-Sent Events stream of the
    /// container's MID-RUN agent output — the container-scoped counterpart
    /// of <c>GET /api/runs/{id}/output</c> (<see cref="RunsController.Output"/>).
    /// Conductor's live agent feed (TX F4.2, #1935) is keyed by the goal's
    /// workspace container id (one container per workspace, decision #21),
    /// not by run id, so it connects HERE. We resolve the container's
    /// most-recent run and delegate to the shared <see cref="RunOutputSse"/>
    /// serialiser so the wire format, <c>Last-Event-ID</c> resumption, and
    /// terminal-stop semantics are byte-identical to the run-scoped endpoint.
    /// </summary>
    /// <remarks>
    /// The <c>IRunOutputBus</c> doc already promised both endpoints subscribe
    /// to it; only the run-scoped route had been wired, so the container feed
    /// connected to a non-existent route (404) and rendered nothing. This is
    /// the missing producer half.
    ///
    /// When no run has been dispatched for the container yet, we emit a
    /// well-formed but empty <c>text/event-stream</c> that closes cleanly so
    /// the consumer renders its empty-state instead of hanging — never a 404
    /// (a healthy never-run container is not an error). <c>follow</c>/<c>tail</c>
    /// query params are accepted for contract parity; the in-process bus
    /// always follows live and replays its ring buffer.
    /// </remarks>
    [HttpGet("{id:guid}/logs")]
    [RequirePermission("container:read")]
    public async Task Logs(Guid id, CancellationToken ct)
    {
        var container = await FindContainerAsync(id, ct);
        if (container is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            Response.Headers["X-Correlation-Id"] = id.ToString();
            return;
        }
        if (!CanAccess(container))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Resolve the run whose output to stream. Prefer a live run
        // (Running > Provisioning > Pending); otherwise the most-recent run
        // so a just-finished task still replays its buffered tail. A
        // container with no run yet yields an empty stream (handled below).
        //
        // SQLite cannot translate an ORDER BY over a DateTimeOffset column
        // (System.NotSupportedException), so the recency tiebreak MUST be
        // ordered client-side. Pull the container's runs (bounded — a
        // workspace container has few runs) into memory, then rank. Ordering
        // the whole thing client-side keeps the priority CASE and the
        // CreatedAt tiebreak in one place and provider-agnostic.
        var candidates = await _db.Runs.AsNoTracking()
            .Where(r => r.ContainerId == id)
            .Select(r => new { r.Id, r.Status, r.CreatedAt })
            .ToListAsync(ct);

        var run = candidates
            .OrderByDescending(r =>
                r.Status == RunStatus.Running ? 3 :
                r.Status == RunStatus.Provisioning ? 2 :
                r.Status == RunStatus.Pending ? 1 : 0)
            .ThenByDescending(r => r.CreatedAt)
            .FirstOrDefault();

        if (run is null)
        {
            // No run dispatched yet — open an empty SSE stream and close it
            // cleanly so the live feed shows its empty-state, not a hang.
            Response.Headers.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-store";
            Response.Headers["X-Accel-Buffering"] = "no";
            if (container.Status is ContainerStatus.Stopped
                or ContainerStatus.Stopping
                or ContainerStatus.Destroying
                or ContainerStatus.Destroyed
                or ContainerStatus.Failed)
            {
                await RunOutputSse.WriteTerminalErrorAsync(
                    Response,
                    "container-stopped",
                    $"Container {id} is {container.Status}.",
                    ct);
                return;
            }
            await Response.Body.FlushAsync(ct);
            return;
        }

        await RunOutputSse.StreamAsync(
            Response,
            Request,
            _outputBus,
            run.Id,
            ct,
            honorLogQuery: true);

        if (run.Status is RunStatus.Failed or RunStatus.Timeout)
        {
            await RunOutputSse.WriteTerminalErrorAsync(
                Response,
                "internal-error",
                $"Run {run.Id} ended with {run.Status}.",
                ct);
        }
        else if (run.Status == RunStatus.Cancelled)
        {
            await RunOutputSse.WriteTerminalErrorAsync(
                Response,
                "container-stopped",
                $"Run {run.Id} was cancelled.",
                ct);
        }
    }

    /// <summary>
    /// F6.4 (rivoli-ai/conductor#1943). Lists the run container's TCP ports:
    /// those published to a host (loopback) port — so Conductor can preview a
    /// web app the agent started over the UnifiedProxy — merged with ports
    /// discovered listening inside the container via <c>ss</c>/<c>netstat</c>
    /// (the exec surface; no new Docker-Engine verb, decision #17). A stopped
    /// container / no-listening-port yields an empty-but-OK result.
    /// </summary>
    [HttpGet("{id:guid}/ports")]
    [RequirePermission("container:read")]
    public async Task<IActionResult> GetPorts(Guid id, CancellationToken ct)
    {
        var container = await FindContainerAsync(id, ct);
        if (container is null) return ContainerNotFound(id);
        if (!CanAccess(container)) return Forbid();

        var ports = await _portDiscoveryService.GetPortsAsync(id, ct);

        return Ok(new ContainerPortsDto(
            ports.Mapped.Select(m => new MappedPortDto(
                m.ContainerPort, m.HostPort, m.Listening, m.WebEndpoint)).ToList(),
            ports.DiscoveredUnmapped,
            ports.SuggestedAppPort));
    }

    /// <summary>
    /// F6.4 (rivoli-ai/conductor#1943). Publishes a container port to a host
    /// (loopback) port for the run's web preview. Docker can only publish at
    /// create-time, so a running-container expose surfaces as a 400 with an
    /// explanatory message (same pattern as live resource resize); providers
    /// that can't add a live mapping also return 400. Requires
    /// <c>container:execute</c> (it mutates the runtime mapping).
    /// </summary>
    [HttpPost("{id:guid}/ports/expose")]
    [RequirePermission("container:execute")]
    public async Task<IActionResult> ExposePort(Guid id, [FromBody] ExposePortRequest request, CancellationToken ct)
    {
        if (request.ContainerPort is <= 0 or > 65535)
            return BadRequest(new { error = "containerPort must be between 1 and 65535." });

        try
        {
            var container = await _containerService.GetContainerAsync(id, ct);
            if (!CanAccess(container)) return Forbid();

            var mapped = await _portDiscoveryService.ExposePortAsync(id, request.ContainerPort, ct);
            return Ok(new MappedPortDto(mapped.ContainerPort, mapped.HostPort, mapped.Listening, mapped.WebEndpoint));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private bool CanAccess(Container container)
    {
        if (_currentUser.IsAdmin()) return true;
        // A trusted M2M service (e.g. andy-tasks executing a goal plan) has
        // already passed the action's [RequirePermission] gate. Owner-equality
        // models HUMAN ownership ("containers the principal doesn't own are
        // invisible", decision #17); a service principal acting on-behalf-of a
        // human (OBO) must not be re-denied here, or andy-tasks 403s on /exec
        // against the very goal-container it just created — the spawn worked,
        // the verifier/next-task exec was forbidden a second later. Permission
        // scoping remains the gate for what a service may do.
        if (_currentUser.IsServiceAccount()) return true;

        // Human identities created before the canonical-subject migration (and
        // OBO goal containers whose owner came from the goal's user context)
        // can be stamped with the verified email claim instead of the token's
        // NameIdentifier/sub. A refreshed session then presents the stable
        // subject id and strict string equality incorrectly 403s the same
        // person. Accept either authenticated identity claim as the owner;
        // this does not broaden access beyond the current principal.
        if (string.Equals(container.OwnerId, _currentUser.GetUserId(), StringComparison.Ordinal))
            return true;

        var email = _currentUser.GetEmail();
        return !string.IsNullOrWhiteSpace(email)
            && string.Equals(container.OwnerId, email, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// READ-only access check (rivoli-ai/andy-containers#366). Strict
    /// owner-equality (<see cref="CanAccess"/>) is correct for write /
    /// lifecycle verbs, but it 403s the human session that polls
    /// <c>GET /api/containers/{id}</c> for a goal-execution container: those
    /// containers are stamped (via the M2M on-behalf-of path, commit 5305232)
    /// with the GOAL owner's id, which need not equal the id of the human
    /// principal now signed in (a <c>dev-user</c> / fallback-claim mismatch,
    /// or a teammate viewing the same goal). The UI must reflect system state,
    /// so a non-owner who BELONGS TO THE SAME ORGANISATION as the container is
    /// allowed to READ it.
    ///
    /// This deliberately mirrors the existing org-membership scoping already
    /// used by the list endpoint (<c>List</c> / <c>Create</c> -&gt;
    /// <c>IOrganizationMembershipService.IsMemberAsync</c>) and by
    /// <c>ContainerAuthorizationService.CanAccessContainerAsync</c>, so a
    /// container that is already visible in your fleet list by org-membership
    /// can also be read individually. It does NOT over-expose: membership is
    /// proven by the <c>org_id</c>/<c>org_ids</c> JWT claim or an andy-rbac
    /// lookup, so a user with no shared organisation still 403s. Containers
    /// with no <c>OrganizationId</c> remain owner/admin-only — there is no
    /// broadening for them. Write / lifecycle verbs keep calling the strict
    /// synchronous <see cref="CanAccess"/>.
    /// </summary>
    private async Task<bool> CanReadAsync(Container container, CancellationToken ct)
    {
        if (CanAccess(container)) return true;
        if (container.OrganizationId.HasValue)
            return await _orgMembership.IsMemberAsync(_currentUser.GetUserId(), container.OrganizationId.Value, ct);
        return false;
    }

    /// <summary>
    /// Resolves a container for sub-resource endpoints, translating the
    /// store's <see cref="KeyNotFoundException"/> into <c>null</c> so the
    /// caller can return the structured 404 envelope instead of leaking a
    /// 500 (rivoli-ai/conductor#1972 — observed live on
    /// <c>GET /api/containers/{id}/git/diff</c> with an unknown id).
    /// </summary>
    private async Task<Container?> FindContainerAsync(Guid id, CancellationToken ct)
    {
        try
        {
            return await _containerService.GetContainerAsync(id, ct);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Structured 404 envelope for an unknown container id — same shape as
    /// the classified <c>GET /api/containers/{id}</c> response (SM.2.6):
    /// <c>{ code, message, correlationId }</c> plus the
    /// <c>X-Correlation-Id</c> header.
    /// </summary>
    private NotFoundObjectResult ContainerNotFound(Guid id)
    {
        Response.Headers["X-Correlation-Id"] = id.ToString();
        return NotFound(new
        {
            code = ContainerNotFoundException.ErrorCode,
            message = $"Container {id} not found.",
            correlationId = id,
        });
    }
}

public record ContainerGitRepositoryDto(
    Guid Id, string Url, string? Branch, string TargetPath, int? CloneDepth, bool Submodules,
    bool IsFromTemplate, string CloneStatus, string? CloneError,
    DateTime? CloneStartedAt, DateTime? CloneCompletedAt);

/// <summary>
/// Wire shape for <c>GET /api/containers/{id}/git/diff</c> (F6.1,
/// rivoli-ai/conductor#1940). Conductor renders structured per-file diffs or
/// falls back to <see cref="RawPatch"/>.
/// </summary>
public record GitDiffDto(
    string? BaseBranch,
    string? RunBranch,
    IReadOnlyList<GitDiffFileDto> Files,
    string RawPatch);

public record GitDiffFileDto(
    string Path,
    string ChangeType,
    int? Additions,
    int? Deletions,
    string Patch,
    bool Truncated);

/// <summary>
/// Wire shape for <c>GET /api/containers/{id}/ports</c> (F6.4,
/// rivoli-ai/conductor#1943). Conductor's web-preview port picker defaults to
/// <see cref="SuggestedAppPort"/> and previews <see cref="MappedPortDto.WebEndpoint"/>
/// through the UnifiedProxy.
/// </summary>
public record ContainerPortsDto(
    IReadOnlyList<MappedPortDto> Mapped,
    IReadOnlyList<int> DiscoveredUnmapped,
    int? SuggestedAppPort);

public record MappedPortDto(
    int ContainerPort,
    int HostPort,
    bool Listening,
    string WebEndpoint);

/// <summary>Body for <c>POST /api/containers/{id}/ports/expose</c> (F6.4).</summary>
public class ExposePortRequest
{
    public int ContainerPort { get; set; }
}

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

public class ResizeRequest
{
    public double CpuCores { get; set; } = 2;
    public int MemoryMb { get; set; } = 4096;
    public int DiskGb { get; set; } = 20;
}

/// <summary>
/// Body for PATCH /api/containers/{id}/theme. Conductor #886.
/// </summary>
public class SetThemeRequest
{
    /// <summary>
    /// Theme catalog id ("dracula", "github-dark", …). Pass null
    /// or empty to clear the override and fall back through the
    /// resolution chain (template → user pref → hardcoded).
    /// </summary>
    public string? ThemeId { get; set; }
}
