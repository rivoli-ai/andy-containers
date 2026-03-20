using Andy.Containers.Abstractions;
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
            var spec = new ContainerSpec
            {
                ImageReference = job.TemplateBaseImage,
                Name = job.ContainerName,
                Resources = job.Resources ?? new ResourceSpec(),
                Gpu = job.Gpu,
                SshEnabled = job.SshEnabled,
                SshPort = 22
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
            container.Status = ContainerStatus.Running;
            container.StartedAt = DateTime.UtcNow;

            if (result.ConnectionInfo is not null)
            {
                container.IdeEndpoint = result.ConnectionInfo.IdeEndpoint;
                container.VncEndpoint = result.ConnectionInfo.VncEndpoint;
                container.NetworkConfig = System.Text.Json.JsonSerializer.Serialize(result.ConnectionInfo);
            }

            db.Events.Add(new ContainerEvent
            {
                ContainerId = job.ContainerId,
                EventType = ContainerEventType.Started,
                SubjectId = job.OwnerId
            });

            await db.SaveChangesAsync(stoppingToken);
            _logger.LogInformation("Container {ContainerId} provisioned successfully on {Provider}",
                job.ContainerId, job.ProviderCode);

            // Post-provisioning: SSH setup (non-fatal)
            if (job.SshEnabled)
            {
                await SetupSshAsync(scope.ServiceProvider, infra, container.ExternalId!, job, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError("Provisioning timed out for container {ContainerId} on {Provider}",
                job.ContainerId, job.ProviderCode);
            await MarkFailedAsync(db, job.ContainerId, "Provisioning timed out after 5 minutes");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision container {ContainerId} on provider {Provider}",
                job.ContainerId, job.ProviderCode);
            await MarkFailedAsync(db, job.ContainerId, ex.Message);
        }
    }

    private async Task SetupSshAsync(IServiceProvider services, IInfrastructureProvider infra, string externalId,
        ContainerProvisionJob job, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Setting up SSH for container {ContainerId}", job.ContainerId);

            var sshKeyService = services.GetRequiredService<ISshKeyService>();
            var sshProvisioning = services.GetRequiredService<ISshProvisioningService>();
            var db = services.GetRequiredService<ContainersDbContext>();

            // Fetch user's registered SSH keys
            var registeredKeys = await sshKeyService.ListKeysAsync(job.OwnerId, ct);

            // Build deduplicated key list: registered keys first, then one-time keys by fingerprint
            var seenFingerprints = new HashSet<string>();
            var allPublicKeys = new List<string>();

            foreach (var key in registeredKeys)
            {
                seenFingerprints.Add(key.Fingerprint);
                allPublicKeys.Add(key.PublicKey);
            }

            if (job.SshPublicKeys is { Length: > 0 })
            {
                foreach (var oneTimeKey in job.SshPublicKeys)
                {
                    try
                    {
                        var fp = sshKeyService.ComputeFingerprint(oneTimeKey);
                        if (seenFingerprints.Add(fp))
                            allPublicKeys.Add(oneTimeKey);
                    }
                    catch
                    {
                        // If fingerprint computation fails, include the key anyway
                        allPublicKeys.Add(oneTimeKey);
                    }
                }
            }

            // Zero-key warning
            if (allPublicKeys.Count == 0)
            {
                _logger.LogWarning("SSH enabled for container {ContainerId} but no keys to inject", job.ContainerId);
                db.Events.Add(new ContainerEvent
                {
                    ContainerId = job.ContainerId,
                    EventType = ContainerEventType.SessionOpened,
                    SubjectId = job.OwnerId,
                    Details = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        action = "ssh_provisioned",
                        warning = "SSH enabled but no keys injected",
                        keysInjected = 0
                    })
                });
                await db.SaveChangesAsync(ct);
                return;
            }

            // Parse SSH config from template or use defaults
            var sshConfig = new Models.SshConfig { Enabled = true };

            // Generate and execute the setup script
            var script = sshProvisioning.GenerateSetupScript(sshConfig, allPublicKeys);
            var result = await infra.ExecAsync(externalId, script, ct);

            if (result.ExitCode != 0)
            {
                _logger.LogWarning("SSH setup script returned exit code {ExitCode} for container {ContainerId}: {StdErr}",
                    result.ExitCode, job.ContainerId, result.StdErr);
            }

            // Update LastUsedAt on injected registered keys
            if (registeredKeys.Count > 0)
            {
                var keyIds = registeredKeys.Select(k => k.Id).ToList();
                await sshKeyService.UpdateLastUsedAsync(job.OwnerId, keyIds, ct);
            }

            db.Events.Add(new ContainerEvent
            {
                ContainerId = job.ContainerId,
                EventType = ContainerEventType.SessionOpened,
                SubjectId = job.OwnerId,
                Details = System.Text.Json.JsonSerializer.Serialize(new
                {
                    action = "ssh_provisioned",
                    keysInjected = allPublicKeys.Count,
                    exitCode = result.ExitCode
                })
            });
            await db.SaveChangesAsync(ct);

            _logger.LogInformation("SSH setup completed for container {ContainerId} ({KeyCount} keys injected)",
                job.ContainerId, allPublicKeys.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH setup failed for container {ContainerId} (non-fatal)", job.ContainerId);

            try
            {
                var db = services.GetRequiredService<ContainersDbContext>();
                db.Events.Add(new ContainerEvent
                {
                    ContainerId = job.ContainerId,
                    EventType = ContainerEventType.Failed,
                    SubjectId = job.OwnerId,
                    Details = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        action = "ssh_setup_failed",
                        error = ex.Message
                    })
                });
                await db.SaveChangesAsync(ct);
            }
            catch (Exception logEx)
            {
                _logger.LogError(logEx, "Failed to log SSH setup failure event for container {ContainerId}", job.ContainerId);
            }
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
                .Where(c => c.CreatedAt < DateTime.UtcNow.AddMinutes(-2))
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
