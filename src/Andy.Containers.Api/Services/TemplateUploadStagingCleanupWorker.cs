using Andy.Containers.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Andy.Containers.Api.Services;

/// <summary>
/// #277 PR C. Periodic sweeper for the multipart-template-upload
/// staging root. Reclaims abandoned <c>&lt;stagingId&gt;</c>
/// subdirectories — those whose path is no longer referenced by any
/// <c>Template.UploadedFilesPath</c> row AND whose last-write
/// timestamp is older than the configured retention.
/// </summary>
/// <remarks>
/// <para>
/// Referenced dirs are kept indefinitely — force-rebuilds of a
/// long-lived template need its uploaded files to be there. Only
/// orphaned dirs (template was deleted, or the multipart POST
/// crashed mid-flight in a way that escaped PR A's best-effort
/// catch) are subject to the retention cutoff.
/// </para>
/// <para>
/// Modeled on <see cref="ProviderHealthCheckWorker"/>: PeriodicTimer
/// loop, IServiceScopeFactory for scoped DB access, single
/// testable <see cref="SweepOnceAsync"/> method so unit tests can
/// run the work without the timer.
/// </para>
/// </remarks>
public sealed class TemplateUploadStagingCleanupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TemplateUploadStagingCleanupOptions _options;
    private readonly ILogger<TemplateUploadStagingCleanupWorker> _logger;
    private readonly TimeProvider _clock;
    private readonly string _stagingRoot;

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    public TemplateUploadStagingCleanupWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<TemplateUploadStagingCleanupOptions> options,
        ILogger<TemplateUploadStagingCleanupWorker> logger)
        : this(scopeFactory, options.Value, logger,
               TemplateUploadStagingPaths.GetStagingRoot(), TimeProvider.System)
    {
    }

    /// <summary>
    /// Test-only constructor. Lets unit tests point the sweeper at a
    /// scratch directory + virtual clock instead of the real
    /// <c>Path.GetTempPath()</c>-rooted staging tree.
    /// </summary>
    internal TemplateUploadStagingCleanupWorker(
        IServiceScopeFactory scopeFactory,
        TemplateUploadStagingCleanupOptions options,
        ILogger<TemplateUploadStagingCleanupWorker> logger,
        string stagingRoot,
        TimeProvider clock)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _stagingRoot = stagingRoot;
        _clock = clock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "TemplateUploadStagingCleanupWorker started. Interval={Interval} Retention={Retention}",
            _options.SweepInterval, _options.Retention);

        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(_options.SweepInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Swallow per-tick exceptions so a transient FS or DB
                // error doesn't take the worker down for the lifetime
                // of the process. Next tick retries.
                _logger.LogError(ex, "TemplateUploadStagingCleanupWorker sweep failed; will retry next tick.");
            }

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("TemplateUploadStagingCleanupWorker stopped.");
    }

    /// <summary>
    /// Run one sweep. Public for direct invocation from unit tests —
    /// the timer loop is not the interesting unit; the per-tick
    /// decision (keep / delete) is. Returns the number of directories
    /// deleted so tests can assert without re-reading the filesystem.
    /// </summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_stagingRoot))
        {
            // No multipart POSTs have ever landed on this host —
            // nothing to reclaim. Common in clean dev environments.
            return 0;
        }

        // Pull the referenced paths in one query. Comparison happens
        // in-memory on absolute paths because EF SQLite doesn't
        // translate Path.GetFullPath; the set size is bounded by
        // the number of templates that ever used the multipart
        // register path (typically small).
        HashSet<string> referenced;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();
            var raw = await db.Templates
                .AsNoTracking()
                .Where(t => t.UploadedFilesPath != null)
                .Select(t => t.UploadedFilesPath!)
                .ToListAsync(ct);
            referenced = new HashSet<string>(
                raw.Select(NormalisePath),
                StringComparer.Ordinal);
        }

        var cutoff = _clock.GetUtcNow().UtcDateTime - _options.Retention;
        var deleted = 0;
        foreach (var dir in Directory.EnumerateDirectories(_stagingRoot))
        {
            ct.ThrowIfCancellationRequested();

            var normalised = NormalisePath(dir);
            if (referenced.Contains(normalised))
            {
                continue;
            }

            DateTime lastWriteUtc;
            try
            {
                lastWriteUtc = Directory.GetLastWriteTimeUtc(dir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "TemplateUploadStagingCleanupWorker could not stat {Dir}; skipping.",
                    dir);
                continue;
            }

            if (lastWriteUtc > cutoff)
            {
                continue;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
                deleted++;
                _logger.LogInformation(
                    "TemplateUploadStagingCleanupWorker reclaimed orphan staging dir {Dir} (lastWrite={LastWrite}).",
                    dir, lastWriteUtc);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "TemplateUploadStagingCleanupWorker failed to delete orphan staging dir {Dir}; will retry next tick.",
                    dir);
            }
        }

        if (deleted > 0)
        {
            _logger.LogInformation(
                "TemplateUploadStagingCleanupWorker sweep reclaimed {Count} orphan staging dir(s).",
                deleted);
        }
        return deleted;
    }

    private static string NormalisePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
}
