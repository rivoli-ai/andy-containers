using System.Collections.Concurrent;
using Andy.Containers.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

/// <summary>
/// AX.16 (rivoli-ai/conductor#2104). Detaches the headless andy-cli
/// execution from the HTTP request that created the run. The dispatcher
/// calls <see cref="Launch"/> and returns immediately; the run is driven
/// to its terminal state on a background task in a fresh DI scope, and
/// callers observe completion through the run events the runner already
/// publishes over NATS (plus <c>GET /api/runs/{id}</c> polling).
/// </summary>
/// <remarks>
/// Why a fresh scope: <see cref="IHeadlessRunner"/> and
/// <see cref="ContainersDbContext"/> are scoped to the HTTP request that
/// would otherwise own them — by the time a multi-minute agent run
/// finishes, that request (and its DbContext) is long gone. The launcher
/// re-loads the <see cref="Andy.Containers.Models.Run"/> row inside its own
/// scope; the create-time-only <c>[NotMapped]</c> fields (Objective,
/// PolicyInstructions, AllowedTools) are not needed here because the
/// configurator already baked them into the headless config file.
/// Cancellation: the per-run cancel endpoint signals through
/// <see cref="IRunCancellationRegistry"/>, which is scope-independent;
/// the launcher's own token only fires on host shutdown.
/// </remarks>
public interface IHeadlessRunLauncher
{
    /// <summary>
    /// Start the headless run in the background. Returns the tracking task
    /// (callers in production discard it; tests await it).
    /// </summary>
    Task Launch(Guid runId, string configPath);

    /// <summary>The in-flight background task for a run, if any.</summary>
    Task? GetInFlight(Guid runId);
}

public sealed class HeadlessRunLauncher : IHeadlessRunLauncher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<HeadlessRunLauncher> _logger;
    private readonly ConcurrentDictionary<Guid, Task> _inFlight = new();

    public HeadlessRunLauncher(
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime lifetime,
        ILogger<HeadlessRunLauncher> logger)
    {
        _scopeFactory = scopeFactory;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task Launch(Guid runId, string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        var task = Task.Run(() => RunDetachedAsync(runId, configPath));
        _inFlight[runId] = task;
        _ = task.ContinueWith(
            _ => _inFlight.TryRemove(runId, out Task? _),
            TaskScheduler.Default);
        return task;
    }

    public Task? GetInFlight(Guid runId)
        => _inFlight.TryGetValue(runId, out var task) ? task : null;

    private async Task RunDetachedAsync(Guid runId, string configPath)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();
            var runner = scope.ServiceProvider.GetRequiredService<IHeadlessRunner>();

            var run = await db.Runs.FirstOrDefaultAsync(
                r => r.Id == runId, _lifetime.ApplicationStopping);
            if (run is null)
            {
                _logger.LogError(
                    "HeadlessRunLauncher: run {RunId} vanished between dispatch and background start; nothing to execute.",
                    runId);
                return;
            }

            var outcome = await runner.StartAsync(run, configPath, _lifetime.ApplicationStopping);
            _logger.LogInformation(
                "HeadlessRunLauncher: run {RunId} completed in background (kind={Kind}, status={Status}).",
                runId, outcome.Kind, outcome.Status);
        }
        catch (OperationCanceledException) when (_lifetime.ApplicationStopping.IsCancellationRequested)
        {
            _logger.LogWarning(
                "HeadlessRunLauncher: run {RunId} interrupted by host shutdown; the run row keeps its last persisted status.",
                runId);
        }
        catch (Exception ex)
        {
            // The runner owns terminal-event writes; if it threw before
            // reaching them the row stays mid-flight — same posture as the
            // old synchronous path, but the failure must be logged HERE
            // because no HTTP caller is waiting to observe it.
            _logger.LogError(ex,
                "HeadlessRunLauncher: run {RunId} background execution threw: {Message}",
                runId, ex.Message);
        }
    }
}
