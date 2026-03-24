using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

public class ContainerStatusSyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IInfrastructureProviderFactory _providerFactory;
    private readonly ILogger<ContainerStatusSyncWorker> _logger;
    private readonly IConfiguration _configuration;

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InfoTimeout = TimeSpan.FromSeconds(10);

    public ContainerStatusSyncWorker(
        IServiceScopeFactory scopeFactory,
        IInfrastructureProviderFactory providerFactory,
        ILogger<ContainerStatusSyncWorker> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _providerFactory = providerFactory;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Container status sync worker started");

        var intervalSeconds = _configuration.GetValue("ContainerSync:IntervalSeconds", 15);
        var interval = TimeSpan.FromSeconds(intervalSeconds);

        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        await SyncAllAsync(stoppingToken);

        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

            await SyncAllAsync(stoppingToken);
        }

        _logger.LogInformation("Container status sync worker stopped");
    }

    internal async Task SyncAllAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();

            var activeContainers = await db.Containers
                .Include(c => c.Provider)
                .Where(c => c.ExternalId != null &&
                    (c.Status == ContainerStatus.Running || c.Status == ContainerStatus.Stopped || c.Status == ContainerStatus.Creating))
                .ToListAsync(ct);

            if (activeContainers.Count == 0) return;

            var changed = false;
            foreach (var container in activeContainers)
            {
                if (ct.IsCancellationRequested) break;
                if (container.Provider is null || container.ExternalId is null) continue;

                try
                {
                    var infra = _providerFactory.GetProvider(container.Provider);
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(InfoTimeout);

                    var info = await infra.GetContainerInfoAsync(container.ExternalId, timeoutCts.Token);

                    if (info.Status != container.Status)
                    {
                        _logger.LogInformation(
                            "Container {Name} ({Id}) status changed: {Old} -> {New}",
                            container.Name, container.Id, container.Status, info.Status);

                        // If provider says it's gone (stopped/destroyed) but DB says running, update
                        container.Status = info.Status;
                        if (info.Status == ContainerStatus.Stopped && container.StoppedAt is null)
                            container.StoppedAt = DateTime.UtcNow;
                        changed = true;
                    }

                    if (info.IpAddress is not null && info.IpAddress != container.HostIp)
                    {
                        container.HostIp = info.IpAddress;
                        changed = true;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogDebug("Status check timed out for container {Name}", container.Name);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                                                            ex.Message.Contains("no such", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Container {Name} ({ExternalId}) no longer exists on provider, marking as Destroyed",
                        container.Name, container.ExternalId);
                    container.Status = ContainerStatus.Destroyed;
                    changed = true;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to check status for container {Name}", container.Name);
                }
            }

            if (changed)
                await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error during container status sync");
        }
    }
}
