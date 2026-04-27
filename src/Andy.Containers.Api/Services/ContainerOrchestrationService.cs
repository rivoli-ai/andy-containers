using System.Diagnostics;
using System.Text.Json;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Telemetry;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Messaging;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;
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
    private readonly IApiKeyService _apiKeyService;
    private readonly ILogger<ContainerOrchestrationService> _logger;

    public ContainerOrchestrationService(
        ContainersDbContext db,
        IInfrastructureRoutingService routing,
        IInfrastructureProviderFactory providerFactory,
        ContainerProvisioningQueue queue,
        IGitRepositoryProbeService probeService,
        IApiKeyService apiKeyService,
        ILogger<ContainerOrchestrationService> logger)
    {
        _db = db;
        _routing = routing;
        _providerFactory = providerFactory;
        _queue = queue;
        _probeService = probeService;
        _apiKeyService = apiKeyService;
        _logger = logger;
    }

    public async Task<Container> CreateContainerAsync(CreateContainerRequest request, CancellationToken ct)
    {
        using var activity = ActivitySources.Provisioning.StartActivity("CreateContainer");
        activity?.SetTag("templateId", request.TemplateId?.ToString() ?? request.TemplateCode);
        activity?.SetTag("provider", request.ProviderCode ?? request.ProviderId?.ToString());

        // Resolve template
        var template = request.TemplateId.HasValue
            ? await _db.Templates.FindAsync([request.TemplateId.Value], ct)
            : await _db.Templates.FirstOrDefaultAsync(t => t.Code == request.TemplateCode, ct);

        if (template is null)
            throw new ArgumentException("Template not found");

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
                ImageReference = template.BaseImage,
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

        // Resolve API key for code assistant if configured
        Dictionary<string, string>? envVars = null;
        if (codeAssistant is not null)
        {
            var assistantProvider = MapCodeAssistantToApiKeyProvider(codeAssistant.Tool);
            // Try primary provider first, then fallback to compatible providers
            var resolvedCred = await _apiKeyService.ResolveCredentialAsync(container.OwnerId, assistantProvider, ct);
            if (resolvedCred is null)
            {
                // For OpenAI-compatible tools, try OpenRouter, then OpenAI, then Custom
                var fallbackProviders = new[] { ApiKeyProvider.OpenRouter, ApiKeyProvider.OpenAI, ApiKeyProvider.OpenAiCompatible, ApiKeyProvider.Custom };
                foreach (var fallback in fallbackProviders)
                {
                    if (fallback == assistantProvider) continue;
                    resolvedCred = await _apiKeyService.ResolveCredentialAsync(container.OwnerId, fallback, ct);
                    if (resolvedCred is not null)
                    {
                        _logger.LogInformation("Primary provider {Primary} not found, using fallback {Fallback}",
                            assistantProvider, fallback);
                        assistantProvider = fallback;
                        break;
                    }
                }
            }
            if (resolvedCred is not null)
            {
                // Always use the tool's expected env var, not the fallback provider's
                var originalProvider = MapCodeAssistantToApiKeyProvider(codeAssistant.Tool);
                var envVarName = codeAssistant.ApiKeyEnvVar ?? GetDefaultEnvVar(originalProvider);
                envVars = new Dictionary<string, string> { [envVarName] = resolvedCred.ApiKey };
                _logger.LogInformation("Resolved API key for {Provider}, will inject as {EnvVar}",
                    assistantProvider, envVarName);

                // Inject base URL from credential (overrides code assistant config)
                var baseUrl = resolvedCred.BaseUrl ?? codeAssistant.ApiBaseUrl;
                if (!string.IsNullOrEmpty(baseUrl))
                {
                    var baseUrlEnv = codeAssistant.ApiBaseUrlEnvVar ?? "OPENAI_API_BASE";
                    envVars[baseUrlEnv] = baseUrl;
                    _logger.LogInformation("Injecting base URL {Url} as {EnvVar}", baseUrl, baseUrlEnv);
                }

                // Inject model name from credential (code assistant config overrides credential)
                var modelName = codeAssistant.ModelName ?? resolvedCred.ModelName;
                if (!string.IsNullOrEmpty(modelName))
                {
                    var modelEnv = codeAssistant.ModelEnvVar ?? GetDefaultModelEnvVar(codeAssistant.Tool);
                    envVars[modelEnv] = modelName;
                    _logger.LogInformation("Injecting model {Model} as {EnvVar}", modelName, modelEnv);
                }
            }
            else
            {
                _logger.LogWarning("No API key found for {Provider} for user {OwnerId}, container will start without it",
                    assistantProvider, container.OwnerId);

                // Still inject base URL from code assistant config if set
                if (!string.IsNullOrEmpty(codeAssistant.ApiBaseUrl))
                {
                    var baseUrlEnv = codeAssistant.ApiBaseUrlEnvVar ?? "OPENAI_API_BASE";
                    envVars ??= new Dictionary<string, string>();
                    envVars[baseUrlEnv] = codeAssistant.ApiBaseUrl;
                }

                // Inject LLM model name if configured
                if (!string.IsNullOrEmpty(codeAssistant.ModelName))
                {
                    var modelEnv = codeAssistant.ModelEnvVar ?? GetDefaultModelEnvVar(codeAssistant.Tool);
                    envVars ??= new Dictionary<string, string>();
                    envVars[modelEnv] = codeAssistant.ModelName;
                }
            }
        }

        // Merge user-specified env vars (don't override API key)
        if (request.EnvironmentVariables is { Count: > 0 })
        {
            envVars ??= new Dictionary<string, string>();
            foreach (var kv in request.EnvironmentVariables.Where(kv => !envVars.ContainsKey(kv.Key)))
                envVars[kv.Key] = kv.Value;
        }

        // Enqueue the provisioning job for the background worker
        var job = new ContainerProvisionJob(
            ContainerId: container.Id,
            ProviderId: provider.Id,
            ProviderCode: provider.Code,
            TemplateBaseImage: template.BaseImage,
            ContainerName: container.Name,
            OwnerId: container.OwnerId,
            Resources: request.Resources,
            Gpu: request.Gpu,
            HasGitRepositories: hasGitRepos,
            PostCreateScripts: postCreateScripts,
            CodeAssistant: codeAssistant,
            EnvironmentVariables: envVars,
            GuiType: template.GuiType,
            ContainerUser: container.ContainerUser ?? "root",
            OwnerEmail: request.OwnerEmail,
            OwnerPreferredUsername: request.OwnerPreferredUsername,
            TemplateName: template.Name,
            ProviderName: provider.Name);

        await _queue.EnqueueAsync(job, ct);
        _logger.LogInformation("Container {ContainerId} enqueued for provisioning on {Provider}",
            container.Id, provider.Code);

        Meters.ContainersCreated.Add(1, new KeyValuePair<string, object?>("provider", container.Provider));

        return container;
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
        activity?.SetTag("containerId", containerId.ToString());

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
        activity?.SetTag("containerId", containerId.ToString());

        var container = await GetContainerAsync(containerId, ct);
        if (container.Status != ContainerStatus.Running)
            throw new InvalidOperationException($"Container is {container.Status}, cannot stop");

        var infra = _providerFactory.GetProvider(container.Provider!);
        await infra.StopContainerAsync(container.ExternalId!, ct);

        container.Status = ContainerStatus.Stopped;
        container.StoppedAt = DateTime.UtcNow;
        _db.Events.Add(new ContainerEvent { ContainerId = containerId, EventType = ContainerEventType.Stopped });
        // Emit andy.containers.events.run.<id>.finished — clean stop.
        var durationSeconds = (container.StartedAt.HasValue && container.StoppedAt.HasValue)
            ? (container.StoppedAt.Value - container.StartedAt.Value).TotalSeconds
            : (double?)null;
        _db.AppendRunEvent(container, RunEventKind.Finished, exitCode: null, durationSeconds: durationSeconds);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DestroyContainerAsync(Guid containerId, CancellationToken ct)
    {
        using var activity = ActivitySources.Provisioning.StartActivity("DeleteContainer");
        activity?.SetTag("containerId", containerId.ToString());

        var container = await GetContainerAsync(containerId, ct);
        if (container.ExternalId is not null)
        {
            var infra = _providerFactory.GetProvider(container.Provider!);
            await infra.DestroyContainerAsync(container.ExternalId, ct);
        }

        container.Status = ContainerStatus.Destroyed;
        _db.Events.Add(new ContainerEvent { ContainerId = containerId, EventType = ContainerEventType.Destroyed });
        // Emit andy.containers.events.run.<id>.cancelled — explicit teardown.
        var destroyedDuration = (container.StartedAt.HasValue)
            ? (DateTime.UtcNow - container.StartedAt.Value).TotalSeconds
            : (double?)null;
        _db.AppendRunEvent(container, RunEventKind.Cancelled, exitCode: null, durationSeconds: destroyedDuration);
        await _db.SaveChangesAsync(ct);

        Meters.ContainersDeleted.Add(1);
    }

    public async Task<ExecResult> ExecAsync(Guid containerId, string command, CancellationToken ct)
    {
        using var activity = ActivitySources.Provisioning.StartActivity("ExecCommand");
        activity?.SetTag("containerId", containerId.ToString());

        var container = await GetContainerAsync(containerId, ct);
        // Allow exec on Running (normal) and Creating (provisioning worker running setup scripts)
        if (container.Status is not (ContainerStatus.Running or ContainerStatus.Creating))
            throw new InvalidOperationException($"Container is {container.Status}, cannot exec");
        if (string.IsNullOrEmpty(container.ExternalId))
            throw new InvalidOperationException("Container has no external ID yet");

        var infra = _providerFactory.GetProvider(container.Provider!);
        return await infra.ExecAsync(container.ExternalId!, command, ct);
    }

    public async Task<ExecResult> ExecAsync(Guid containerId, string command, TimeSpan timeout, CancellationToken ct)
    {
        using var activity = ActivitySources.Provisioning.StartActivity("ExecCommand");
        activity?.SetTag("containerId", containerId.ToString());
        activity?.SetTag("timeout", timeout.TotalSeconds);

        var container = await GetContainerAsync(containerId, ct);
        if (container.Status is not (ContainerStatus.Running or ContainerStatus.Creating))
            throw new InvalidOperationException($"Container is {container.Status}, cannot exec");
        if (string.IsNullOrEmpty(container.ExternalId))
            throw new InvalidOperationException("Container has no external ID yet");

        var infra = _providerFactory.GetProvider(container.Provider!);
        return await infra.ExecAsync(container.ExternalId!, command, timeout, ct);
    }

    public async Task<ConnectionInfo> GetConnectionInfoAsync(Guid containerId, CancellationToken ct)
    {
        var container = await GetContainerAsync(containerId, ct);
        if (container.ExternalId is null)
            return new ConnectionInfo();

        var infra = _providerFactory.GetProvider(container.Provider!);
        return await infra.GetConnectionInfoAsync(container.ExternalId, ct);
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

    private static ApiKeyProvider MapCodeAssistantToApiKeyProvider(CodeAssistantType tool) => tool switch
    {
        CodeAssistantType.ClaudeCode => ApiKeyProvider.Anthropic,
        CodeAssistantType.CodexCli => ApiKeyProvider.OpenAI,
        CodeAssistantType.Aider => ApiKeyProvider.OpenAI,
        CodeAssistantType.Continue => ApiKeyProvider.Custom,
        CodeAssistantType.OpenCode => ApiKeyProvider.OpenAI,
        CodeAssistantType.QwenCoder => ApiKeyProvider.Dashscope,
        CodeAssistantType.GeminiCode => ApiKeyProvider.Google,
        CodeAssistantType.GitHubCopilot => ApiKeyProvider.Custom,
        CodeAssistantType.AmazonQ => ApiKeyProvider.Custom,
        CodeAssistantType.Cline => ApiKeyProvider.Anthropic,
        _ => ApiKeyProvider.OpenAiCompatible
    };

    private static string GetDefaultModelEnvVar(CodeAssistantType tool) => tool switch
    {
        CodeAssistantType.Aider => "AIDER_MODEL",
        CodeAssistantType.OpenCode => "LLM_MODEL",
        CodeAssistantType.CodexCli => "OPENAI_MODEL",
        _ => "LLM_MODEL"
    };

    private static string GetDefaultEnvVar(ApiKeyProvider provider) => provider switch
    {
        ApiKeyProvider.Anthropic => "ANTHROPIC_API_KEY",
        ApiKeyProvider.OpenAI => "OPENAI_API_KEY",
        ApiKeyProvider.Google => "GOOGLE_API_KEY",
        ApiKeyProvider.Dashscope => "DASHSCOPE_API_KEY",
        ApiKeyProvider.OpenRouter => "OPENROUTER_API_KEY",
        ApiKeyProvider.Ollama => "OLLAMA_API_KEY",
        ApiKeyProvider.OpenAiCompatible => "OPENAI_API_KEY",
        _ => "API_KEY"
    };
}
