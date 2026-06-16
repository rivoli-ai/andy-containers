using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Messaging;
using Andy.Containers.Messaging.Events;
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

    // rivoli-ai/conductor#2204. How many CONSECUTIVE not-found probes a
    // container must accumulate before its DB record is reconciled to
    // Failed. A single miss can be a transient docker daemon restart;
    // three misses (~45 s at the default 15 s interval) means the
    // container was genuinely deleted out-of-band (prune, reboot,
    // manual rm) and the record must leave the live polling set —
    // otherwise this worker and the screenshot worker retry the
    // NotFound forever.
    internal const int MissingContainerThreshold = 3;

    /// <summary>
    /// Machine-readable reason stamped on the ContainerEvent when a
    /// record is reconciled because its backing container vanished.
    /// </summary>
    internal const string MissingContainerReason = "docker_container_missing";

    // Per-container consecutive not-found counter. The worker is a
    // singleton hosted service, so instance state survives across
    // cycles. Entries are cleared on a successful probe, on reconcile,
    // and for ids that drop out of the polled set.
    private readonly Dictionary<Guid, int> _consecutiveNotFound = new();

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

            // Drop stale miss-counters for containers that left the
            // polled set through another path (user destroy, etc.).
            var activeIds = activeContainers.Select(c => c.Id).ToHashSet();
            foreach (var staleId in _consecutiveNotFound.Keys.Where(id => !activeIds.Contains(id)).ToList())
                _consecutiveNotFound.Remove(staleId);

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

                    // Probe succeeded — any earlier not-found streak was
                    // transient (docker daemon restart). Reset it.
                    _consecutiveNotFound.Remove(container.Id);

                    if (info.Status != container.Status)
                    {
                        // Don't override Creating → Running: the provisioning worker
                        // sets Creating while post-create scripts run and only transitions
                        // to Running after all setup completes. The provider reports Running
                        // because the container process is up, but tools aren't installed yet.
                        if (container.Status == ContainerStatus.Creating && info.Status == ContainerStatus.Running)
                            continue;

                        _logger.LogInformation(
                            "Container {Name} ({Id}) status changed: {Old} -> {New}",
                            container.Name, container.Id, container.Status, info.Status);

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
                catch (Exception ex) when (ContainerMissingDetection.IsContainerMissing(ex))
                {
                    // rivoli-ai/conductor#2204. The backing container is
                    // gone from the provider (out-of-band prune / reboot /
                    // manual rm) but the DB record is still in a live
                    // state. Tolerate a small bounded number of
                    // consecutive misses to ride out transient docker
                    // daemon restarts, then reconcile the record to
                    // Failed so it leaves the polling set — never retry
                    // NotFound forever.
                    var misses = _consecutiveNotFound.GetValueOrDefault(container.Id) + 1;
                    if (misses < MissingContainerThreshold)
                    {
                        _consecutiveNotFound[container.Id] = misses;
                        _logger.LogDebug(
                            "Container {Name} ({ExternalId}) not found on provider (consecutive miss {Misses}/{Threshold})",
                            container.Name, container.ExternalId, misses, MissingContainerThreshold);
                        continue;
                    }

                    _consecutiveNotFound.Remove(container.Id);

                    // The ONE warning for this whole episode.
                    _logger.LogWarning(
                        "[CONTAINERS-SYNC-MISSING] Container {Name} ({ExternalId}) missing on provider for {Threshold} consecutive checks — reconciling record {Id} from {Status} to Failed ({Reason})",
                        container.Name, container.ExternalId, MissingContainerThreshold,
                        container.Id, container.Status, MissingContainerReason);

                    container.Status = ContainerStatus.Failed;
                    container.StoppedAt ??= DateTime.UtcNow;
                    db.Events.Add(new ContainerEvent
                    {
                        ContainerId = container.Id,
                        EventType = ContainerEventType.Failed,
                        Details = MissingContainerReason
                    });
                    // Emit andy.containers.events.run.<id>.failed — same
                    // outbox path stop/destroy transitions use, so
                    // downstream consumers (andy-tasks, Conductor) see
                    // the terminal transition.
                    var durationSeconds = container.StartedAt.HasValue
                        ? (DateTime.UtcNow - container.StartedAt.Value).TotalSeconds
                        : (double?)null;
                    db.AppendRunEvent(container, RunEventKind.Failed,
                        exitCode: null, durationSeconds: durationSeconds);
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
