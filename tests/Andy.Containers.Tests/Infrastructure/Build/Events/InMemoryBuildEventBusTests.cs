using Andy.Containers.Abstractions.Images;
using Andy.Containers.Infrastructure.Build.Events;
using Andy.Containers.Storage;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Infrastructure.Build.Events;

// IM9 (rivoli-ai/andy-containers#263). The event bus backs the SSE
// stream; getting it right is the difference between "the user sees
// build progress" and "the user sees a hung connection." These tests
// pin the contracts that matter:
//   - publish + subscribe in publish order
//   - multiple subscribers all see every event (fan-out)
//   - a subscriber that attaches mid-build sees buffered history
//   - Last-Event-ID resumption skips already-seen events
//   - terminal BuildCompletedEvent closes the stream cleanly
//   - bus survives a subscriber that walks away mid-stream
public class InMemoryBuildEventBusTests
{
    [Fact]
    public async Task Publish_DeliversToSingleSubscriber()
    {
        using var bus = new InMemoryBuildEventBus();
        var buildId = Guid.NewGuid();

        var subscribeTask = ConsumeAsync(bus, buildId, lastEventId: null, take: 2);

        // Give the subscription a beat to start before publishing —
        // the bus's buffer covers ordering, but we want to exercise
        // the "live publish to attached subscriber" path here too.
        await Task.Delay(20);
        bus.Publish(buildId, MakeStdout("hello"));
        bus.Publish(buildId, MakeCompleted(BuildOutcome.Succeeded));

        var events = await subscribeTask;
        events.Should().HaveCount(2);
        events[0].Event.Should().BeOfType<BuildStepStdoutEvent>()
            .Which.Line.Should().Be("hello");
        events[1].Event.Should().BeOfType<BuildCompletedEvent>();
    }

    [Fact]
    public async Task Subscribe_LateSubscriberSeesBufferedHistory()
    {
        using var bus = new InMemoryBuildEventBus();
        var buildId = Guid.NewGuid();

        bus.Publish(buildId, MakeStdout("first"));
        bus.Publish(buildId, MakeStdout("second"));
        bus.Publish(buildId, MakeCompleted(BuildOutcome.Succeeded));

        // Subscribe AFTER the build has completed — the buffer
        // should replay all three events and then close.
        var events = await ConsumeAsync(bus, buildId, lastEventId: null, take: 3);

        events.Should().HaveCount(3);
        events.OfType<BuildEventEnvelope>().Select(e => e.SequenceNumber)
            .Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Subscribe_LastEventIdSkipsAlreadySeenEvents()
    {
        using var bus = new InMemoryBuildEventBus();
        var buildId = Guid.NewGuid();

        bus.Publish(buildId, MakeStdout("a"));     // seq 1
        bus.Publish(buildId, MakeStdout("b"));     // seq 2
        bus.Publish(buildId, MakeStdout("c"));     // seq 3
        bus.Publish(buildId, MakeCompleted(BuildOutcome.Succeeded)); // seq 4

        // Reconnect with Last-Event-ID = 2 — should see seq 3 + 4
        // only.
        var events = await ConsumeAsync(bus, buildId, lastEventId: 2, take: 2);

        events.Select(e => e.SequenceNumber).Should().Equal(3, 4);
    }

    [Fact]
    public async Task Subscribe_MultipleSubscribersSeeFanOut()
    {
        using var bus = new InMemoryBuildEventBus();
        var buildId = Guid.NewGuid();

        var firstTask = ConsumeAsync(bus, buildId, lastEventId: null, take: 2);
        var secondTask = ConsumeAsync(bus, buildId, lastEventId: null, take: 2);

        await Task.Delay(20);
        bus.Publish(buildId, MakeStdout("everyone gets one"));
        bus.Publish(buildId, MakeCompleted(BuildOutcome.Succeeded));

        var first = await firstTask;
        var second = await secondTask;

        first.Should().HaveCount(2);
        second.Should().HaveCount(2);
        first.Select(e => e.Event.GetType()).Should().Equal(second.Select(e => e.Event.GetType()));
    }

    [Fact]
    public async Task Subscribe_TerminalEventClosesStream()
    {
        using var bus = new InMemoryBuildEventBus();
        var buildId = Guid.NewGuid();

        bus.Publish(buildId, MakeCompleted(BuildOutcome.Succeeded));

        // The subscription should yield the terminal event and
        // exit — not block forever waiting for more.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var events = new List<BuildEventEnvelope>();
        await foreach (var envelope in bus.SubscribeAsync(buildId, null, cts.Token))
        {
            events.Add(envelope);
        }

        events.Should().HaveCount(1);
        events[0].Event.Should().BeOfType<BuildCompletedEvent>();
        cts.IsCancellationRequested.Should().BeFalse(
            "the stream must close on its own when the terminal event fires — not because the test cancellation timed out.");
    }

    [Fact]
    public async Task Forget_DropsBuildState()
    {
        using var bus = new InMemoryBuildEventBus();
        var buildId = Guid.NewGuid();

        bus.Publish(buildId, MakeStdout("orphan"));
        bus.Publish(buildId, MakeCompleted(BuildOutcome.Succeeded));

        bus.Forget(buildId);

        // After forget, the buffered history is gone — a fresh
        // subscription sees nothing until something is published.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var events = new List<BuildEventEnvelope>();
        try
        {
            await foreach (var envelope in bus.SubscribeAsync(buildId, null, cts.Token))
            {
                events.Add(envelope);
            }
        }
        catch (OperationCanceledException)
        {
            // expected — no events arrived before cancellation.
        }

        events.Should().BeEmpty(
            "after Forget, the bus has no record of this build's events.");
    }

    private static async Task<List<BuildEventEnvelope>> ConsumeAsync(
        IBuildEventBus bus,
        Guid buildId,
        long? lastEventId,
        int take)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var events = new List<BuildEventEnvelope>();
        await foreach (var envelope in bus.SubscribeAsync(buildId, lastEventId, cts.Token))
        {
            events.Add(envelope);
            if (events.Count >= take)
            {
                break;
            }
        }
        return events;
    }

    private static BuildStepStdoutEvent MakeStdout(string line) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        StepName = "build",
        Line = line,
    };

    private static BuildCompletedEvent MakeCompleted(BuildOutcome outcome) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        Outcome = outcome,
    };
}
