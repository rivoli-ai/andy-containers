using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Providers.Local;
using Andy.Containers.Models;
using Andy.Containers.Validation;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Services;

/// <summary>
/// rivoli-ai/andy-tasks#390. Startup warmer for the pre-baked agent image
/// <see cref="LocalImages.AgentCli"/> (<c>andy-agent-cli:latest</c>).
///
/// The Docker provider builds that image lazily on first use
/// (<see cref="DockerInfrastructureProvider.EnsureLocalImageAsync"/>), but a
/// lazy build means the FIRST workspace container still pays the full
/// clone + <c>dotnet publish</c> cost (~5 minutes) inside the image build —
/// the exact delay this fix exists to remove. This worker fires once at
/// service startup and triggers the build in the background, so by the time
/// the first plan execution provisions a container the image is already
/// tagged and provisioning completes in seconds.
///
/// Strictly best-effort: any failure (Docker not running, no network, no
/// Docker provider seeded) is logged and swallowed — provisioning falls back
/// to the lazy build (Docker provider) or the post_create source-build
/// (non-Docker providers), both of which behave exactly as before #390.
/// Disable via <c>Containers:AgentCliImage:WarmOnStartup=false</c>.
/// </summary>
public sealed class AgentCliImageWarmer : BackgroundService
{
    internal const string WarmOnStartupKey = "Containers:AgentCliImage:WarmOnStartup";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IInfrastructureProviderFactory _providerFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AgentCliImageWarmer> _logger;

    public AgentCliImageWarmer(
        IServiceScopeFactory scopeFactory,
        IInfrastructureProviderFactory providerFactory,
        IConfiguration configuration,
        ILogger<AgentCliImageWarmer> logger)
    {
        _scopeFactory = scopeFactory;
        _providerFactory = providerFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!(_configuration.GetValue<bool?>(WarmOnStartupKey) ?? true))
        {
            _logger.LogDebug("Agent-cli image warm-on-startup disabled via {Key}.", WarmOnStartupKey);
            return;
        }

        try
        {
            // Resolve the seeded Docker provider (only Docker can build the
            // local image). No enabled Docker provider row → nothing to warm.
            InfrastructureProvider? providerEntity;
            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();
                providerEntity = await db.Providers
                    .AsNoTracking()
                    .Where(p => p.Type == ProviderType.Docker && p.IsEnabled)
                    .OrderBy(p => p.Code)
                    .FirstOrDefaultAsync(stoppingToken);
            }

            if (providerEntity is null)
            {
                _logger.LogDebug("No enabled Docker provider seeded; skipping agent-cli image warm-up.");
                return;
            }

            if (_providerFactory.GetProvider(providerEntity) is not DockerInfrastructureProvider docker)
            {
                return;
            }

            _logger.LogInformation(
                "Warming pre-baked agent image {Image} (builds from images/agent-cli/Dockerfile when missing)…",
                LocalImages.AgentCli);

            var started = System.Diagnostics.Stopwatch.StartNew();
            await docker.EnsureLocalImageAsync(LocalImages.AgentCli, stoppingToken);
            started.Stop();

            _logger.LogInformation(
                "Pre-baked agent image {Image} is ready ({Elapsed:F1}s).",
                LocalImages.AgentCli, started.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // service shutdown — nothing to log
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Agent-cli image warm-up failed; the Docker provider will lazily build {Image} on first use " +
                "(or the container's post_create script source-builds andy-cli).",
                LocalImages.AgentCli);
        }
    }
}
