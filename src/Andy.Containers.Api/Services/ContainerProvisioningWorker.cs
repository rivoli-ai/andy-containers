using System.Diagnostics;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Telemetry;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Messaging;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;
using Andy.Containers.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

public class ContainerProvisioningWorker : BackgroundService
{
    private readonly ContainerProvisioningQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IInfrastructureProviderFactory _providerFactory;
    private readonly IContainerLifecycleBus _lifecycleBus;
    private readonly ILogger<ContainerProvisioningWorker> _logger;

    internal static readonly TimeSpan ProvisionTimeout = TimeSpan.FromMinutes(5);

    public ContainerProvisioningWorker(
        ContainerProvisioningQueue queue,
        IServiceScopeFactory scopeFactory,
        IInfrastructureProviderFactory providerFactory,
        IContainerLifecycleBus lifecycleBus,
        ILogger<ContainerProvisioningWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _providerFactory = providerFactory;
        _lifecycleBus = lifecycleBus;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Container provisioning worker started");

        // Recover any containers stuck in Creating/Pending from a previous crash
        await RecoverStuckContainersAsync(stoppingToken);

        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error processing provisioning job for container {ContainerId}", job.ContainerId);
            }
        }

        _logger.LogInformation("Container provisioning worker stopped");
    }

    private async Task ProcessJobAsync(ContainerProvisionJob job, CancellationToken stoppingToken)
    {
        using var activity = ActivitySources.Provisioning.StartActivity("ProvisionContainer");
        // OT7 (rivoli-ai/conductor#1265). Attributes renamed under the
        // `andy.containers.*` namespace per docs/semconv-compliance.md.
        // Legacy names dual-emit during the 0.2.4 transition window.
        var containerIdTag = job.ContainerId.ToString();
        activity?.SetTag("andy.containers.id", containerIdTag);
        activity?.SetTag("andy.containers.provider", job.ProviderCode);
        activity?.SetTag("containerId", containerIdTag);    // deprecated; removed in 0.3.0
        activity?.SetTag("provider", job.ProviderCode);     // deprecated; removed in 0.3.0
        var sw = Stopwatch.StartNew();

        _logger.LogInformation("Processing provisioning job for container {ContainerId} on provider {Provider}",
            job.ContainerId, job.ProviderCode);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();

        var container = await db.Containers.FindAsync([job.ContainerId], stoppingToken);
        if (container is null)
        {
            _logger.LogWarning("Container {ContainerId} not found in DB, skipping", job.ContainerId);
            return;
        }

        // Set status to Creating and emit the "creating" lifecycle phase.
        container.Status = ContainerStatus.Creating;
        await db.SaveChangesAsync(stoppingToken);
        PublishPhase(container, "creating", new ContainerLifecyclePhaseData());

        try
        {
            var provider = await db.Providers.FindAsync([job.ProviderId], stoppingToken);
            if (provider is null)
                throw new InvalidOperationException($"Provider {job.ProviderId} not found");

            var infra = _providerFactory.GetProvider(provider);
            var portMappings = new Dictionary<int, int> { [22] = 0 };
            // Expose noVNC websocket port for templates with VNC desktop
            if (string.Equals(job.GuiType, "vnc", StringComparison.OrdinalIgnoreCase))
                portMappings[6080] = 0;

            var spec = new ContainerSpec
            {
                ImageReference = job.TemplateBaseImage,
                Name = job.ContainerName,
                Resources = job.Resources ?? new ResourceSpec(),
                Gpu = job.Gpu,
                // VNC desktop images use /start.sh which starts VNC+websockify+SSH
                Command = string.Equals(job.GuiType, "vnc", StringComparison.OrdinalIgnoreCase)
                    ? "/start.sh" : null,
                // Always expose SSH (port 22) with a dynamic host port
                PortMappings = portMappings,
                // Inject env vars (incl. API keys) at creation time so they propagate
                // to every `docker exec` without being persisted to world-readable files
                // inside the container (/etc/environment, /etc/profile.d, etc).
                EnvironmentVariables = job.EnvironmentVariables
            };

            // Use a timeout so we don't hang forever
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeoutCts.CancelAfter(ProvisionTimeout);

            _logger.LogInformation("Calling CreateContainerAsync for {ContainerId} on {Provider} with image {Image}",
                job.ContainerId, job.ProviderCode, job.TemplateBaseImage);

            var result = await infra.CreateContainerAsync(spec, timeoutCts.Token);

            _logger.LogInformation("Provider returned ExternalId={ExternalId} Status={Status} for {ContainerId}",
                result.ExternalId, result.Status, job.ContainerId);

            container.ExternalId = result.ExternalId;
            // Keep status as Creating while post-create scripts, env vars, and
            // code assistant install run. This prevents users from connecting
            // to a container that isn't fully set up yet.
            container.Status = ContainerStatus.Creating;

            if (result.ConnectionInfo is not null)
            {
                container.IdeEndpoint = result.ConnectionInfo.IdeEndpoint;
                container.VncEndpoint = result.ConnectionInfo.VncEndpoint;
                container.HostIp = result.ConnectionInfo.IpAddress;
                container.NetworkConfig = System.Text.Json.JsonSerializer.Serialize(result.ConnectionInfo);
            }

            await db.SaveChangesAsync(stoppingToken);
            _logger.LogInformation("Container {ContainerId} infrastructure ready on {Provider}, running setup scripts",
                job.ContainerId, job.ProviderCode);

            // Run post-create scripts (e.g., install git, dev tools)
            if (job.PostCreateScripts is { Count: > 0 })
            {
                var containerService = scope.ServiceProvider.GetRequiredService<IContainerService>();
                foreach (var script in job.PostCreateScripts)
                {
                    try
                    {
                        _logger.LogInformation("Running post-create script for container {ContainerId}", job.ContainerId);
                        var scriptResult = await containerService.ExecAsync(job.ContainerId, script, TimeSpan.FromMinutes(10), stoppingToken);
                        if (scriptResult.ExitCode != 0)
                            _logger.LogWarning("Post-create script exited with {ExitCode} for container {ContainerId}: {StdErr}",
                                scriptResult.ExitCode, job.ContainerId, scriptResult.StdErr);
                    }
                    catch (Exception scriptEx)
                    {
                        _logger.LogWarning(scriptEx, "Post-create script failed for container {ContainerId}", job.ContainerId);
                    }
                }
            }

            // Create non-root user inside the container
            if (job.ContainerUser != "root")
            {
                try
                {
                    var containerService = scope.ServiceProvider.GetRequiredService<IContainerService>();
                    var userSetupCmd =
                        $"id {job.ContainerUser} >/dev/null 2>&1 || " +
                        $"(command -v useradd >/dev/null 2>&1 && useradd -m -s /bin/bash {job.ContainerUser} || " +
                        $"adduser -D -s /bin/bash {job.ContainerUser}) && " +
                        // Grant sudo
                        $"(command -v apt-get >/dev/null 2>&1 && apt-get install -y -qq sudo >/dev/null 2>&1 || " +
                        $"command -v apk >/dev/null 2>&1 && apk add --no-cache sudo >/dev/null 2>&1 || true) && " +
                        $"echo '{job.ContainerUser} ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/{job.ContainerUser} && " +
                        $"chmod 0440 /etc/sudoers.d/{job.ContainerUser}";

                    var userResult = await containerService.ExecAsync(job.ContainerId, userSetupCmd, TimeSpan.FromMinutes(2), stoppingToken);
                    if (userResult.ExitCode != 0)
                        _logger.LogWarning("User creation exited with {ExitCode} for container {ContainerId}: {StdErr}",
                            userResult.ExitCode, job.ContainerId, userResult.StdErr);
                    else
                        _logger.LogInformation("Created user {User} in container {ContainerId}", job.ContainerUser, job.ContainerId);

                    // Configure git user
                    if (!string.IsNullOrEmpty(job.OwnerEmail) || !string.IsNullOrEmpty(job.OwnerPreferredUsername))
                    {
                        var gitConfigCmd = $"su - {job.ContainerUser} -c '";
                        if (!string.IsNullOrEmpty(job.OwnerPreferredUsername))
                            gitConfigCmd += $"git config --global user.name \"{job.OwnerPreferredUsername.Replace("\"", "\\\"")}\" && ";
                        else if (!string.IsNullOrEmpty(job.OwnerEmail))
                            gitConfigCmd += $"git config --global user.name \"{job.OwnerEmail.Split('@')[0]}\" && ";
                        if (!string.IsNullOrEmpty(job.OwnerEmail))
                            gitConfigCmd += $"git config --global user.email \"{job.OwnerEmail}\"";
                        else
                            gitConfigCmd = gitConfigCmd.TrimEnd('&', ' ');
                        gitConfigCmd += "'";
                        await containerService.ExecAsync(job.ContainerId, gitConfigCmd, stoppingToken);
                    }
                }
                catch (Exception userEx)
                {
                    _logger.LogWarning(userEx, "Failed to create user {User} in container {ContainerId}",
                        job.ContainerUser, job.ContainerId);
                }
            }

            // #1046. Materialise the user's git credentials inside the
            // container so user-initiated `git clone` (terminal,
            // code-server, agent runs) authenticate without needing the
            // user to re-enter their PAT. Best-effort: a failure here
            // does NOT fail the container — the initial template clone
            // (which uses the credentials directly via embedded URL)
            // already succeeded by this point, so the worst case is
            // that subsequent manual clones fall back to the pre-#1046
            // behaviour and prompt for auth.
            if (!string.IsNullOrEmpty(job.OwnerId))
            {
                try
                {
                    var credentialService = scope.ServiceProvider.GetRequiredService<IGitCredentialService>();
                    var credentials = await credentialService.ListWithDecryptedTokensAsync(job.OwnerId, stoppingToken);
                    var injectionScript = GitCredentialInjector.BuildInjectionScript(job.ContainerUser, credentials);
                    if (injectionScript is not null)
                    {
                        var containerService = scope.ServiceProvider.GetRequiredService<IContainerService>();
                        var credResult = await containerService.ExecAsync(job.ContainerId, injectionScript, TimeSpan.FromMinutes(1), stoppingToken);
                        if (credResult.ExitCode != 0)
                        {
                            _logger.LogWarning(
                                "Git credential injection exited with {ExitCode} for container {ContainerId}: {StdErr}",
                                credResult.ExitCode, job.ContainerId, credResult.StdErr);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "Materialised {Count} git credential(s) into container {ContainerId} as user {User}",
                                credentials.Count, job.ContainerId, job.ContainerUser);
                        }
                    }
                }
                catch (Exception credEx)
                {
                    _logger.LogWarning(credEx,
                        "Failed to materialise git credentials into container {ContainerId} — manual `git clone` from inside the container will fall back to interactive auth.",
                        job.ContainerId);
                }
            }

            // Install code assistant after post-create scripts.
            // rivoli-ai/conductor#945 (M1.5.3). The container remains
            // Running on failure (degraded but reachable so the user
            // can debug), but the outcome is captured on the row so
            // the UI surfaces a "Code assistant install failed"
            // banner instead of attaching the user to a workspace
            // that silently lacks the assistant they picked.
            if (job.CodeAssistant is not null)
            {
                var executor = scope.ServiceProvider.GetRequiredService<ICodeAssistantInstallExecutor>();
                await executor.RunAsync(container, job.CodeAssistant, stoppingToken);
            }

            // Clone git repositories after container is running
            if (job.HasGitRepositories)
            {
                try
                {
                    var gitCloneService = scope.ServiceProvider.GetRequiredService<IGitCloneService>();
                    await gitCloneService.CloneRepositoriesAsync(job.ContainerId, stoppingToken);
                }
                catch (Exception gitEx)
                {
                    // Failed clones do NOT fail the container
                    _logger.LogWarning(gitEx, "Git clone failed for container {ContainerId}, container remains Running",
                        job.ContainerId);
                }
            }

            // Generate welcome banner
            try
            {
                var containerService = scope.ServiceProvider.GetRequiredService<IContainerService>();
                var bannerScript = GenerateWelcomeBannerScript(job);
                await containerService.ExecAsync(job.ContainerId, bannerScript, TimeSpan.FromSeconds(30), stoppingToken);
                _logger.LogDebug("Welcome banner installed for container {ContainerId}", job.ContainerId);
            }
            catch (Exception bannerEx)
            {
                _logger.LogDebug(bannerEx, "Failed to install welcome banner for container {ContainerId}", job.ContainerId);
            }

            // Conductor #871: probe /etc/os-release so the UI can show
            // "Debian 12" / "Alpine 3.19" alongside the friendly name.
            // Best-effort: a probe failure leaves OsLabel null and
            // does NOT block provisioning — the banner step above
            // already passed, so the container is healthy enough to
            // surface to the user.
            try
            {
                var containerService = scope.ServiceProvider.GetRequiredService<IContainerService>();
                var probe = await containerService.ExecAsync(
                    job.ContainerId,
                    "cat /etc/os-release 2>/dev/null || true",
                    TimeSpan.FromSeconds(10),
                    stoppingToken);
                var label = OsReleaseParser.ParseLabel(probe.StdOut);
                if (!string.IsNullOrEmpty(label))
                {
                    container.OsLabel = label;
                    _logger.LogDebug("OS label probed for container {ContainerId}: {Label}",
                        job.ContainerId, label);
                }
            }
            catch (Exception osEx)
            {
                _logger.LogDebug(osEx, "Failed to probe /etc/os-release for container {ContainerId}", job.ContainerId);
            }

            // All setup complete — now mark as Running
            container.Status = ContainerStatus.Running;
            container.StartedAt = DateTime.UtcNow;
            db.Events.Add(new ContainerEvent
            {
                ContainerId = job.ContainerId,
                EventType = ContainerEventType.Started,
                SubjectId = job.OwnerId
            });
            await db.SaveChangesAsync(stoppingToken);

            // SM.2.6: emit the "running" lifecycle phase now that the
            // container is fully provisioned and ready to accept connections.
            PublishPhase(container, "running", new ContainerLifecyclePhaseData());

            sw.Stop();
            Meters.ProvisioningDuration.Record(sw.Elapsed.TotalMilliseconds);
            _logger.LogInformation("Container {ContainerId} fully provisioned on {Provider}",
                job.ContainerId, job.ProviderCode);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // SM.2.6: explicit provisioning-abort — timeout during runtime
            // acquisition. Distinct from a stoppingToken cancel (service
            // shutdown) so the client knows to surface a retryable message.
            _logger.LogError("Provisioning timed out for container {ContainerId} on {Provider}",
                job.ContainerId, job.ProviderCode);
            Meters.ProvisioningErrors.Add(1);
            activity?.SetStatus(ActivityStatusCode.Error, "Provisioning timed out after 5 minutes");
            await MarkFailedAsync(db, job.ContainerId, ProvisioningAbortReason.Timeout,
                "Provisioning timed out after 5 minutes");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // SM.2.6: service is shutting down — abort with "cancelled"
            // so the SM.0.4 helper doesn't classify this as a hard failure.
            _logger.LogWarning("Provisioning cancelled (service shutdown) for container {ContainerId}",
                job.ContainerId);
            await MarkFailedAsync(db, job.ContainerId, ProvisioningAbortReason.Cancelled,
                "Service shutdown during provisioning");
        }
        catch (Exception ex)
        {
            // SM.2.6: classify the abort reason from the exception type so
            // Conductor's SM.4 machine can render an actionable message.
            var reason = ClassifyAbortReason(ex);
            _logger.LogError(ex, "Failed to provision container {ContainerId} on provider {Provider} (reason={Reason})",
                job.ContainerId, job.ProviderCode, reason.ToWireString());
            Meters.ProvisioningErrors.Add(1);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            await MarkFailedAsync(db, job.ContainerId, reason, ex.Message);
        }
    }

    /// <summary>
    /// SM.2.6. Maps exception types to provisioning-abort reason codes.
    /// Callers receive a typed enum so the abort event's <c>reason</c>
    /// field carries the canonical wire string.
    /// </summary>
    internal static ProvisioningAbortReason ClassifyAbortReason(Exception ex)
    {
        // Docker/containerd "not found" manifest responses surface as
        // various exception types depending on the client library; we
        // heuristically match on the message text because the underlying
        // image-not-found path doesn't currently throw a typed exception.
        if (ex is InvalidOperationException &&
            (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
             ex.Message.Contains("manifest unknown", StringComparison.OrdinalIgnoreCase) ||
             ex.Message.Contains("image", StringComparison.OrdinalIgnoreCase)))
        {
            return ProvisioningAbortReason.ImageNotFound;
        }

        // QuotaExceededException — the quota check fires in
        // CreateContainerAsync before the job reaches this worker, so
        // this branch is mostly a belt-and-suspenders guard, but included
        // for completeness.
        if (ex is QuotaExceededException)
        {
            return ProvisioningAbortReason.QuotaDenied;
        }

        // Provider / engine unreachable (connection refused, timeout from
        // the Docker daemon, Apple Containers not running, etc.).
        if (ex is HttpRequestException ||
            ex is TimeoutException ||
            (ex.Message.Contains("connect", StringComparison.OrdinalIgnoreCase) &&
             ex.Message.Contains("refused", StringComparison.OrdinalIgnoreCase)))
        {
            return ProvisioningAbortReason.EngineUnavailable;
        }

        return ProvisioningAbortReason.Unknown;
    }

    private async Task MarkFailedAsync(
        ContainersDbContext db,
        Guid containerId,
        ProvisioningAbortReason reason,
        string detail)
    {
        try
        {
            var container = await db.Containers.FindAsync(containerId);
            if (container is not null)
            {
                container.Status = ContainerStatus.Failed;
                db.Events.Add(new ContainerEvent
                {
                    ContainerId = containerId,
                    EventType = ContainerEventType.Failed,
                    Details = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        error = detail,
                        reason = reason.ToWireString()
                    })
                });
                // Emit andy.containers.events.run.<id>.failed — provisioning failure.
                db.AppendRunEvent(container, RunEventKind.Failed, exitCode: null, durationSeconds: null);

                // SM.2.6: emit the discrete containerProvisioningAborted outbox
                // event. This lets downstream services (andy-tasks, andy-issues)
                // react without subscribing to the SSE stream.
                var correlationId = container.StoryId ?? container.Id;
                db.AppendProvisioningAbortedEvent(container, reason, detail, correlationId);

                await db.SaveChangesAsync();

                // SM.2.6: emit the lifecycle phase=failed SSE event with the
                // reason so SM.4 can transition the ContainerLifecycle machine
                // to a recoverable "provisioning failed — retry" state.
                PublishPhase(container, "failed",
                    new ContainerLifecyclePhaseData(Reason: reason.ToWireString()));
            }
        }
        catch (Exception ex)
        {
            // Last resort — can't even save the failure. The recovery logic will catch this on next startup.
            System.Diagnostics.Debug.WriteLine($"Failed to save failure status for container {containerId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Publishes one lifecycle phase transition to the in-process
    /// <see cref="IContainerLifecycleBus"/>. Non-throwing: a publish
    /// failure never propagates back to the provisioning flow.
    /// </summary>
    private void PublishPhase(Container container, string phase, ContainerLifecyclePhaseData phaseData)
    {
        try
        {
            var correlationId = container.StoryId ?? container.Id;
            _lifecycleBus.Publish(new ContainerLifecycleEvent(
                ContainerId: container.Id,
                Phase: phase,
                PhaseData: phaseData,
                CorrelationId: correlationId,
                Timestamp: DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to publish lifecycle phase '{Phase}' for container {ContainerId}",
                phase, container.Id);
        }
    }

    private async Task RecoverStuckContainersAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();

            var stuckContainers = db.Containers
                .Where(c => c.Status == ContainerStatus.Creating || c.Status == ContainerStatus.Pending)
                .Where(c => c.CreatedAt < DateTime.UtcNow.AddMinutes(-30))
                .ToList();

            foreach (var container in stuckContainers)
            {
                _logger.LogWarning("Recovering stuck container {ContainerId} (status={Status}, created={CreatedAt})",
                    container.Id, container.Status, container.CreatedAt);
                const string recoveryDetail = "Recovered from stuck state on worker restart";
                container.Status = ContainerStatus.Failed;
                db.Events.Add(new ContainerEvent
                {
                    ContainerId = container.Id,
                    EventType = ContainerEventType.Failed,
                    Details = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        error = recoveryDetail,
                        reason = ProvisioningAbortReason.Unknown.ToWireString()
                    })
                });
                db.AppendRunEvent(container, RunEventKind.Failed, exitCode: null, durationSeconds: null);
                // SM.2.6: emit provisioning-aborted outbox event so downstream
                // consumers can act on the recovery.
                var correlationId = container.StoryId ?? container.Id;
                db.AppendProvisioningAbortedEvent(container, ProvisioningAbortReason.Unknown,
                    recoveryDetail, correlationId);
                // SM.2.6: emit lifecycle phase so SSE subscribers see the abort.
                PublishPhase(container, "failed",
                    new ContainerLifecyclePhaseData(Reason: ProvisioningAbortReason.Unknown.ToWireString()));
            }

            if (stuckContainers.Count > 0)
            {
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("Recovered {Count} stuck container(s)", stuckContainers.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover stuck containers on startup");
        }
    }

    private static string GenerateWelcomeBannerScript(ContainerProvisionJob job)
    {
        // Build the banner content with tool introspection
        // The script detects installed tools at runtime and writes the banner
        var codeAssistantLine = job.CodeAssistant is not null
            ? $"CODE_ASSISTANT=\\\"{job.CodeAssistant.Tool}\\\""
            : "CODE_ASSISTANT=\\\"\\\"";
        var modelLine = job.CodeAssistant?.ModelName is not null
            ? $"MODEL=\\\"{job.CodeAssistant.ModelName}\\\""
            : "MODEL=\\\"\\\"";

        var containerName = EscapeForShell(job.ContainerName);
        var templateName = EscapeForShell(job.TemplateName ?? job.TemplateBaseImage);
        var providerName = EscapeForShell(job.ProviderName ?? job.ProviderCode);
        var containerUser = EscapeForShell(job.ContainerUser);
        var caTool = job.CodeAssistant is not null ? EscapeForShell(job.CodeAssistant.Tool.ToString()) : "";
        var caModel = job.CodeAssistant?.ModelName is not null ? EscapeForShell(job.CodeAssistant.ModelName) : "";

        // Build fastfetch custom config lines
        var caLine = !string.IsNullOrEmpty(caTool)
            ? (!string.IsNullOrEmpty(caModel) ? $"{caTool} ({caModel})" : caTool)
            : "";

        var script = $@"
# Install fastfetch (lightweight neofetch replacement)
command -v fastfetch >/dev/null 2>&1 || {{
    command -v apk >/dev/null 2>&1 && apk add --no-cache fastfetch >/dev/null 2>&1
    command -v apt-get >/dev/null 2>&1 && apt-get install -y -qq fastfetch >/dev/null 2>&1
}} || true

# Note: dtach (terminal session persistence) is NOT installed here.
# An earlier version of this script tried `apt-get install dtach`,
# but on containers whose apt cache hasn't been refreshed the install
# hangs against the 30-second exec timeout — leaving the container
# stuck in Creating for ~30s and breaking new-container UX.
# Containers can install dtach manually with `apt-get update &&
# apt-get install -y dtach` and the bash session will be wrapped in
# dtach on the next reattach. Tracking proper install-at-image-build
# under conductor #842.

# Create custom fastfetch config for Andy Containers
mkdir -p /etc/fastfetch
cat > /etc/fastfetch/config.jsonc << 'FFCONF'
{{
    ""$schema"": ""https://github.com/fastfetch-cli/fastfetch/raw/dev/doc/json_schema.json"",
    ""logo"": {{ ""type"": ""small"" }},
    ""display"": {{ ""separator"": ""  "" }},
    ""modules"": [
        {{ ""type"": ""title"", ""format"": ""Andy Containers"" }},
        ""separator"",
        {{ ""type"": ""custom"", ""format"": ""Container:  {containerName}"" }},
        {{ ""type"": ""custom"", ""format"": ""Template:   {templateName}"" }},
        {{ ""type"": ""custom"", ""format"": ""Provider:   {providerName}"" }},
        {{ ""type"": ""custom"", ""format"": ""User:       {containerUser}"" }},
        ""separator"",
        ""os"",
        ""kernel"",
        ""uptime"",
        ""packages"",
        ""shell"",
        ""cpu"",
        ""memory"",
        ""disk"",
        ""localip"",{(string.IsNullOrEmpty(caLine) ? "" : $@"
        ""separator"",
        {{ ""type"": ""custom"", ""format"": ""Code Asst:  {caLine}"" }},")}
        ""break"",
        ""colors""
    ]
}}
FFCONF

# Create banner wrapper
cat > /usr/local/bin/andy-banner << 'BANNEREOF'
#!/bin/sh
[ ""$ANDY_NO_BANNER"" = ""1"" ] && exit 0
if command -v fastfetch >/dev/null 2>&1; then
    fastfetch --config /etc/fastfetch/config.jsonc 2>/dev/null
else
    printf '\n  Andy Containers - {containerName}\n  Template: {templateName}\n  Provider: {providerName}\n\n'
fi
BANNEREOF
chmod +x /usr/local/bin/andy-banner

# Banner is triggered by the terminal controller after tmux attaches
";
        return script;
    }

    private static string EscapeForShell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("'", "'\\''");
    }
}
