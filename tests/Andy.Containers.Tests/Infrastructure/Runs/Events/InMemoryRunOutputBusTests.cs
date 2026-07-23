using Andy.Containers.Infrastructure.Runs.Events;
using Andy.Containers.Storage;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Infrastructure.Runs.Events;

// F4.1 (rivoli-ai/conductor#1934). The run-output bus backs the mid-run
// SSE stream (GET /api/runs/{id}/output). It is a direct sibling of
// InMemoryBuildEventBus, so the contracts that matter are the same:
//   - publish + subscribe in publish order, with monotonic sequence ids
//   - a late subscriber sees buffered history then live-tails
//   - Last-Event-ID resumption skips already-seen lines (no dupes/gaps)
//   - multiple subscribers all see every line (fan-out)
//   - Complete() closes every subscriber after a final drain
//   - a subscriber that attaches AFTER Complete() replays + closes
//   - stdout/stderr stream-kind survives round-trip
//   - lines published after Complete() are dropped (frozen buffer)
//   - Forget() drops a run's buffered history
public class InMemoryRunOutputBusTests
{
    [Fact]
    public async Task Publish_DeliversToSingleSubscriberInOrderWithSequenceIds()
    {
        using var bus = new InMemoryRunOutputBus();
        var runId = Guid.NewGuid();

        var subscribeTask = ConsumeAsync(bus, runId, lastEventId: null, take: 2);

        await Task.Delay(20);
        bus.Publish(runId, Line(RunOutputStream.Stdout, "hello"));
        bus.Publish(runId, Line(RunOutputStream.Stdout, "world"));

        var lines = await subscribeTask;
        lines.Should().HaveCount(2);
        lines[0].SequenceNumber.Should().Be(1);
        lines[1].SequenceNumber.Should().Be(2);
        lines[0].Line.Line.Should().Be("hello");
        lines[1].Line.Line.Should().Be("world");
    }

