using Andy.Containers.Abstractions;
using Andy.Containers.Api.Telemetry;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

public class ProviderHealthCheckWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IInfrastructureProviderFactory _providerFactory;
    private readonly ILogger<ProviderHealthCheckWorker> _logger;
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Delay before the first health check after startup, giving services time to initialize.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Timeout for individual provider health checks.
    /// </summary>
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(30);

    public ProviderHealthCheckWorker(
        IServiceScopeFactory scopeFactory,
        IInfrastructureProviderFactory providerFactory,
        ILogger<ProviderHealthCheckWorker> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _providerFactory = providerFactory;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Provider health check worker started");

        var intervalSeconds = _configuration.GetValue("HealthCheck:IntervalSeconds", 60);
        var interval = TimeSpan.FromSeconds(intervalSeconds);

        // Brief delay on startup to let services initialize
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Run initial health check immediately after startup delay
        await CheckAllProvidersAsync(stoppingToken);

        // Then check on a recurring interval
        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await CheckAllProvidersAsync(stoppingToken);
        }

        _logger.LogInformation("Provider health check worker stopped");
    }

    internal async Task CheckAllProvidersAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();

            var providers = await db.Providers
                .Where(p => p.IsEnabled)
                .ToListAsync(ct);

            if (providers.Count == 0)
            {
                _logger.LogDebug("No enabled providers to health-check");
                return;
            }

            _logger.LogDebug("Checking health of {Count} provider(s)", providers.Count);

            foreach (var provider in providers)
            {
                if (ct.IsCancellationRequested) break;
                await CheckProviderAsync(db, provider, ct);
            }

            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown requested, ignore
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error during provider health check cycle");
        }
    }

    private async Task CheckProviderAsync(ContainersDbContext db, InfrastructureProvider provider, CancellationToken ct)
    {
        var previousStatus = provider.HealthStatus;

        try
        {
            var infra = _providerFactory.GetProvider(provider);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(HealthCheckTimeout);

            var health = await infra.HealthCheckAsync(timeoutCts.Token);
            provider.HealthStatus = health;
            provider.LastHealthCheck = DateTime.UtcNow;

            Meters.HealthChecksCompleted.Add(1);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout on this specific provider
            _logger.LogWarning("Health check timed out for provider {ProviderCode}", provider.Code);
            provider.HealthStatus = ProviderHealth.Unreachable;
            provider.LastHealthCheck = DateTime.UtcNow;
            Meters.HealthCheckErrors.Add(1);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Re-throw if app is shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health check failed for provider {ProviderCode}", provider.Code);
            provider.HealthStatus = ProviderHealth.Unreachable;
            provider.LastHealthCheck = DateTime.UtcNow;
            Meters.HealthCheckErrors.Add(1);
        }

        // Log status transitions
        if (previousStatus != provider.HealthStatus)
        {
            _logger.LogInformation(
                "Provider {ProviderCode} health changed: {OldStatus} -> {NewStatus}",
                provider.Code, previousStatus, provider.HealthStatus);
        }
    }
}
