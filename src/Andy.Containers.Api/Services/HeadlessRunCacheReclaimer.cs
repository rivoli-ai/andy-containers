using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Services;

/// <summary>
/// Reclaims GUID-scoped <c>/tmp/andy-runs</c> directories left behind when a
/// daemon or container exec died before <see cref="HeadlessRunner"/> could run
/// its normal cleanup. A live launch writes its shell PID into
/// <c>.owner-pid</c>; old directories are removed only when that process no
/// longer exists. The age boundary also protects a launch between mkdir and
/// marker creation.
/// </summary>
public sealed class HeadlessRunCacheReclaimer : BackgroundService
{
    internal const string RetentionKey = "Containers:HeadlessRunCache:OrphanRetention";
    internal const string SweepIntervalKey = "Containers:HeadlessRunCache:SweepInterval";
    internal const string RunRoot = "/tmp/andy-runs";

    private static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(6);
    private static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ExecTimeout = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<HeadlessRunCacheReclaimer> _logger;

    public HeadlessRunCacheReclaimer(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<HeadlessRunCacheReclaimer> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retention = PositiveOrDefault(
            _configuration.GetValue<TimeSpan?>(RetentionKey), DefaultRetention);
        var interval = PositiveOrDefault(
            _configuration.GetValue<TimeSpan?>(SweepIntervalKey), DefaultSweepInterval);

        _logger.LogInformation(
            "Headless run cache reclaimer started (retention {Retention}, interval {Interval}).",
            retention, interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(retention, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Headless run cache reclamation sweep failed; the next sweep will retry.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task SweepOnceAsync(TimeSpan retention, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();
        var containers = scope.ServiceProvider.GetRequiredService<IContainerService>();
        var containerIds = await db.Containers
            .AsNoTracking()
            .Where(c => c.Status == ContainerStatus.Running || c.Status == ContainerStatus.Creating)
            .Select(c => c.Id)
            .ToListAsync(ct);
        var command = BuildReclamationCommand(RunRoot, retention);

        foreach (var containerId in containerIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await containers.ExecAsync(containerId, command, ExecTimeout, ct);
                if (result.ExitCode != 0)
                {
                    _logger.LogWarning(
                        "Headless run cache reclamation failed in container {ContainerId} (exit {Exit}): {Error}",
                        containerId, result.ExitCode, Bounded(result.StdErr));
                    continue;
                }

                var reclaimed = (result.StdOut ?? string.Empty)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Count(line => line.StartsWith("[AC-RUN-CACHE-RECLAIMED] ", StringComparison.Ordinal));
                if (reclaimed > 0)
                {
                    _logger.LogInformation(
                        "Reclaimed {Count} orphaned headless run cache directories in container {ContainerId}.",
                        reclaimed, containerId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not reclaim headless run caches in container {ContainerId}; the next sweep will retry.",
                    containerId);
            }
        }
    }

    /// <summary>
    /// Pure command seam used by tests. Only aged, UUID-shaped direct children
    /// of <paramref name="root"/> are candidates. A numeric owner PID that is
    /// still alive always wins over age and prevents deletion.
    /// </summary>
    internal static string BuildReclamationCommand(string root, TimeSpan retention)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var minutes = Math.Max(1, (int)Math.Ceiling(retention.TotalMinutes));
        var q = ShellQuote(root);
        return
            $"root={q}; [ ! -d \"$root\" ] || "
            + $"find \"$root\" -mindepth 1 -maxdepth 1 -type d -mmin +{minutes} -print | "
            + "while IFS= read -r dir; do "
            + "name=${dir##*/}; "
            + "case \"$name\" in ????????-????-????-????-????????????) ;; *) continue ;; esac; "
            + "owner=''; [ ! -f \"$dir/.owner-pid\" ] || owner=$(cat \"$dir/.owner-pid\" 2>/dev/null); "
            + "case \"$owner\" in ''|*[!0-9]*) ;; *) if kill -0 \"$owner\" 2>/dev/null; then continue; fi ;; esac; "
            + "if find \"$dir\" -depth -delete 2>/dev/null; then "
            + "echo \"[AC-RUN-CACHE-RECLAIMED] $name\"; fi; done";
    }

    private static TimeSpan PositiveOrDefault(TimeSpan? value, TimeSpan fallback) =>
        value is { } candidate && candidate > TimeSpan.Zero ? candidate : fallback;

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private static string Bounded(string? value) =>
        string.IsNullOrEmpty(value) || value.Length <= 400 ? value ?? string.Empty : value[^400..];
}
