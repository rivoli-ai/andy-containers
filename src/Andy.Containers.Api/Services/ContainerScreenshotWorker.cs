using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Andy.Containers.Api.Services;

public class ContainerScreenshotWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IInfrastructureProviderFactory _providerFactory;
    private readonly ILogger<ContainerScreenshotWorker> _logger;
    private readonly IConfiguration _configuration;

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ExecTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DelayBetweenContainers = TimeSpan.FromMilliseconds(500);
    private const int MaxContainersPerCycle = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ContainerScreenshotWorker(
        IServiceScopeFactory scopeFactory,
        IInfrastructureProviderFactory providerFactory,
        ILogger<ContainerScreenshotWorker> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _providerFactory = providerFactory;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Container screenshot worker started");

        var intervalSeconds = _configuration.GetValue("Screenshot:IntervalSeconds", 30);
        var interval = TimeSpan.FromSeconds(intervalSeconds);

        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        await CaptureAllScreenshotsAsync(stoppingToken);

        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

            await CaptureAllScreenshotsAsync(stoppingToken);
        }

        _logger.LogInformation("Container screenshot worker stopped");
    }

    internal async Task CaptureAllScreenshotsAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();

            var containers = await db.Containers
                .Include(c => c.Provider)
                .Where(c => c.ExternalId != null && c.Status == ContainerStatus.Running)
                .Take(MaxContainersPerCycle)
                .ToListAsync(ct);

            if (containers.Count == 0) return;

            _logger.LogDebug("Capturing screenshots for {Count} container(s)", containers.Count);

            foreach (var container in containers)
            {
                if (ct.IsCancellationRequested) break;
                if (container.Provider is null || container.ExternalId is null) continue;

                try
                {
                    var infra = _providerFactory.GetProvider(container.Provider);
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(ExecTimeout);

                    // Check if VNC template
                    var template = container.TemplateId != Guid.Empty
                        ? await db.Templates.FindAsync([container.TemplateId], ct)
                        : null;
                    var isVnc = template?.GuiType == "vnc";
                    var containerUser = container.ContainerUser ?? "root";
                    var captureCmd = isVnc
                        ? "echo '[VNC Desktop - connect via noVNC on port 6080]'"
                        : containerUser != "root"
                            ? $"su - {containerUser} -c 'tmux capture-pane -p -t web -S -40 2>/dev/null' 2>/dev/null || echo '[No active terminal session]'"
                            : "tmux capture-pane -p -t web -S -40 2>/dev/null || echo '[No active terminal session]'";

                    var result = await infra.ExecAsync(
                        container.ExternalId,
                        captureCmd,
                        timeoutCts.Token);

                    var ansiText = result.StdOut;
                    if (string.IsNullOrWhiteSpace(ansiText)) continue;

                    // Parse existing metadata or create new
                    ContainerMetadata metadata;
                    if (!string.IsNullOrEmpty(container.Metadata))
                    {
                        try
                        {
                            metadata = JsonSerializer.Deserialize<ContainerMetadata>(container.Metadata, JsonOptions)
                                       ?? new ContainerMetadata();
                        }
                        catch
                        {
                            metadata = new ContainerMetadata();
                        }
                    }
                    else
                    {
                        metadata = new ContainerMetadata();
                    }

                    metadata.Screenshot = new ContainerScreenshot
                    {
                        AnsiText = ansiText,
                        CapturedAt = DateTime.UtcNow,
                        Cols = 120,
                        Rows = 40
                    };

                    container.Metadata = JsonSerializer.Serialize(metadata, JsonOptions);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogDebug("Screenshot capture timed out for container {Name}", container.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to capture screenshot for container {Name} ({Id})",
                        container.Name, container.Id);
                }

                // Brief delay between containers to avoid overloading the provider
                try { await Task.Delay(DelayBetweenContainers, ct); }
                catch (OperationCanceledException) { break; }
            }

            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error during container screenshot capture cycle");
        }
    }
}
