using System.Runtime.CompilerServices;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Services;

/// <summary>
/// AP8/AP9 (rivoli-ai/andy-containers#110, #111). Shared outbox-polling
/// stream of <see cref="RunEventDto"/> values for a given run id. The MCP
/// <c>run.events</c> tool, the HTTP <c>GET /api/runs/{id}/events</c>
/// endpoint, and the CLI <c>runs events</c> command all consume this so
/// the polling loop, terminal-stop logic, and cursor semantics live in
/// exactly one place.
/// </summary>
/// <remarks>
/// The helper polls <see cref="ContainersDbContext.OutboxEntries"/> for
/// rows with subject prefix <c>andy.containers.events.run.{id}.</c>,
/// yielding each successfully-parsed <see cref="RunEventDto"/>. It stops
/// when the run reaches a terminal status (after a final drain pass so a
/// terminal write that committed in the same transaction as the last
/// event still surfaces) or the run row disappears.
/// </remarks>
public static class RunEventStream
{
    /// <summary>Default poll cadence; short enough to feel live, long enough not to hammer the DB.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Bounded batch size per poll. A single run shouldn't accumulate this many backfill events.</summary>
    private const int BatchSize = 64;

    public static async IAsyncEnumerable<RunEventDto> AsyncEnumerate(
        ContainersDbContext db,
        Guid runId,
        TimeSpan? pollInterval = null,
        [EnumeratorCancellation] CancellationToken ct = default,
        long? afterSequence = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        var interval = pollInterval ?? DefaultPollInterval;
        var subjectPrefix = $"andy.containers.events.run.{runId}.";
        var cursor = afterSequence;
        var sawTerminal = false;

        while (!ct.IsCancellationRequested)
        {
            // SQLite cannot order DateTimeOffset reliably, and timestamps are
            // not unique when several lifecycle rows share one transaction.
            // Scope by translatable subject prefix, then parse/filter/order by
            // the authoritative payload sequence on the client. One run's
            // lifecycle history is small, so this remains bounded.
            var rows = await db.OutboxEntries
                .AsNoTracking()
                .Where(e => e.Subject.StartsWith(subjectPrefix))
                .ToListAsync(ct);

            var batch = rows
                .Select(e => (Entry: e, Event: RunEventDto.FromOutbox(e)))
                .Where(x => x.Event is not null
                    && (cursor is null || x.Event.Sequence > cursor.Value))
                .OrderBy(x => x.Event!.Sequence)
                .ThenBy(x => x.Entry.CreatedAt)
                .Take(BatchSize)
                .ToList();

            foreach (var item in batch)
            {
                var dto = item.Event!;
                cursor = dto.Sequence;
                yield return dto;
            }

            if (sawTerminal && batch.Count == 0)
            {
                // Saw terminal on a prior tick AND this tick produced
                // nothing new — every event committed before the terminal
                // write has been delivered.
                yield break;
            }

            // Re-check the row's status. Reading the OutboxEntry alone
            // isn't sufficient because force-cancel paths flip the row
            // and the event row in the same SaveChangesAsync; the read
            // fan-out can still interleave.
            var current = await db.Runs
                .AsNoTracking()
                .Where(r => r.Id == runId)
                .Select(r => (RunStatus?)r.Status)
                .FirstOrDefaultAsync(ct);
            if (current is null)
            {
                yield break;
            }
            if (RunStatusTransitions.IsTerminal(current.Value))
            {
                sawTerminal = true;
                // One more pass to drain any event that landed between
                // this batch and the terminal write before we exit.
                continue;
            }

            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }
}
