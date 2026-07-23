using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Messaging;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// AP9 (rivoli-ai/andy-containers#111). RunEventStream is the shared
// outbox-poll loop consumed by the MCP run.events tool, the HTTP
// NDJSON endpoint, and the CLI. These tests pin the contract that
// matters — terminal-stop, drain pass, prefix filter — independently
// of any of those callers so each consumer can rely on the helper.
public class RunEventStreamTests : IDisposable
{
    private readonly ContainersDbContext _db;
    // Tight poll so terminal-stop tests don't add seconds of latency.
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(20);

    public RunEventStreamTests()
    {
        _db = InMemoryDbHelper.CreateContext();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task TerminalRunWithBackfill_YieldsAllThenStops()
    {
        var run = SeedRun(RunStatus.Succeeded);
        _db.AppendAgentRunEvent(run, RunEventKind.Finished, exitCode: 0, durationSeconds: 1.5);
        await _db.SaveChangesAsync();

        var collected = new List<RunEventDto>();
        await foreach (var evt in RunEventStream.AsyncEnumerate(_db, run.Id, FastPoll))
        {
            collected.Add(evt);
        }

        collected.Should().ContainSingle();
        collected[0].Subject.Should().Be($"andy.containers.events.run.{run.Id}.finished");
        collected[0].ExitCode.Should().Be(0);
        collected[0].DurationSeconds.Should().Be(1.5);
    }

    [Fact]
    public async Task TerminalRun_OnRealSqlite_YieldsEventAndStopsWithoutTranslationError()
    {
        // Regression: the cursor advance (`CreatedAt > cursor`) is a
        // DateTimeOffset comparison the SQLite EF provider CANNOT translate.
        // After the first poll delivers the terminal event and sets the
        // cursor, the drain poll re-runs the query WITH that cursor and the
        // buggy code threw InvalidOperationException ("could not be
        // translated") — which aborted the run.events stream so the terminal
        // Succeeded/Failed event never reached the upstream executor and the
        // task hung "Running" forever. The InMemory provider masks this (it
        // evaluates any CLR predicate), so — like
        // OutboxDispatcherTests.DrainOnceAsync_DoesNotCrashUnderSqlite — this
        // runs RunEventStream against a REAL SQLite database.
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ContainersDbContext>()
            .UseSqlite(connection)
            .Options;
        using var db = new ContainersDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = "stream-test",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Status = RunStatus.Succeeded,
        };
        db.Runs.Add(run);
        db.AppendAgentRunEvent(run, RunEventKind.Finished, exitCode: 0, durationSeconds: 1.5);
        await db.SaveChangesAsync();

        var collected = new List<RunEventDto>();
        await foreach (var evt in RunEventStream.AsyncEnumerate(db, run.Id, FastPoll))
        {
            collected.Add(evt);
        }

        collected.Should().ContainSingle();
        collected[0].Subject.Should().Be($"andy.containers.events.run.{run.Id}.finished");
        collected[0].ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task FiltersByRunIdPrefix_DoesNotEmitOtherRunsEvents()
    {
        var target = SeedRun(RunStatus.Succeeded);
        var other = SeedRun(RunStatus.Failed);
        _db.AppendAgentRunEvent(target, RunEventKind.Finished);
        _db.AppendAgentRunEvent(other, RunEventKind.Failed);
        await _db.SaveChangesAsync();

        var collected = new List<RunEventDto>();
        await foreach (var evt in RunEventStream.AsyncEnumerate(_db, target.Id, FastPoll))
        {
            collected.Add(evt);
        }

        collected.Should().ContainSingle()
            .Which.RunId.Should().Be(target.Id);
    }

    [Fact]
    public async Task AfterSequence_ReplaysOnlyNewerLifecycleEvents()
    {
        var run = SeedRun(RunStatus.Succeeded);
        _db.AppendAgentRunEvent(run, RunEventKind.Queued);
        _db.AppendAgentRunEvent(run, RunEventKind.Running);
        _db.AppendAgentRunEvent(run, RunEventKind.Finished);
        await _db.SaveChangesAsync();

        var all = (await _db.OutboxEntries.ToListAsync())
            .Select(RunEventDto.FromOutbox)
            .Where(e => e is not null)
            .Cast<RunEventDto>()
            .OrderBy(e => e.Sequence)
            .ToList();

        var collected = new List<RunEventDto>();
        await foreach (var evt in RunEventStream.AsyncEnumerate(
            _db,
            run.Id,
            FastPoll,
            afterSequence: all[0].Sequence))
        {
            collected.Add(evt);
        }

        collected.Select(e => e.Kind).Should().Equal("running", "finished");
        collected.Select(e => e.Sequence).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task UnknownRun_TerminatesImmediately()
    {
        // Run row not in DB → helper exits cleanly without yielding.
        var collected = new List<RunEventDto>();
        await foreach (var evt in RunEventStream.AsyncEnumerate(_db, Guid.NewGuid(), FastPoll))
        {
            collected.Add(evt);
        }

        collected.Should().BeEmpty();
    }

    [Fact]
    public async Task LiveTerminalTransition_DrainsEventsBeforeExit()
    {
        // Reproduce the AP8 race: while the consumer is in its poll
        // loop, the runner commits Cancelled + the cancelled outbox row
        // in the same transaction. The drain pass must catch that row
        // before exiting on terminal observation.
        var run = SeedRun(RunStatus.Running);

        var streamTask = Task.Run(async () =>
        {
            var collected = new List<RunEventDto>();
            await foreach (var evt in RunEventStream.AsyncEnumerate(_db, run.Id, FastPoll))
            {
                collected.Add(evt);
            }
            return collected;
        });

        // Give the loop one poll cycle, then commit terminal state.
        await Task.Delay(50);
        run.TransitionTo(RunStatus.Cancelled);
        _db.AppendAgentRunEvent(run, RunEventKind.Cancelled);
        await _db.SaveChangesAsync();

        var collected = await streamTask;

        collected.Should().ContainSingle()
            .Which.Kind.Should().Be("cancelled");
    }

    [Fact]
    public async Task CallerCancellation_ClosesEnumeration()
    {
        var run = SeedRun(RunStatus.Running);
        using var cts = new CancellationTokenSource();

        var enumerationTask = Task.Run(async () =>
        {
            var collected = new List<RunEventDto>();
            try
            {
                await foreach (var evt in RunEventStream.AsyncEnumerate(_db, run.Id, FastPoll, cts.Token))
                {
                    collected.Add(evt);
                }
            }
            catch (OperationCanceledException)
            {
                // Either swallowed by the helper or raised by Task.Delay —
                // both are valid; the test only cares that we exit.
            }
            return collected;
        });

        await Task.Delay(50);
        cts.Cancel();

        var collected = await enumerationTask;
        collected.Should().BeEmpty(
            "no events were emitted before cancel; the loop must close cleanly anyway");
    }

    private Run SeedRun(RunStatus status)
    {
        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = "stream-test",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Status = status,
        };
        _db.Runs.Add(run);
        _db.SaveChanges();
        return run;
    }
}
