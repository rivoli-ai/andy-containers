namespace Andy.Containers.Api.Services;

public class ImageBuildWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ImageBuildWorker> _logger;

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    public ImageBuildWorker(IServiceScopeFactory scopeFactory, ILogger<ImageBuildWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Image build worker started");

        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        // Initial status check
        await RefreshAsync(stoppingToken);

        using var timer = new PeriodicTimer(RefreshInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

            await RefreshAsync(stoppingToken);
        }

        _logger.LogInformation("Image build worker stopped");
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var buildService = scope.ServiceProvider.GetRequiredService<ITemplateBuildService>();
            await buildService.RefreshStatusesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh image build statuses");
        }
    }
}
