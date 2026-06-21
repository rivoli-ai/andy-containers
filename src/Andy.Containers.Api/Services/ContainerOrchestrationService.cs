using System.Diagnostics;
using System.Text.Json;
using Andy.Containers.Abstractions;
using Andy.Containers.Abstractions.Images;
using Andy.Containers.Api.Telemetry;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Messaging;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;
using Andy.Containers.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ConnectionInfo = Andy.Containers.Abstractions.ConnectionInfo;

namespace Andy.Containers.Api.Services;

public class ContainerOrchestrationService : IContainerService
{
    private readonly ContainersDbContext _db;
    private readonly IInfrastructureRoutingService _routing;
    private readonly IInfrastructureProviderFactory _providerFactory;
    private readonly ContainerProvisioningQueue _queue;
    private readonly IGitRepositoryProbeService _probeService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ContainerOrchestrationService> _logger;
    private readonly IServiceTokenService? _serviceTokenService;
    private readonly IProxyTokenService? _proxyTokenService;
    private readonly IDataProtector? _proxyTokenProtector;
    private readonly IBuildArtifactStore? _buildArtifactStore;
    private readonly IRegistryConfiguration? _registryConfiguration;
    // rivoli-ai/andy-containers#316. Optional so the existing test
    // surface (which doesn't construct one) keeps working; live DI
    // always supplies FilesystemOutputArtifactCollector. When null,
    // terminal events publish without an OutputArtifacts manifest —
    // matches the v1 schema-version-1 wire shape exactly.
    private readonly IOutputArtifactCollector? _artifactCollector;

    /// <summary>
    /// rivoli-ai/conductor#943. Data-protection purpose for encrypting
    /// the JWT persisted in <c>Container.ProxyServiceToken</c>. Scoped
    /// to this column so a future purpose-string change (e.g. for a
    /// key rotation) only re-encrypts proxy tokens, not every secret.
    /// </summary>
    private const string ProxyTokenProtectorPurpose = "Container.ProxyServiceToken";

    /// <summary>
    /// Conductor #878. Default per-user simultaneous-container
    /// cap when the config key is missing or unparseable. Sized
    /// at 32 to keep friendly-name collision probability under
    /// 0.5% (the wordlist has ~4K combinations) and to flag at
    /// roughly the host RAM ceiling for a typical dev workstation.
    /// </summary>
    public const int DefaultPerUserSimultaneousLimit = 32;

    public ContainerOrchestrationService(
        ContainersDbContext db,
        IInfrastructureRoutingService routing,
        IInfrastructureProviderFactory providerFactory,
        ContainerProvisioningQueue queue,
        IGitRepositoryProbeService probeService,
        IConfiguration configuration,
        ILogger<ContainerOrchestrationService> logger,
        IServiceTokenService? serviceTokenService = null,
        IProxyTokenService? proxyTokenService = null,
        IDataProtectionProvider? dataProtection = null,
        IBuildArtifactStore? buildArtifactStore = null,
        IRegistryConfiguration? registryConfiguration = null,
        IOutputArtifactCollector? artifactCollector = null)
    {
        _db = db;
        _routing = routing;
        _providerFactory = providerFactory;
        _queue = queue;
        _probeService = probeService;
        _configuration = configuration;
        _logger = logger;
        // #944. Optional so existing tests (which don't construct one)
        // keep working; live DI always supplies the registered impl.
        // When null, the per-container token-injection step is a no-op.
        _serviceTokenService = serviceTokenService;
        // rivoli-ai/conductor#943. Optional for the same reason. When
        // both are null we fall back to the pre-#943 behaviour (no
        // per-container token, env-var injection runs unmodified).
        _proxyTokenService = proxyTokenService;
        _proxyTokenProtector = dataProtection?.CreateProtector(ProxyTokenProtectorPurpose);
        // #274 (P1F1). Optional so unit tests that don't exercise the
        // image-management pipeline don't need to construct stubs.
        // When either is null we fall back to template.BaseImage —
        // pre-IM behaviour. Live DI always supplies both.
        _buildArtifactStore = buildArtifactStore;
        _registryConfiguration = registryConfiguration;
        // #316. Optional; null = pre-#316 wire shape, no artifact
        // manifest on the terminal event.
        _artifactCollector = artifactCollector;
    }

