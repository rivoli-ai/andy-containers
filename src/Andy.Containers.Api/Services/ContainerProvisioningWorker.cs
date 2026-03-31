using System.Diagnostics;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Telemetry;
using Andy.Containers.Infrastructure.Data;
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
                PortMappings = portMappings
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

            // Inject environment variables (including API keys) into the container
            if (job.EnvironmentVariables is { Count: > 0 })
            {
                try
                {
                    var containerService = scope.ServiceProvider.GetRequiredService<IContainerService>();
                    // Set env vars via export commands — values are not logged
                    var exportCommands = string.Join(" && ",
                        job.EnvironmentVariables.Select(kv => $"export {kv.Key}='{kv.Value.Replace("'", "'\\''")}'"));
                    // Write to /etc/environment, .bashrc, and .profile for persistence across all session types
                    var persistCmd = string.Join(" && ",
                        job.EnvironmentVariables.Select(kv =>
                        {
                            var escaped = kv.Value.Replace("'", "'\\''");
                            return $"echo '{kv.Key}={escaped}' >> /etc/environment && " +
                                   $"echo 'export {kv.Key}=\"{escaped}\"' >> /root/.bashrc && " +
                                   $"echo 'export {kv.Key}=\"{escaped}\"' >> /root/.profile && " +
                                   $"echo 'export {kv.Key}=\"{escaped}\"' >> /etc/profile";
                        }));
                    await containerService.ExecAsync(job.ContainerId, $"{exportCommands} && {persistCmd}", stoppingToken);
                    _logger.LogInformation("Injected {Count} environment variable(s) into container {ContainerId}",
                        job.EnvironmentVariables.Count, job.ContainerId);
                }
                catch (Exception envEx)
                {
                    _logger.LogWarning(envEx, "Failed to inject environment variables into container {ContainerId}",
                        job.ContainerId);
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
}
