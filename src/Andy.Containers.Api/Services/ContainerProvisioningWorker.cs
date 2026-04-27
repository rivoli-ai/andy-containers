using System.Diagnostics;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Telemetry;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Messaging;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

public class ContainerProvisioningWorker : BackgroundService
{
    private readonly ContainerProvisioningQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IInfrastructureProviderFactory _providerFactory;
    private readonly ILogger<ContainerProvisioningWorker> _logger;

    private static readonly TimeSpan ProvisionTimeout = TimeSpan.FromMinutes(5);

    public ContainerProvisioningWorker(
        ContainerProvisioningQueue queue,
        IServiceScopeFactory scopeFactory,
        IInfrastructureProviderFactory providerFactory,
        ILogger<ContainerProvisioningWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _providerFactory = providerFactory;
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
        activity?.SetTag("containerId", job.ContainerId.ToString());
        activity?.SetTag("provider", job.ProviderCode);
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

        // Set status to Creating
        container.Status = ContainerStatus.Creating;
        await db.SaveChangesAsync(stoppingToken);

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

            // Install code assistant after post-create scripts
            if (job.CodeAssistant is not null)
            {
                try
                {
                    var installService = scope.ServiceProvider.GetRequiredService<ICodeAssistantInstallService>();
                    var installScript = installService.GenerateInstallScript(job.CodeAssistant);
                    var containerService = scope.ServiceProvider.GetRequiredService<IContainerService>();

                    _logger.LogInformation("Installing code assistant {Tool} for container {ContainerId}",
                        job.CodeAssistant.Tool, job.ContainerId);
                    var installResult = await containerService.ExecAsync(job.ContainerId, installScript, TimeSpan.FromMinutes(10), stoppingToken);
                    if (installResult.ExitCode != 0)
                        _logger.LogWarning("Code assistant install exited with {ExitCode} for container {ContainerId}: {StdErr}",
                            installResult.ExitCode, job.ContainerId, installResult.StdErr);
                    else
                        _logger.LogInformation("Code assistant {Tool} installed for container {ContainerId}",
                            job.CodeAssistant.Tool, job.ContainerId);
                }
                catch (Exception assistantEx)
                {
                    // Failed install does NOT fail the container
                    _logger.LogWarning(assistantEx, "Code assistant install failed for container {ContainerId}, container remains Running",
                        job.ContainerId);
                }
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

            sw.Stop();
            Meters.ProvisioningDuration.Record(sw.Elapsed.TotalMilliseconds);
            _logger.LogInformation("Container {ContainerId} fully provisioned on {Provider}",
                job.ContainerId, job.ProviderCode);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError("Provisioning timed out for container {ContainerId} on {Provider}",
                job.ContainerId, job.ProviderCode);
            Meters.ProvisioningErrors.Add(1);
            activity?.SetStatus(ActivityStatusCode.Error, "Provisioning timed out after 5 minutes");
            await MarkFailedAsync(db, job.ContainerId, "Provisioning timed out after 5 minutes");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision container {ContainerId} on provider {Provider}",
                job.ContainerId, job.ProviderCode);
            Meters.ProvisioningErrors.Add(1);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            await MarkFailedAsync(db, job.ContainerId, ex.Message);
        }
    }

    private static async Task MarkFailedAsync(ContainersDbContext db, Guid containerId, string errorMessage)
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
                    Details = System.Text.Json.JsonSerializer.Serialize(new { error = errorMessage })
                });
                // Emit andy.containers.events.run.<id>.failed — provisioning failure.
                db.AppendRunEvent(container, RunEventKind.Failed, exitCode: null, durationSeconds: null);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            // Last resort — can't even save the failure. The recovery logic will catch this on next startup.
            System.Diagnostics.Debug.WriteLine($"Failed to save failure status for container {containerId}: {ex.Message}");
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
                container.Status = ContainerStatus.Failed;
                db.Events.Add(new ContainerEvent
                {
                    ContainerId = container.Id,
                    EventType = ContainerEventType.Failed,
                    Details = System.Text.Json.JsonSerializer.Serialize(new { error = "Recovered from stuck state on worker restart" })
                });
                db.AppendRunEvent(container, RunEventKind.Failed, exitCode: null, durationSeconds: null);
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