    // rivoli-ai/andy-containers#316. Wraps the collector call so a
    // misbehaving probe (exec failure, hash crash, timeout) can never
    // block the terminal-event write. Returns null on any failure —
    // the outbox helper omits the OutputArtifacts field when null,
    // preserving the v1-compatible wire shape.
    private async Task<IReadOnlyList<RunOutputArtifact>?> TryCollectArtifactsAsync(
        Container container, CancellationToken ct)
    {
        if (_artifactCollector is null) return null;
        try
        {
            return await _artifactCollector.CollectAsync(container, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Artifact collection failed for container {ContainerId}; emitting terminal event without manifest. {Message}",
                container.Id, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Reads the current per-user simultaneous-container cap.
    /// Re-read on every call (no caching) so an admin bumping
    /// the setting takes effect on the next CreateContainer
    /// request — matches the spec for #878.
    /// </summary>
    private int GetPerUserSimultaneousLimit()
    {
        var configured = _configuration.GetValue<int?>("Containers:PerUserSimultaneousLimit");
        // Reject zero / negative — those would mean "no creates
        // allowed at all" which is not a useful state and is
        // almost certainly a config typo. Treat as default.
        if (configured is null || configured.Value <= 0)
        {
            return DefaultPerUserSimultaneousLimit;
        }
        return configured.Value;
    }

    public async Task<Container> CreateContainerAsync(CreateContainerRequest request, CancellationToken ct)
    {
        using var activity = ActivitySources.Provisioning.StartActivity("CreateContainer");
        // OT7 (rivoli-ai/conductor#1265). Attributes renamed under the
        // `andy.containers.*` namespace per docs/semconv-compliance.md.
        // Legacy names dual-emit during the 0.2.4 transition window.
        var templateTag = request.TemplateId?.ToString() ?? request.TemplateCode;
        var providerTag = request.ProviderCode ?? request.ProviderId?.ToString();
        activity?.SetTag("andy.containers.template_id", templateTag);
        activity?.SetTag("andy.containers.provider", providerTag);
        activity?.SetTag("templateId", templateTag); // deprecated; removed in 0.3.0
        activity?.SetTag("provider", providerTag);   // deprecated; removed in 0.3.0

        // Conductor #878. Per-user quota check. Done BEFORE
        // resolving template/provider so a user at the cap
        // gets an immediate 422 instead of paying for two
        // database round-trips just to be told no. We count
        // every non-Destroyed row — Pending / Creating / Failed
        // all consume the slot because they tie up a name and
        // (for non-Failed) potentially provider resources.
        var ownerId = request.OwnerId ?? "system";
        var limit = GetPerUserSimultaneousLimit();
        var current = await _db.Containers
            .Where(c => c.OwnerId == ownerId && c.Status != ContainerStatus.Destroyed)
            .CountAsync(ct);
        if (current >= limit)
        {
            throw new QuotaExceededException(limit, current, ownerId);
        }

        // Resolve template
        var template = request.TemplateId.HasValue
            ? await _db.Templates.FindAsync([request.TemplateId.Value], ct)
            : await _db.Templates.FirstOrDefaultAsync(t => t.Code == request.TemplateCode, ct);

        if (template is null)
            throw new ArgumentException("Template not found");

        // #274 (P1F1). If the IM pipeline has produced a BuildArtifact
        // for this template in the primary registry, prefer its
        // digest-pinned ref over the legacy template.BaseImage string.
        // Falls back to BaseImage when no artifact exists (templates
        // that haven't been built via IM yet, e.g. andy-desktop-* which
        // are local-built fixtures, not registry images).
        var resolvedTemplateImage =
            await ResolveTemplateImageRefAsync(template.Id, ct)
            ?? template.BaseImage;

        // X4 (rivoli-ai/andy-containers#93). Resolve the bound profile
        // (if any). When set, the profile's BaseImageRef and Kind
        // override the template's image and GUI behaviour: Headless /
        // Terminal kinds skip the VNC sidecar entirely; Desktop keeps
        // it. The template still drives resources, scripts, and
        // dependencies — only image + sidecar surface flip.
        //
        // X5 (rivoli-ai/andy-containers#94). When the request omits a
        // profile but binds a workspace, inherit the workspace's bound
        // profile — that's the workspace's governance anchor and every
        // container the workspace provisions should match its envelope.
        // Explicit request.EnvironmentProfileId still wins (one-off
        // shells into a different env are intentional).
        var effectiveProfileId = request.EnvironmentProfileId;
        if (effectiveProfileId is null && request.WorkspaceId.HasValue)
        {
            effectiveProfileId = await _db.Workspaces
                .Where(w => w.Id == request.WorkspaceId.Value)
                .Select(w => w.EnvironmentProfileId)
                .FirstOrDefaultAsync(ct);
        }

        EnvironmentProfile? profile = null;
        if (effectiveProfileId.HasValue)
        {
            profile = await _db.EnvironmentProfiles
                .FirstOrDefaultAsync(p => p.Id == effectiveProfileId.Value, ct);
            if (profile is null)
            {
                throw new ArgumentException(
                    $"EnvironmentProfile '{effectiveProfileId.Value}' not found.");
            }
        }

        // Resolve or route to provider
        InfrastructureProvider provider;
        if (request.ProviderId.HasValue)
        {
            provider = await _db.Providers.FindAsync([request.ProviderId.Value], ct)
                ?? throw new ArgumentException("Provider not found");
        }
        else if (!string.IsNullOrEmpty(request.ProviderCode))
        {
            provider = await _db.Providers.FirstOrDefaultAsync(p => p.Code == request.ProviderCode, ct)
                ?? throw new ArgumentException("Provider not found");
        }
        else
        {
            var spec = new ContainerSpec
            {
                ImageReference = resolvedTemplateImage,
                Name = request.Name,
                Resources = request.Resources,
                Gpu = request.Gpu
            };
            provider = await _routing.SelectProviderAsync(spec, new RoutingPreferences
            {
                OrganizationId = request.OrganizationId
            }, ct);
        }

        var container = new Container
        {
            Name = request.Name,
            TemplateId = template.Id,
            ProviderId = provider.Id,
            OwnerId = request.OwnerId ?? "system",
            OrganizationId = request.OrganizationId,
            TeamId = request.TeamId,
            Status = ContainerStatus.Pending,
            CreationSource = request.Source,
            ClientInfo = request.ClientInfo,
            StoryId = request.StoryId,
            // Conductor #871: short human-friendly handle generated
            // at create time. Stable for the container's lifetime.
            // Avoid collisions with names already in the live fleet
            // (Destroyed rows are excluded — those names are free to
            // recycle since the user can't see them anymore).
            FriendlyName = FriendlyNameGenerator.GenerateAvoiding(
                (await _db.Containers
                    .Where(c => c.Status != ContainerStatus.Destroyed
                                && c.FriendlyName != null)
                    .Select(c => c.FriendlyName!)
                    .ToListAsync(ct)).ToHashSet()),
            ExpiresAt = request.ExpiresAfter.HasValue
                ? DateTime.UtcNow.Add(request.ExpiresAfter.Value)
                : null
        };

        // Derive container username from owner claims
        var containerUser = UserNameDerivation.DeriveUsername(
            request.OwnerPreferredUsername,
            request.OwnerEmail,
            request.OwnerId ?? "system");
        container.ContainerUser = containerUser;

        if (request.GitRepository is not null)
        {
            container.GitRepository = JsonSerializer.Serialize(request.GitRepository);
        }

        // Resolve code assistant: request override > template default
        CodeAssistantConfig? codeAssistant = null;
        if (request.CodeAssistant is not null)
        {
            codeAssistant = request.CodeAssistant;
        }
        else if (!request.ExcludeTemplateCodeAssistant && !string.IsNullOrEmpty(template.CodeAssistant))
        {
            codeAssistant = JsonSerializer.Deserialize<CodeAssistantConfig>(template.CodeAssistant);
        }

        if (codeAssistant is not null)
        {
            container.CodeAssistant = JsonSerializer.Serialize(codeAssistant);
        }

        _db.Containers.Add(container);

        // Link to workspace if specified
        Workspace? workspace = null;
        if (request.WorkspaceId.HasValue)
        {
            workspace = await _db.Workspaces.Include(w => w.Containers).FirstOrDefaultAsync(w => w.Id == request.WorkspaceId, ct)
                ?? throw new ArgumentException($"Workspace not found: {request.WorkspaceId}");
            workspace.Containers.Add(container);

            // Adopt this container as the workspace's default when it has none.
            // The run dispatcher (RunModeDispatcher) resolves a run's target via
            // Workspace.DefaultContainerId; without this, the first container a
            // workspace provisions never becomes the default and every agent-run
            // dispatch fails with "workspace has no default container". First
            // container wins; an explicit re-point is a separate concern.
            if (workspace.DefaultContainerId is null)
            {
                workspace.DefaultContainerId = container.Id;
            }
        }

        _db.Events.Add(new ContainerEvent
        {
            ContainerId = container.Id,
            EventType = ContainerEventType.Created,
            SubjectId = container.OwnerId
        });

        // Collect git repositories from request and template
        var gitRepos = new List<GitRepositoryConfig>();

        // Add repos from the list
        if (request.GitRepositories is { Count: > 0 })
        {
            var errors = GitRepositoryValidator.ValidateAll(request.GitRepositories);
            if (errors.Count > 0)
                throw new ArgumentException(string.Join("; ", errors));
            gitRepos.AddRange(request.GitRepositories);
        }

        // Backward compat: single GitRepository
        if (request.GitRepository is not null && request.GitRepositories is null)
        {
            var errors = GitRepositoryValidator.Validate(request.GitRepository);
            if (errors.Count > 0)
                throw new ArgumentException(string.Join("; ", errors));
            gitRepos.Add(request.GitRepository);
        }

        // Merge template repos unless excluded
        if (!request.ExcludeTemplateRepos && !string.IsNullOrEmpty(template.GitRepositories))
        {
            var templateRepos = JsonSerializer.Deserialize<List<GitRepositoryConfig>>(template.GitRepositories);
            if (templateRepos is not null)
            {
                foreach (var tr in templateRepos)
                {
                    gitRepos.Add(tr);
                }
            }
        }

        // Merge workspace repos (user-specified repos win on URL conflict)
        if (workspace is not null && !string.IsNullOrEmpty(workspace.GitRepositories))
        {
            var wsRepos = JsonSerializer.Deserialize<List<GitRepositoryConfig>>(workspace.GitRepositories);
            if (wsRepos is not null)
            {
                var existingUrls = gitRepos.Select(r => r.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var wr in wsRepos.Where(wr => !existingUrls.Contains(wr.Url)))
                {
                    gitRepos.Add(wr);
                }
            }
        }

        // Deduplicate by URL (user-specified wins)
        gitRepos = gitRepos
            .GroupBy(r => r.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // Probe repository URLs for accessibility and credential validation (unless skipped)
        if (gitRepos.Count > 0 && !request.SkipUrlValidation)
        {
            var probeErrors = await _probeService.ProbeRepositoriesAsync(
                gitRepos, request.OwnerId ?? "system", requireCredentials: true, ct);
            if (probeErrors.Count > 0)
                throw new ArgumentException(string.Join("; ", probeErrors));
        }

        // Create ContainerGitRepository entities
        var hasGitRepos = false;
        foreach (var repoConfig in gitRepos)
        {
            var gitRepo = new ContainerGitRepository
            {
                ContainerId = container.Id,
                Url = repoConfig.Url,
                Branch = repoConfig.Branch,
                TargetPath = repoConfig.TargetPath ?? "/workspace",
                CredentialRef = repoConfig.CredentialRef,
                CloneDepth = repoConfig.CloneDepth,
                Submodules = repoConfig.Submodules,
                IsFromTemplate = !string.IsNullOrEmpty(template.GitRepositories) &&
                    (request.GitRepositories is null || !request.GitRepositories.Any(r => r.Url == repoConfig.Url)),
                CloneStatus = GitCloneStatus.Pending
            };
            _db.ContainerGitRepositories.Add(gitRepo);
            hasGitRepos = true;
        }

        await _db.SaveChangesAsync(ct);

        // Parse post-create scripts from template
        IReadOnlyList<string>? postCreateScripts = null;
        if (!string.IsNullOrEmpty(template.Scripts))
        {
            try
            {
                var scripts = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(template.Scripts);
                if (scripts?.TryGetValue("post_create", out var script) == true && !string.IsNullOrWhiteSpace(script))
                    postCreateScripts = [script];
            }
            catch (System.Text.Json.JsonException)
            {
                _logger.LogWarning("Failed to parse scripts for template {TemplateCode}", template.Code);
            }
        }

        // rivoli-ai/conductor#946 (M1.5.4). The legacy
        // `ApiKeyCredentials` table that used to hold per-user provider
        // keys has been retired — keys now live in andy-settings and
        // reach the container via the per-tool proxy routing below
        // (#944). The only fields we still honour here are the explicit
        // overrides on the request's `CodeAssistantConfig`:
        //
        //   - `ApiBaseUrl` — set when the user picked Ollama or an
        //     OpenAI-compatible self-hosted backend in the launch UI
        //     (conductor#948). The proxy routing block below skips
        //     these cases, so the user-supplied URL is the only
        //     base URL the container sees.
        //   - `ModelName` — optional default model the install script
        //     should pick when starting the assistant.
        //
        // Everything else (the per-tool key + the proxy URL for the
        // default backends) is set by the proxy routing block lower
        // down, after the per-container service token is minted.
        Dictionary<string, string>? envVars = null;
        if (codeAssistant is not null)
        {
            if (!string.IsNullOrEmpty(codeAssistant.ApiBaseUrl))
            {
                var baseUrlEnv = codeAssistant.ApiBaseUrlEnvVar ?? "OPENAI_API_BASE";
                envVars ??= new Dictionary<string, string>();
                envVars[baseUrlEnv] = codeAssistant.ApiBaseUrl;
                _logger.LogInformation("Injecting user-supplied base URL {Url} as {EnvVar}",
                    UrlRedactor.Redact(codeAssistant.ApiBaseUrl), baseUrlEnv);
            }

            if (!string.IsNullOrEmpty(codeAssistant.ModelName))
            {
                var modelEnv = codeAssistant.ModelEnvVar ?? GetDefaultModelEnvVar(codeAssistant.Tool);
                envVars ??= new Dictionary<string, string>();
                envVars[modelEnv] = codeAssistant.ModelName;
                _logger.LogInformation("Injecting model {Model} as {EnvVar}", codeAssistant.ModelName, modelEnv);
            }
        }

        // Merge user-specified env vars (don't override API key)
        if (request.EnvironmentVariables is { Count: > 0 })
        {
            envVars ??= new Dictionary<string, string>();
            foreach (var kv in request.EnvironmentVariables.Where(kv => !envVars.ContainsKey(kv.Key)))
                envVars[kv.Key] = kv.Value;
        }

        // Merge template-default env vars (lowest precedence — codeAssistant
        // config and explicit request env both win). This is how a template
        // ships standing configuration: an agent-runtime template (e.g.
        // andy-cli-agent) carries its provider/model defaults so a container
        // launched from the catalog — including from the Sessions UI, which
        // sends only templateCode and no env — comes up configured.
        envVars = MergeTemplateEnvDefaults(envVars, template.EnvironmentVariables, template.Code, _logger);

        // rivoli-ai/conductor#1947. Per-run token attribution. Inject the
        // run/task/agent identity this container is executing so the
        // in-container agent can forward them as X-Andy-Run-Id /
        // X-Andy-Task-Id / X-Andy-Agent-Id on every andy-models proxy
        // call — that's what makes a single headless run's token+cost
        // usage queryable per run/task in the andy-models ledger and on
        // the gen_ai metric stream. Nullable: a container with no run
        // context leaves them unset and the env vars are omitted. We
        // don't overwrite a value a caller already supplied explicitly.
        InjectAttributionEnv(ref envVars, "ANDY_RUN_ID", request.RunId);
        InjectAttributionEnv(ref envVars, "ANDY_TASK_ID", request.TaskId);
        InjectAttributionEnv(ref envVars, "ANDY_AGENT_ID", request.AttributionAgentId);

        // #944. Inject the proxy URL + service token so a code
        // assistant installed inside the container can authenticate
        // against andy-models without the user supplying a token.
        // Both are best-effort: a missing config or a token-mint
        // failure logs a warning and the container still starts.
        // User-supplied env vars (above) win — if a caller already
        // set ANDY_PROXY_BASE_URL or ANDY_SERVICE_TOKEN explicitly,
        // we don't overwrite.
        var containerFacingProxyUrl = _configuration.GetValue<string?>("Proxy:ContainerFacingBaseUrl");
        if (!string.IsNullOrWhiteSpace(containerFacingProxyUrl))
        {
            envVars ??= new Dictionary<string, string>();
            if (!envVars.ContainsKey("ANDY_PROXY_BASE_URL"))
            {
                envVars["ANDY_PROXY_BASE_URL"] = containerFacingProxyUrl;
                _logger.LogInformation("Injecting ANDY_PROXY_BASE_URL={Url} into container env",
                    UrlRedactor.Redact(containerFacingProxyUrl));
            }
        }
        // rivoli-ai/conductor#943 (M1.5.1). Per-container proxy token.
        // When the chosen code assistant requires proxy access (non-
        // empty slug list — see ToolSlugDefaults), call andy-models to
        // mint a JWT scoped to {containerId, allowedSlugs[]} and inject
        // THAT as ANDY_SERVICE_TOKEN — narrower than the M2M bearer
        // we'd otherwise hand the container.
        //
        // No code assistant or empty slug list → fall back to the
        // shared M2M token below (pre-#943 behaviour). That covers
        // Ollama, OpenAI-compatible self-hosted, and "no assistant
        // pre-installed" templates.
        var requiredSlugs = codeAssistant is null
            ? Array.Empty<string>()
            : ToolSlugDefaults.Resolve(codeAssistant);
        var injectedProxyToken = false;
        if (_proxyTokenService is not null
            && requiredSlugs.Count > 0
            && (envVars is null || !envVars.ContainsKey("ANDY_SERVICE_TOKEN")))
        {
            MintedProxyToken? minted;
            try
            {
                minted = await _proxyTokenService.MintForContainerAsync(
                    container.Id.ToString(),
                    container.OwnerId,
                    requiredSlugs,
                    ct);
            }
            catch (ProxyTokenException ex)
            {
                // Hard fail. The assistant inside the container would
                // otherwise start with no working credential and the
                // user would see opaque 401s from the model surface
                // long after the create call returned success. Better
                // to surface the andy-models health problem now.
                throw new InvalidOperationException(
                    $"Container creation failed: could not mint per-container proxy token from andy-models for assistant " +
                    $"'{codeAssistant?.Tool}'. Check andy-models health and AndyModels:BaseUrl configuration. " +
                    $"Underlying error: {ex.Message}",
                    ex);
            }
            if (minted is not null)
            {
                container.ProxyServiceTokenId = minted.TokenId;
                container.ProxyTokenIssuedAt = DateTime.UtcNow;
                container.ProxyServiceToken = _proxyTokenProtector is not null
                    ? _proxyTokenProtector.Protect(minted.Jwt)
                    : minted.Jwt;
                envVars ??= new Dictionary<string, string>();
                envVars["ANDY_SERVICE_TOKEN"] = minted.Jwt;
                injectedProxyToken = true;
                _logger.LogInformation(
                    "Injected per-container ANDY_SERVICE_TOKEN (tokenId={TokenId}, slugs={Slugs}, length={Length}) into container env",
                    minted.TokenId, string.Join(",", requiredSlugs), minted.Jwt.Length);

                // rivoli-ai/conductor#944 (M1.5.2). Set the tool-specific
                // env vars so the code assistant inside the container
                // routes through the andy-models proxy with the per-
                // container service token. Without this, Claude Code
                // would read its real `ANTHROPIC_API_KEY` from the
                // credentials path (if set) and skip the proxy entirely,
                // losing the UsageEvent log + the proxy's key resolution.
                //
                // We overwrite anything the credentials-based path set
                // — the proxy mode is the architecture intent, the
                // direct-credential path is a fallback.
                var routing = CodeAssistantProxyRouting.For(codeAssistant!);
                if (routing is not null
                    && !string.IsNullOrWhiteSpace(containerFacingProxyUrl))
                {
                    var dialectURL = CodeAssistantProxyRouting.BuildBaseUrl(
                        containerFacingProxyUrl,
                        routing.DialectPath);
                    envVars[routing.KeyEnvVar] = minted.Jwt;
                    envVars[routing.BaseUrlEnvVar] = dialectURL;
                    _logger.LogInformation(
                        "Routed {Tool} through andy-models proxy: {KeyEnv}=<jwt>, {BaseEnv}={Url}",
                        codeAssistant!.Tool, routing.KeyEnvVar, routing.BaseUrlEnvVar,
                        UrlRedactor.Redact(dialectURL));
                }
                else if (routing is null && codeAssistant is not null)
                {
                    _logger.LogInformation(
                        "Code assistant {Tool} has no proxy routing entry — leaving credential-path env vars in place",
                        codeAssistant.Tool);
                }
            }
        }

        // Fall back to the shared M2M bearer when no per-container
        // token was minted. Tests + assistant-less containers + Ollama
        // / OpenAI-compatible self-hosted setups all hit this path.
        if (!injectedProxyToken
            && _serviceTokenService is not null
            && (envVars is null || !envVars.ContainsKey("ANDY_SERVICE_TOKEN")))
        {
            try
            {
                var serviceToken = await _serviceTokenService.GetAccessTokenAsync(ct);
                envVars ??= new Dictionary<string, string>();
                envVars["ANDY_SERVICE_TOKEN"] = serviceToken;
                _logger.LogInformation(
                    "Injected shared ANDY_SERVICE_TOKEN (length={Length}) into container env (no per-container slugs required)",
                    serviceToken.Length);

                // The headless andy-cli agent (plan execution) and any other
                // OpenAI-SDK client read OPENAI_API_KEY + OPENAI_BASE_URL. The
                // codeAssistant routing block above only runs for a configured
                // assistant; an assistant-less headless run lands here, so
                // point the OpenAI dialect at the andy-models proxy.
                if (!string.IsNullOrWhiteSpace(containerFacingProxyUrl))
                {
                    // andy-models exposes the OpenAI-compatible chat surface at
                    // `/models/v1/chat/completions` (the OpenAI SDK appends
                    // `/chat/completions` to OPENAI_API_BASE). The `openai/v1`
                    // dialect mount returns 405 for POST — use the plain `v1`.
                    var openAiBaseUrl = CodeAssistantProxyRouting.BuildBaseUrl(
                        containerFacingProxyUrl, "v1");
                    // Set BOTH base-URL env names: the OpenAI SDK family uses
                    // OPENAI_BASE_URL, but andy-cli's Andy.Llm provider (and
                    // older clients like aider) read OPENAI_API_BASE.
                    if (!envVars.ContainsKey("OPENAI_BASE_URL"))
                    {
                        envVars["OPENAI_BASE_URL"] = openAiBaseUrl;
                    }
                    if (!envVars.ContainsKey("OPENAI_API_BASE"))
                    {
                        envVars["OPENAI_API_BASE"] = openAiBaseUrl;
                    }

                    // The proxy validates aud=urn:andy-models-api; the shared
                    // andy-containers M2M token (aud=urn:andy-containers-api) is
                    // rejected 401. Mint a per-container proxy token ISSUED BY
                    // andy-models (correct audience + denylist-revocable),
                    // scoped to the default model slugs, and hand THAT to the
                    // OpenAI client.
                    if (_proxyTokenService is not null && !envVars.ContainsKey("OPENAI_API_KEY"))
                    {
                        var modelSlugs = _configuration.GetSection("Proxy:HeadlessModelSlugs").Get<string[]>();
                        if (modelSlugs is null || modelSlugs.Length == 0)
                        {
                            modelSlugs = new[] { "deepseek-v4-flash" };
                        }
                        try
                        {
                            var modelToken = await _proxyTokenService.MintForContainerAsync(
                                container.Id.ToString(), container.OwnerId, modelSlugs, ct);
                            if (modelToken is not null)
                            {
                                envVars["OPENAI_API_KEY"] = modelToken.Jwt;
                                container.ProxyServiceTokenId ??= modelToken.TokenId;
                                _logger.LogInformation(
                                    "Minted andy-models proxy token for headless OpenAI client (tokenId={TokenId}, slugs={Slugs}); OPENAI_BASE_URL={Url}",
                                    modelToken.TokenId, string.Join(",", modelSlugs),
                                    UrlRedactor.Redact(openAiBaseUrl));
                            }
                        }
                        catch (ProxyTokenException ex)
                        {
                            _logger.LogWarning(ex,
                                "Failed to mint andy-models proxy token for headless container {ContainerName}; OpenAI calls will 401.",
                                container.Name);
                        }
                    }
                }
            }
            catch (Exception tokenEx)
            {
                // Don't fail container creation. The container will
                // start without a service token; an assistant inside
                // it that tries to call andy-models will hit a 401
                // and the user can still configure a personal API
                // key via the existing apiKey resolution path.
                _logger.LogWarning(tokenEx,
                    "Failed to mint shared service token for container {ContainerName}; container will start without ANDY_SERVICE_TOKEN.",
                    container.Name);
            }
        }

        // X4: profile-driven overrides. When a profile is bound, its
        // BaseImageRef wins over the template's BaseImage, and the
        // sidecar GuiType is derived from profile.Kind (Desktop → "vnc",
        // Headless / Terminal → "none"). Without a profile, fall back
        // to the template's existing values for full back-compat.
        var effectiveImage = profile?.BaseImageRef ?? resolvedTemplateImage;
        var effectiveGuiType = profile is null
            ? template.GuiType
            : (profile.Kind == EnvironmentKind.Desktop ? "vnc" : "none");

        // rivoli-ai/andy-containers#125. Strict mode: refuse to
        // provision against a mutable tag. The flag is opt-in
        // (default false) so dev workflows that rely on `:latest`
        // still work; production deploys flip it on and pin every
        // template to `@sha256:...`. Locally-built `andy-desktop-*`
        // images are exempt — they're built from the repo's own
        // Dockerfiles and never pulled from a registry, so a
        // substitution attacker can't reach them.
        var requireDigestPin = _configuration.GetValue<bool?>("Containers:Image:RequireDigestPin") ?? false;
        if (requireDigestPin
            && !effectiveImage.StartsWith("andy-desktop-", StringComparison.Ordinal)
            && !Andy.Containers.Validation.OciReferenceValidator.IsDigestPinned(effectiveImage))
        {
            throw new ArgumentException(
                $"Containers:Image:RequireDigestPin is enabled and image '{effectiveImage}' is not digest-pinned. " +
                $"Use a reference of the form 'name@sha256:<hex>' to pin against mutable-tag substitution.",
                nameof(request));
        }

        // Enqueue the provisioning job for the background worker
        var job = new ContainerProvisionJob(
            ContainerId: container.Id,
            ProviderId: provider.Id,
            ProviderCode: provider.Code,
            TemplateBaseImage: effectiveImage,
            ContainerName: container.Name,
            OwnerId: container.OwnerId,
            Resources: request.Resources,
            Gpu: request.Gpu,
            HasGitRepositories: hasGitRepos,
            PostCreateScripts: postCreateScripts,
            CodeAssistant: codeAssistant,
            EnvironmentVariables: envVars,
            GuiType: effectiveGuiType,
            ContainerUser: container.ContainerUser ?? "root",
            OwnerEmail: request.OwnerEmail,
            OwnerPreferredUsername: request.OwnerPreferredUsername,
            TemplateName: template.Name,
            ProviderName: provider.Name,
            EnvironmentProfileId: profile?.Id,
            EnvironmentKind: profile?.Kind.ToString());

        await _queue.EnqueueAsync(job, ct);
        _logger.LogInformation("Container {ContainerId} enqueued for provisioning on {Provider}",
            container.Id, provider.Code);

        // OT7 (rivoli-ai/conductor#1265). `provider` → `andy.containers.provider`.
        // Legacy name dual-emits during 0.2.4 transition; removed in 0.3.0.
        Meters.ContainersCreated.Add(1,
            new KeyValuePair<string, object?>("andy.containers.provider", container.Provider),
            new KeyValuePair<string, object?>("provider", container.Provider)); // deprecated

        return container;
    }

    /// <summary>
    /// #274 (P1F1). Resolves the most-recent IM-produced
    /// <see cref="Andy.Containers.Models.ImageManagement.BuildArtifactEntity"/>
    /// for <paramref name="templateId"/> in the primary registry into a
    /// fully-qualified digest-pinned image reference of the form
    /// <c>{authority}/{repoPath}@{digest}</c>. Returns <c>null</c> when:
    /// (a) the store/config aren't wired (unit-test back-compat path);
    /// (b) no artifact exists for this template;
    /// (c) the artifact has no reference in the primary registry
    ///     (rare — would mean an artifact row was persisted without
    ///     a successful push, which the orchestrator doesn't currently
    ///     do, but we guard anyway);
    /// (d) the primary registry's URL can't be parsed (config error).
    /// The caller falls back to <c>template.BaseImage</c> on null.
    /// </summary>
    private async Task<string?> ResolveTemplateImageRefAsync(Guid templateId, CancellationToken ct)
    {
        if (_buildArtifactStore is null || _registryConfiguration is null)
        {
            return null;
        }

        string primaryRegistryId;
        try
        {
            primaryRegistryId = _registryConfiguration.PrimaryRegistryId;
        }
        catch (InvalidOperationException)
        {
            // No registries configured — pre-IM deployment. Legitimate
            // fall-back to BaseImage.
            return null;
        }

        var (items, _) = await _buildArtifactStore.ListAsync(
            templateId: templateId,
            registryId: primaryRegistryId,
            skip: 0,
            take: 1,
            ct: ct).ConfigureAwait(false);
        if (items.Count == 0)
        {
            return null;
        }

        var artifact = items[0];
        var reference = artifact.References
            .FirstOrDefault(r => r.RegistryId == primaryRegistryId);
        if (reference is null)
        {
            _logger.LogWarning(
                "BuildArtifact {ArtifactId} for template {TemplateId} has no RegistryReference in primary registry {RegistryId}; falling back to template.BaseImage.",
                artifact.Id, templateId, primaryRegistryId);
            return null;
        }

        var registryEntry = _registryConfiguration.GetByIdOrThrow(primaryRegistryId);
        if (!Uri.TryCreate(registryEntry.Url, UriKind.Absolute, out var url))
        {
            _logger.LogWarning(
                "Primary registry {RegistryId} has unparseable URL '{Url}'; falling back to template.BaseImage.",
                primaryRegistryId, registryEntry.Url);
            return null;
        }

        var authority = url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}";
        return $"{authority}/{reference.RepoPath}@{artifact.Digest}";
    }

    public async Task<Container> GetContainerAsync(Guid containerId, CancellationToken ct)
    {
        return await _db.Containers
            .Include(c => c.Template)
            .Include(c => c.Provider)
            .FirstOrDefaultAsync(c => c.Id == containerId, ct)
            ?? throw new KeyNotFoundException($"Container {containerId} not found");
    }

    public async Task<IReadOnlyList<Container>> ListContainersAsync(ContainerFilter filter, CancellationToken ct)
    {
        var query = _db.Containers
            .Include(c => c.Template)
            .Include(c => c.Provider)
            .AsQueryable();

        if (!string.IsNullOrEmpty(filter.OwnerId))
            query = query.Where(c => c.OwnerId == filter.OwnerId);
        if (filter.OrganizationId.HasValue)
            query = query.Where(c => c.OrganizationId == filter.OrganizationId);
        if (filter.TeamId.HasValue)
            query = query.Where(c => c.TeamId == filter.TeamId);
        if (filter.WorkspaceId.HasValue)
            query = query.Where(c => _db.Workspaces.Any(w => w.Id == filter.WorkspaceId && w.Containers.Contains(c)));
        if (filter.Status.HasValue)
            query = query.Where(c => c.Status == filter.Status);
        if (filter.TemplateId.HasValue)
            query = query.Where(c => c.TemplateId == filter.TemplateId);
        if (filter.ProviderId.HasValue)
            query = query.Where(c => c.ProviderId == filter.ProviderId);
        if (filter.Source.HasValue)
            query = query.Where(c => c.CreationSource == filter.Source);

        query = query.OrderByDescending(c => c.CreatedAt);

        if (filter.Skip.HasValue)
            query = query.Skip(filter.Skip.Value);
        if (filter.Take.HasValue)
            query = query.Take(filter.Take.Value);
        else
            query = query.Take(20);

        return await query.ToListAsync(ct);
    }

    public async Task StartContainerAsync(Guid containerId, CancellationToken ct)
    {
        using var activity = ActivitySources.Provisioning.StartActivity("StartContainer");
        activity?.SetTag("andy.containers.id", containerId.ToString());
        activity?.SetTag("containerId", containerId.ToString()); // deprecated; removed in Andy.Telemetry 0.3.0 (OT7 / #1265)

        var container = await GetContainerAsync(containerId, ct);
        if (container.Status != ContainerStatus.Stopped)
            throw new InvalidOperationException($"Container is {container.Status}, cannot start");

        var infra = _providerFactory.GetProvider(container.Provider!);
        await infra.StartContainerAsync(container.ExternalId!, ct);

        container.Status = ContainerStatus.Running;
        container.StartedAt = DateTime.UtcNow;
        container.StoppedAt = null;
        _db.Events.Add(new ContainerEvent { ContainerId = containerId, EventType = ContainerEventType.Started });
        await _db.SaveChangesAsync(ct);
    }

    public async Task StopContainerAsync(Guid containerId, CancellationToken ct)
    {
        using var activity = ActivitySources.Provisioning.StartActivity("StopContainer");
        activity?.SetTag("andy.containers.id", containerId.ToString());
        activity?.SetTag("containerId", containerId.ToString()); // deprecated; removed in Andy.Telemetry 0.3.0 (OT7 / #1265)

        var container = await GetContainerAsync(containerId, ct);
        if (container.Status != ContainerStatus.Running)
            throw new InvalidOperationException($"Container is {container.Status}, cannot stop");

        var infra = _providerFactory.GetProvider(container.Provider!);

        // #316. Collect artifacts BEFORE stopping the container — the
        // probe runs in-band via ExecAsync so the container must still
        // be Running when we ask. A Stopped container would just yield
        // an exec-failed warning and an empty manifest.
        var artifacts = await TryCollectArtifactsAsync(container, ct);

        await infra.StopContainerAsync(container.ExternalId!, ct);

        container.Status = ContainerStatus.Stopped;
        container.StoppedAt = DateTime.UtcNow;
        _db.Events.Add(new ContainerEvent { ContainerId = containerId, EventType = ContainerEventType.Stopped });
        // Emit andy.containers.events.run.<id>.finished — clean stop.
        var durationSeconds = (container.StartedAt.HasValue && container.StoppedAt.HasValue)
            ? (container.StoppedAt.Value - container.StartedAt.Value).TotalSeconds
            : (double?)null;
        _db.AppendRunEvent(container, RunEventKind.Finished,
            exitCode: null, durationSeconds: durationSeconds, outputArtifacts: artifacts);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DestroyContainerAsync(Guid containerId, CancellationToken ct)
    {
        using var activity = ActivitySources.Provisioning.StartActivity("DeleteContainer");
        activity?.SetTag("andy.containers.id", containerId.ToString());
        activity?.SetTag("containerId", containerId.ToString()); // deprecated; removed in Andy.Telemetry 0.3.0 (OT7 / #1265)

        var container = await GetContainerAsync(containerId, ct);

        // #316. Collect artifacts BEFORE the destroy call wipes the
        // filesystem. Only meaningful while the container is still
        // running; for already-stopped rows (e.g. a destroy after a
        // prior stop) the probe will exec-fail and the helper returns
        // null — terminal event still publishes, just without a manifest.
        var artifacts = container.Status == ContainerStatus.Running
            ? await TryCollectArtifactsAsync(container, ct)
            : null;

        if (container.ExternalId is not null)
        {
            var infra = _providerFactory.GetProvider(container.Provider!);
            await infra.DestroyContainerAsync(container.ExternalId, ct);
        }

        // rivoli-ai/conductor#943 (M1.5.1). Revoke the per-container
        // proxy token if we minted one. AndyModelsProxyTokenService
        // swallows transport errors itself so a slow / down andy-models
        // can't wedge container destroy — the token's `exp` claim is
        // the backstop.
        if (_proxyTokenService is not null && container.ProxyServiceTokenId is { } tokenId)
        {
            await _proxyTokenService.RevokeAsync(tokenId, ct);
            container.ProxyServiceToken = null;
            container.ProxyServiceTokenId = null;
            container.ProxyTokenIssuedAt = null;
        }

        container.Status = ContainerStatus.Destroyed;
        _db.Events.Add(new ContainerEvent { ContainerId = containerId, EventType = ContainerEventType.Destroyed });
        // Emit andy.containers.events.run.<id>.cancelled — explicit teardown.
        var destroyedDuration = (container.StartedAt.HasValue)
            ? (DateTime.UtcNow - container.StartedAt.Value).TotalSeconds
            : (double?)null;
        _db.AppendRunEvent(container, RunEventKind.Cancelled,
            exitCode: null, durationSeconds: destroyedDuration, outputArtifacts: artifacts);
        await _db.SaveChangesAsync(ct);

        Meters.ContainersDeleted.Add(1);
    }

    public async Task<ExecResult> ExecAsync(Guid containerId, string command, CancellationToken ct)
    {
        using var activity = ActivitySources.Provisioning.StartActivity("ExecCommand");
        activity?.SetTag("andy.containers.id", containerId.ToString());
        activity?.SetTag("containerId", containerId.ToString()); // deprecated; removed in Andy.Telemetry 0.3.0 (OT7 / #1265)

        var container = await GetContainerAsync(containerId, ct);
        // Allow exec on Running (normal) and Creating (provisioning worker running setup scripts)
        if (container.Status is not (ContainerStatus.Running or ContainerStatus.Creating))
            throw new InvalidOperationException($"Container is {container.Status}, cannot exec");
        if (string.IsNullOrEmpty(container.ExternalId))
            throw new InvalidOperationException("Container has no external ID yet");

        var infra = _providerFactory.GetProvider(container.Provider!);
        return await infra.ExecAsync(container.ExternalId!, command, ct);
    }

    public Task<ExecResult> ExecAsync(Guid containerId, string command, TimeSpan timeout, CancellationToken ct)
        => ExecAsync(containerId, command, timeout, workingDir: null, ct);

    public async Task<ExecResult> ExecAsync(Guid containerId, string command, TimeSpan timeout, string? workingDir, CancellationToken ct)
    {
        using var activity = ActivitySources.Provisioning.StartActivity("ExecCommand");
        activity?.SetTag("andy.containers.id", containerId.ToString());
        activity?.SetTag("containerId", containerId.ToString()); // deprecated; removed in Andy.Telemetry 0.3.0 (OT7 / #1265)
        activity?.SetTag("andy.containers.timeout_seconds", timeout.TotalSeconds);
        activity?.SetTag("timeout", timeout.TotalSeconds); // deprecated; removed in Andy.Telemetry 0.3.0 (OT7 / #1265)
        if (!string.IsNullOrWhiteSpace(workingDir))
            activity?.SetTag("andy.containers.working_dir", workingDir);

        var container = await GetContainerAsync(containerId, ct);
        if (container.Status is not (ContainerStatus.Running or ContainerStatus.Creating))
            throw new InvalidOperationException($"Container is {container.Status}, cannot exec");
        if (string.IsNullOrEmpty(container.ExternalId))
            throw new InvalidOperationException("Container has no external ID yet");

        var infra = _providerFactory.GetProvider(container.Provider!);
        return await infra.ExecAsync(container.ExternalId!, command, timeout, workingDir, ct);
    }

    // F4.1 (rivoli-ai/conductor#1934). Mid-run streaming exec. Resolves
    // the container + provider exactly like the buffered overload, then
    // delegates to the provider's streaming exec so the runner sees each
    // andy-cli line as it lands. Providers that can't stream fall back to
    // the interface default (buffered, replayed at end).
    public async Task<ExecResult> ExecStreamingAsync(
        Guid containerId, string command, TimeSpan timeout,
        Action<ExecOutputChunk> onLine, CancellationToken ct,
        string? workingDir = null)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        using var activity = ActivitySources.Provisioning.StartActivity("ExecCommandStreaming");
        activity?.SetTag("andy.containers.id", containerId.ToString());
        activity?.SetTag("andy.containers.timeout_seconds", timeout.TotalSeconds);
        if (!string.IsNullOrWhiteSpace(workingDir))
            activity?.SetTag("andy.containers.working_dir", workingDir);

        var container = await GetContainerAsync(containerId, ct);
        if (container.Status is not (ContainerStatus.Running or ContainerStatus.Creating))
            throw new InvalidOperationException($"Container is {container.Status}, cannot exec");
        if (string.IsNullOrEmpty(container.ExternalId))
            throw new InvalidOperationException("Container has no external ID yet");

        var infra = _providerFactory.GetProvider(container.Provider!);
        return await infra.ExecStreamingAsync(container.ExternalId!, command, timeout, onLine, ct, workingDir);
    }

    public async Task<ConnectionInfo> GetConnectionInfoAsync(Guid containerId, CancellationToken ct)
    {
        var container = await GetContainerAsync(containerId, ct);
        if (container.ExternalId is null)
            return new ConnectionInfo();

        var infra = _providerFactory.GetProvider(container.Provider!);
        return await infra.GetConnectionInfoAsync(container.ExternalId, ct);
    }

    public async Task<MappedPort> ExposePortAsync(Guid containerId, int containerPort, CancellationToken ct)
    {
        var container = await GetContainerAsync(containerId, ct);
        if (container.ExternalId is null)
            throw new InvalidOperationException("Container has no external ID");
        if (container.Status != ContainerStatus.Running)
            throw new InvalidOperationException($"Container is {container.Status}, must be Running to expose a port");

        var infra = _providerFactory.GetProvider(container.Provider!);
        return await infra.ExposePortAsync(container.ExternalId, containerPort, ct);
    }

    public async Task<ContainerStats> GetContainerStatsAsync(Guid containerId, CancellationToken ct)
    {
        var container = await GetContainerAsync(containerId, ct);
        if (container.ExternalId is null || container.Status != ContainerStatus.Running)
            return new ContainerStats();

        var infra = _providerFactory.GetProvider(container.Provider!);
        return await infra.GetContainerStatsAsync(container.ExternalId, ct);
    }

    public async Task ResizeContainerAsync(Guid containerId, ResourceSpec resources, CancellationToken ct)
    {
        var container = await GetContainerAsync(containerId, ct);
        if (container.ExternalId is null)
            throw new InvalidOperationException("Container has no external ID");
        if (container.Status != ContainerStatus.Running)
            throw new InvalidOperationException($"Container is {container.Status}, must be Running to resize");

        var infra = _providerFactory.GetProvider(container.Provider!);
        await infra.ResizeContainerAsync(container.ExternalId, resources, ct);

        // Update stored allocation
        container.AllocatedResources = System.Text.Json.JsonSerializer.Serialize(resources);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Container {ContainerId} resized to {CpuCores} CPU, {MemoryMb}MB RAM",
            containerId, resources.CpuCores, resources.MemoryMb);
    }

    /// <summary>
    /// Merges a template's JSON-encoded default environment variables into the
    /// container env, WITHOUT overriding any key already set (codeAssistant
    /// config and explicit request env both take precedence). This is how a
    /// template ships standing configuration so a container launched from the
    /// catalog — including the Sessions UI, which sends only templateCode and no
    /// env — comes up configured. Null/empty input and invalid JSON are ignored
    /// (the latter logged), never throwing out of the create path.
    /// </summary>
    internal static Dictionary<string, string>? MergeTemplateEnvDefaults(
        Dictionary<string, string>? envVars,
        string? templateEnvJson,
        string templateCode,
        ILogger logger)
    {
        if (string.IsNullOrEmpty(templateEnvJson))
        {
            return envVars;
        }

        Dictionary<string, string>? templateEnv;
        try
        {
            templateEnv = JsonSerializer.Deserialize<Dictionary<string, string>>(templateEnvJson);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "Failed to parse EnvironmentVariables for template {TemplateCode}", templateCode);
            return envVars;
        }

        if (templateEnv is not { Count: > 0 })
        {
            return envVars;
        }

        envVars ??= new Dictionary<string, string>();
        foreach (var kv in templateEnv.Where(kv => !envVars.ContainsKey(kv.Key)))
        {
            envVars[kv.Key] = kv.Value;
        }

        return envVars;
    }

    private static string GetDefaultModelEnvVar(CodeAssistantType tool) => tool switch
    {
        CodeAssistantType.Aider => "AIDER_MODEL",
        CodeAssistantType.OpenCode => "LLM_MODEL",
        CodeAssistantType.CodexCli => "OPENAI_MODEL",
        _ => "LLM_MODEL"
    };

    // rivoli-ai/conductor#1947. Set a per-run attribution env var when a
    // value is present, never overwriting one a caller already supplied.
    // Blank/whitespace values are treated as "unset" so the env var is
    // omitted rather than emitting an empty header downstream.
    private static void InjectAttributionEnv(ref Dictionary<string, string>? envVars, string envVarName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        envVars ??= new Dictionary<string, string>();
        if (!envVars.ContainsKey(envVarName))
        {
            envVars[envVarName] = value.Trim();
        }
    }
}
