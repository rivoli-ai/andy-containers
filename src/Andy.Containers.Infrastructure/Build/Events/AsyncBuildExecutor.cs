using Andy.Containers.Abstractions.Images;
using Andy.Containers.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Infrastructure.Build.Events;

/// <summary>
/// Cache-hit-synchronous, cache-miss-async wrapper around
/// <see cref="IImageBuildOrchestrator"/>. Cache misses spawn a
/// background <see cref="Task"/> with a fresh DI scope (so the
/// orchestrator's scoped DbContext is correctly fresh per build),
/// publish progress through <see cref="IBuildEventBus"/>, and update
/// <see cref="IBuildExecutionRegistry"/> as the build transitions.
/// </summary>
/// <remarks>
/// IM9 (rivoli-ai/andy-containers#263). The background task is
/// fire-and-forget — exceptions inside the task are caught and
/// recorded as a Failed terminal state on the registry + emitted as
/// a <see cref="BuildCompletedEvent"/> on the bus. The host
/// <see cref="IHostApplicationLifetime.ApplicationStopping"/> token
/// is linked into the build's cancellation so a graceful shutdown
/// surfaces as <see cref="BuildOutcome.Cancelled"/> rather than a
/// truncated stream.
/// </remarks>
public sealed class AsyncBuildExecutor : IAsyncBuildExecutor
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IBuildEventBus _bus;
    private readonly IBuildExecutionRegistry _registry;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AsyncBuildExecutor> _logger;

    public AsyncBuildExecutor(
        IServiceScopeFactory scopes,
        IBuildEventBus bus,
        IBuildExecutionRegistry registry,
        IHostApplicationLifetime lifetime,
        ILogger<AsyncBuildExecutor> logger)
    {
        _scopes = scopes;
        _bus = bus;
        _registry = registry;
        _lifetime = lifetime;
        _logger = logger;
    }

    public async Task<AsyncBuildHandle> StartAsync(ImageBuildRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Cache-hit fast path. Resolve in the caller's scope so the
        // request's DbContext is reused — no need to spin up a new
        // scope for a read-only check.
        using (var scope = _scopes.CreateScope())
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<IImageBuildOrchestrator>();
            if (!request.Force)
            {
                var cached = await orchestrator.TryCacheHitAsync(request, ct);
                if (cached is not null)
                {
                    return new AsyncBuildHandle(cached.BuildId, AsyncBuildHandleStatus.Cached, cached);
                }
            }
        }

        // Miss: register and queue. The buildId we register is also
        // surfaced through the bus's events so SSE subscribers can
        // correlate.
        var buildId = Guid.NewGuid();
        _registry.Register(buildId, request);

        // Link the host lifetime so a graceful shutdown cancels
        // in-flight builds rather than tearing them down mid-write.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
        _ = Task.Run(() => RunBuildAsync(buildId, request, cts.Token), CancellationToken.None);

        return new AsyncBuildHandle(buildId, AsyncBuildHandleStatus.Queued, null);
    }

    private async Task RunBuildAsync(Guid buildId, ImageBuildRequest request, CancellationToken ct)
    {
        // Mark Running before doing anything so a fast-following SSE
        // attach sees the right status.
        var registered = _registry.TryGet(buildId);
        if (registered is not null)
        {
            _registry.Update(buildId, registered with
            {
                Status = BuildExecutionStatus.Running,
            });
        }

        var reporter = new BusEventReporter(_bus, buildId);

        try
        {
            using var scope = _scopes.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IImageBuildOrchestrator>();
            var result = await orchestrator.BuildAsync(request, reporter, ct);

            var terminal = registered is null
                ? new BuildExecutionState
                {
                    BuildId = buildId,
                    TemplateId = request.TemplateId,
                    Status = MapStatus(result.Status),
                    StartedAt = DateTimeOffset.UtcNow,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Digest = result.Digest,
                    References = result.References,
                    ErrorCode = result.ErrorCode,
                    ErrorMessage = result.ErrorMessage,
                    FailureLog = result.FailureLog,
                }
                : registered with
                {
                    Status = MapStatus(result.Status),
                    CompletedAt = DateTimeOffset.UtcNow,
                    Digest = result.Digest,
                    References = result.References,
                    ErrorCode = result.ErrorCode,
                    ErrorMessage = result.ErrorMessage,
                    FailureLog = result.FailureLog,
                };
            _registry.Update(buildId, terminal);

            // Publish a terminal event for SSE consumers if the
            // orchestrator hasn't already (the build backend may
            // have emitted its own complete event during run; the
            // bus's idempotency on "terminal seen" handles either
            // ordering).
            _bus.Publish(buildId, new BuildCompletedEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                Outcome = result.Status switch
                {
                    BuildResultStatus.Succeeded => BuildOutcome.Succeeded,
                    BuildResultStatus.Cached => BuildOutcome.Succeeded,
                    BuildResultStatus.Failed => BuildOutcome.Failed,
                    _ => BuildOutcome.Failed,
                },
                Digest = result.Digest,
                FailureReason = result.ErrorMessage,
            });
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Build {BuildId} cancelled via host shutdown.", buildId);
            var current = _registry.TryGet(buildId);
            if (current is not null)
            {
                _registry.Update(buildId, current with
                {
                    Status = BuildExecutionStatus.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow,
                });
            }
            _bus.Publish(buildId, new BuildCompletedEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                Outcome = BuildOutcome.Cancelled,
                FailureReason = "build cancelled",
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Build {BuildId} failed unexpectedly.", buildId);
            var current = _registry.TryGet(buildId);
            if (current is not null)
            {
                _registry.Update(buildId, current with
                {
                    Status = BuildExecutionStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorCode = "build.unexpected",
                    ErrorMessage = ex.Message,
                });
            }
            _bus.Publish(buildId, new BuildCompletedEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                Outcome = BuildOutcome.Failed,
                FailureReason = ex.Message,
            });
        }
    }

    private static BuildExecutionStatus MapStatus(BuildResultStatus status) => status switch
    {
        BuildResultStatus.Cached => BuildExecutionStatus.Cached,
        BuildResultStatus.Succeeded => BuildExecutionStatus.Succeeded,
        BuildResultStatus.Failed => BuildExecutionStatus.Failed,
        _ => BuildExecutionStatus.Failed,
    };

    private sealed class BusEventReporter : IProgress<BuildProgressEvent>
    {
        private readonly IBuildEventBus _bus;
        private readonly Guid _buildId;

        public BusEventReporter(IBuildEventBus bus, Guid buildId)
        {
            _bus = bus;
            _buildId = buildId;
        }

        public void Report(BuildProgressEvent value) => _bus.Publish(_buildId, value);
    }
}
