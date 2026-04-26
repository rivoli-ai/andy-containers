using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

/// <summary>
/// Startup-time reconciler that detects DB rows whose underlying
/// container was removed out-of-band (host reboot, manual
/// <c>docker rm -f</c>) and marks them <see cref="ContainerStatus.Destroyed"/>.
///
/// Without this, the periodic <see cref="ContainerStatusSyncWorker"/>
/// eventually catches orphans, but only after a 10 s startup delay
/// plus ~15 s polling interval — up to 25 s of zombies showing as
/// running in Conductor's UI. Running once at startup before the
/// polling worker fixes the cold-start window. Conductor #840.
///
/// Per-provider behaviour:
///   * Local providers (Docker, Apple Containers) implement
///     <see cref="IInfrastructureProvider.ListExternalIdsAsync"/> as a
///     single CLI call — fast, bulk.
///   * Cloud providers return <c>null</c> from
///     <see cref="IInfrastructureProvider.ListExternalIdsAsync"/>;
///     we skip them and let the periodic worker handle them.
/// </summary>
public class ContainerExternalIdReconciler : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ContainerExternalIdReconciler> _logger;

    public ContainerExternalIdReconciler(
        IServiceScopeFactory scopeFactory,
        ILogger<ContainerExternalIdReconciler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Fire-and-forget the reconcile so we don't block app startup.
        // The periodic worker covers ongoing drift; this just closes
        // the cold-start window.
        _ = Task.Run(() => ReconcileAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Internal for unit tests. Reconciles every Running row against
    /// its provider's bulk externalId listing; flips orphans to
    /// <see cref="ContainerStatus.Destroyed"/>.
    /// </summary>
    internal async Task ReconcileAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();
            var providerFactory = scope.ServiceProvider
                .GetRequiredService<IInfrastructureProviderFactory>();

            var rows = await db.Containers
                .Include(c => c.Provider)
                .Where(c => c.Status == ContainerStatus.Running &&
                            c.ExternalId != null && c.ExternalId != "")
                .ToListAsync(ct);

            if (rows.Count == 0)
            {
                _logger.LogDebug(
                    "[CONTAINERS-RECONCILE] No running rows to reconcile at startup");
                return;
            }

            // Group by provider entity so we issue one bulk listing per
            // distinct provider, not per row.
            var byProvider = rows
                .Where(r => r.Provider is not null)
                .GroupBy(r => r.ProviderId);

            var orphanCount = 0;

            foreach (var group in byProvider)
            {
                var providerEntity = group.First().Provider!;
                IInfrastructureProvider provider;
                try
                {
                    provider = providerFactory.GetProvider(providerEntity);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[CONTAINERS-RECONCILE] Failed to resolve provider {Provider}; skipping {Count} rows",
                        providerEntity.Name, group.Count());
                    continue;
                }

                HashSet<string>? liveIds;
                try
                {
                    liveIds = await provider.ListExternalIdsAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[CONTAINERS-RECONCILE] Provider {Provider} ListExternalIdsAsync threw; skipping {Count} rows",
                        providerEntity.Name, group.Count());
                    continue;
                }

                if (liveIds is null)
                {
                    // Provider doesn't support bulk listing — periodic
                    // worker will reconcile these via per-row probes.
                    _logger.LogDebug(
                        "[CONTAINERS-RECONCILE] Provider {Provider} does not support bulk enumeration",
                        providerEntity.Name);
                    continue;
                }

                foreach (var row in group)
                {
                    if (!liveIds.Contains(row.ExternalId!))
                    {
                        _logger.LogWarning(
                            "[CONTAINERS-RECONCILE] Container {Name} ({Id}) ExternalId {ExternalId} not present on {Provider} — marking Destroyed",
                            row.Name,
                            row.Id,
                            row.ExternalId,
                            providerEntity.Name);
                        row.Status = ContainerStatus.Destroyed;
                        if (row.StoppedAt is null)
                            row.StoppedAt = DateTime.UtcNow;
                        orphanCount++;
                    }
                }
            }

            if (orphanCount > 0)
            {
                await db.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "[CONTAINERS-RECONCILE] Marked {Count} orphan container(s) Destroyed at startup",
                    orphanCount);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — fine.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[CONTAINERS-RECONCILE] Startup reconciliation failed");
        }
    }
}