    [Fact]
    public async Task Subscribe_LateSubscriberSeesBufferedHistory()
    {
        using var bus = new InMemoryRunOutputBus();
        var runId = Guid.NewGuid();

        bus.Publish(runId, Line(RunOutputStream.Stdout, "first"));
        bus.Publish(runId, Line(RunOutputStream.Stdout, "second"));
        bus.Complete(runId);

        // Subscribe AFTER the run completed — buffer replays both lines
        // then closes.
        var lines = await ConsumeAsync(bus, runId, lastEventId: null, take: 2);

        lines.Should().HaveCount(2);
        lines.Select(e => e.SequenceNumber).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Subscribe_LastEventIdSkipsAlreadySeenLines_NoDupesNoGaps()
    {
        using var bus = new InMemoryRunOutputBus();
        var runId = Guid.NewGuid();

        bus.Publish(runId, Line(RunOutputStream.Stdout, "a")); // seq 1
        bus.Publish(runId, Line(RunOutputStream.Stdout, "b")); // seq 2
        bus.Publish(runId, Line(RunOutputStream.Stdout, "c")); // seq 3
        bus.Complete(runId);

        // Reconnect with Last-Event-ID = 1 → must see seq 2 + 3 only.
        var lines = await ConsumeAsync(bus, runId, lastEventId: 1, take: 2);

        lines.Select(e => e.SequenceNumber).Should().Equal(2, 3);
        lines.Select(e => e.Line.Line).Should().Equal("b", "c");
    }

    [Fact]
    public async Task Subscribe_MultipleSubscribersSeeFanOut()
    {
        using var bus = new InMemoryRunOutputBus();
        var runId = Guid.NewGuid();

        var firstTask = ConsumeAsync(bus, runId, lastEventId: null, take: 1);
        var secondTask = ConsumeAsync(bus, runId, lastEventId: null, take: 1);

        await Task.Delay(20);
        bus.Publish(runId, Line(RunOutputStream.Stdout, "everyone gets one"));

        var first = await firstTask;
        var second = await secondTask;

        first.Should().HaveCount(1);
        second.Should().HaveCount(1);
        first[0].Line.Line.Should().Be(second[0].Line.Line);
    }

    [Fact]
    public async Task Complete_ClosesSubscriberAfterFinalDrain()
    {
        using var bus = new InMemoryRunOutputBus();
        var runId = Guid.NewGuid();

        bus.Publish(runId, Line(RunOutputStream.Stdout, "tail line"));
        bus.Complete(runId);

        // The subscription should yield the buffered line then exit on
        // its own — not block waiting for more.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var lines = new List<RunOutputEnvelope>();
        await foreach (var envelope in bus.SubscribeAsync(runId, null, cts.Token))
        {
            lines.Add(envelope);
        }

        lines.Should().HaveCount(1);
        cts.IsCancellationRequested.Should().BeFalse(
            "the stream must close on its own when the run is marked terminal — not because the test cancellation timed out.");
    }

    [Fact]
    public async Task Complete_BeforeSubscribe_ReplaysThenClosesImmediately()
    {
        using var bus = new InMemoryRunOutputBus();
        var runId = Guid.NewGuid();

        bus.Publish(runId, Line(RunOutputStream.Stderr, "boom"));
        bus.Complete(runId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var lines = new List<RunOutputEnvelope>();
        await foreach (var envelope in bus.SubscribeAsync(runId, null, cts.Token))
        {
            lines.Add(envelope);
        }

        lines.Should().HaveCount(1);
        lines[0].Line.Stream.Should().Be(RunOutputStream.Stderr);
        cts.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task Publish_AfterComplete_IsDropped()
    {
        using var bus = new InMemoryRunOutputBus();
        var runId = Guid.NewGuid();

        bus.Publish(runId, Line(RunOutputStream.Stdout, "before"));
        bus.Complete(runId);
        // This line lands after the terminal marker — the buffer is
        // frozen, so a fresh subscriber must never see it.
        bus.Publish(runId, Line(RunOutputStream.Stdout, "after-terminal"));

        var lines = new List<RunOutputEnvelope>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (var envelope in bus.SubscribeAsync(runId, null, cts.Token))
        {
            lines.Add(envelope);
        }

        lines.Should().HaveCount(1);
        lines[0].Line.Line.Should().Be("before");
    }

    [Fact]
    public async Task StreamKind_SurvivesRoundTrip()
    {
        using var bus = new InMemoryRunOutputBus();
        var runId = Guid.NewGuid();

        bus.Publish(runId, Line(RunOutputStream.Stdout, "out"));
        bus.Publish(runId, Line(RunOutputStream.Stderr, "err"));
        bus.Complete(runId);

        var lines = await ConsumeAsync(bus, runId, lastEventId: null, take: 2);

        lines[0].Line.Stream.Should().Be(RunOutputStream.Stdout);
        lines[1].Line.Stream.Should().Be(RunOutputStream.Stderr);
    }

    [Fact]
    public async Task Forget_DropsRunState()
    {
        using var bus = new InMemoryRunOutputBus();
        var runId = Guid.NewGuid();

        bus.Publish(runId, Line(RunOutputStream.Stdout, "orphan"));
        bus.Complete(runId);

        bus.Forget(runId);

        // After Forget, the buffered history is gone — a fresh
        // subscription sees nothing until something is published.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var lines = new List<RunOutputEnvelope>();
        try
        {
            await foreach (var envelope in bus.SubscribeAsync(runId, null, cts.Token))
            {
                lines.Add(envelope);
            }
        }
        catch (OperationCanceledException)
        {
            // expected — no lines arrived before cancellation.
        }

        lines.Should().BeEmpty();
    }

    [Fact]
    public async Task Subscribe_AgedOutLastEventId_RestartsFromOldestBuffered()
    {
        // Bounded buffer: a tiny capacity forces older lines to drop. A
        // reconnect asking for an id that has aged out replays from the
        // oldest line still buffered rather than hanging or throwing.
        using var bus = new InMemoryRunOutputBus(new RunOutputBusOptions { BufferSize = 2 });
        var runId = Guid.NewGuid();

        bus.Publish(runId, Line(RunOutputStream.Stdout, "1")); // seq 1, evicted
        bus.Publish(runId, Line(RunOutputStream.Stdout, "2")); // seq 2, evicted
        bus.Publish(runId, Line(RunOutputStream.Stdout, "3")); // seq 3
        bus.Publish(runId, Line(RunOutputStream.Stdout, "4")); // seq 4
        bus.Complete(runId);

        // Ask to resume after seq 1, which is no longer buffered. The
        // bus replays the oldest still-buffered line (seq 3) onward.
        var lines = await ConsumeAsync(bus, runId, lastEventId: 1, take: 2);

        lines.Select(e => e.SequenceNumber).Should().Equal(3, 4);
    }

    [Fact]
    public async Task Subscribe_TailAndSince_FilterReplay()
    {
        using var bus = new InMemoryRunOutputBus();
        var runId = Guid.NewGuid();
        var cutoff = DateTimeOffset.UtcNow;

        bus.Publish(runId, new RunOutputLine(
            RunOutputStream.Stdout,
            "too-old",
            cutoff.AddMinutes(-1)));
        bus.Publish(runId, new RunOutputLine(
            RunOutputStream.Stdout,
            "first-current",
            cutoff));
        bus.Publish(runId, new RunOutputLine(
            RunOutputStream.Stdout,
            "last-current",
            cutoff.AddSeconds(1)));

        var lines = new List<RunOutputEnvelope>();
        await foreach (var envelope in bus.SubscribeAsync(
            runId,
            lastEventId: null,
            CancellationToken.None,
            tail: 1,
            since: cutoff,
            follow: false))
        {
            lines.Add(envelope);
        }

        lines.Should().ContainSingle();
        lines[0].Line.Line.Should().Be("last-current");
    }

    [Fact]
    public async Task Subscribe_FollowFalse_ReplaysAndClosesWithoutTerminalMarker()
    {
        using var bus = new InMemoryRunOutputBus();
        var runId = Guid.NewGuid();
        bus.Publish(runId, Line(RunOutputStream.Stdout, "snapshot"));

        var lines = new List<RunOutputEnvelope>();
        await foreach (var envelope in bus.SubscribeAsync(
            runId,
            lastEventId: null,
            CancellationToken.None,
            follow: false))
        {
            lines.Add(envelope);
        }

        lines.Should().ContainSingle();
        lines[0].Line.Line.Should().Be("snapshot");
    }

    private static async Task<List<RunOutputEnvelope>> ConsumeAsync(
        IRunOutputBus bus, Guid runId, long? lastEventId, int take)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var lines = new List<RunOutputEnvelope>();
        await foreach (var envelope in bus.SubscribeAsync(runId, lastEventId, cts.Token))
        {
            lines.Add(envelope);
            if (lines.Count >= take)
            {
                break;
            }
        }
        return lines;
    }

    [Fact]
    public async Task AttemptCorrelatedOutput_UsesSharedMonotonicSequenceAndReconnectCursor()
    {
        using var bus = new InMemoryRunOutputBus();
        var runId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();

        var first = bus.Publish(runId, Line(RunOutputStream.Stdout, "one"), attemptId);
        var second = bus.Publish(runId, Line(RunOutputStream.Stdout, "two"), attemptId);
        bus.Complete(runId);

        second.SequenceNumber.Should().BeGreaterThan(first.SequenceNumber);
        second.AttemptId.Should().Be(attemptId);
        second.RunId.Should().Be(runId);

        var replay = new List<RunOutputEnvelope>();
        await foreach (var envelope in bus.SubscribeAsync(
            runId,
            first.SequenceNumber,
            CancellationToken.None))
        {
            replay.Add(envelope);
        }

        replay.Should().ContainSingle()
            .Which.Line.Line.Should().Be("two");
    }

    private static RunOutputLine Line(RunOutputStream stream, string text)
        => new(stream, text, DateTimeOffset.UtcNow);
}
