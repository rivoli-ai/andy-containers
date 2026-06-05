// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Containers.Infrastructure.Runs.Events;
using Andy.Containers.Storage;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

/// <summary>
/// SM.2.6 (rivoli-ai/conductor#2008). Unit tests for
/// <see cref="InMemoryContainerLifecycleBus"/> — verifies publish /
/// subscribe / replay semantics the SSE endpoint relies on.
/// </summary>
public class InMemoryContainerLifecycleBusTests
{
    private static ContainerLifecycleEvent MakeEvent(Guid containerId, string phase, string? reason = null)
        => new(
            ContainerId: containerId,
            Phase: phase,
            PhaseData: new ContainerLifecyclePhaseData(Reason: reason),
            CorrelationId: containerId,
            Timestamp: DateTimeOffset.UtcNow);

    [Fact]
    public async Task Subscribe_ReceivesPublishedEvents()
    {
        var bus = new InMemoryContainerLifecycleBus();
        var cid = Guid.NewGuid();
        var evt = MakeEvent(cid, "running");

        using var cts = new CancellationTokenSource();
        var received = new List<ContainerLifecycleEnvelope>();
        var subTask = Task.Run(async () =>
        {
            await foreach (var env in bus.SubscribeAsync(null, cts.Token))
            {
                received.Add(env);
                if (received.Count >= 1) break;
            }
        });

        // Give the subscriber time to attach.
        await Task.Delay(50);
        bus.Publish(evt);
        await subTask.WaitAsync(TimeSpan.FromSeconds(5));

        received.Should().HaveCount(1);
        received[0].Event.ContainerId.Should().Be(cid);
        received[0].Event.Phase.Should().Be("running");
        received[0].SequenceNumber.Should().Be(1);
    }

    [Fact]
    public async Task Subscribe_ReplayBufferedEvents_WhenLastEventIdProvided()
    {
        var bus = new InMemoryContainerLifecycleBus();
        var cid = Guid.NewGuid();

        // Publish 3 events before any subscriber attaches.
        bus.Publish(MakeEvent(cid, "pending"));
        bus.Publish(MakeEvent(cid, "creating"));
        bus.Publish(MakeEvent(cid, "running"));

        // Subscribe asking to resume after seq 1 — should replay seq 2 + 3.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<ContainerLifecycleEnvelope>();
        await foreach (var env in bus.SubscribeAsync(lastEventId: 1, cts.Token))
        {
            received.Add(env);
            if (received.Count >= 2) break;
        }

        received.Should().HaveCount(2);
        received[0].SequenceNumber.Should().Be(2);
        received[0].Event.Phase.Should().Be("creating");
        received[1].SequenceNumber.Should().Be(3);
        received[1].Event.Phase.Should().Be("running");
    }

    [Fact]
    public async Task Subscribe_WithNoLastEventId_ReplaysFull_Buffer()
    {
        var bus = new InMemoryContainerLifecycleBus();
        var cid = Guid.NewGuid();

        bus.Publish(MakeEvent(cid, "pending"));
        bus.Publish(MakeEvent(cid, "creating"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<ContainerLifecycleEnvelope>();
        await foreach (var env in bus.SubscribeAsync(null, cts.Token))
        {
            received.Add(env);
            if (received.Count >= 2) break;
        }

        received.Should().HaveCount(2);
        received.Select(e => e.Event.Phase).Should().ContainInOrder("pending", "creating");
    }

    [Fact]
    public async Task Publish_FailedPhaseWithAbortReason_CarriesReason()
    {
        var bus = new InMemoryContainerLifecycleBus();
        var cid = Guid.NewGuid();
        bus.Publish(MakeEvent(cid, "failed", reason: "quota_denied"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<ContainerLifecycleEnvelope>();
        await foreach (var env in bus.SubscribeAsync(null, cts.Token))
        {
            received.Add(env);
            if (received.Count >= 1) break;
        }

        received.Should().HaveCount(1);
        received[0].Event.Phase.Should().Be("failed");
        received[0].Event.PhaseData.Reason.Should().Be("quota_denied");
    }

    [Fact]
    public async Task MultipleSubscribers_EachReceiveAllEvents()
    {
        var bus = new InMemoryContainerLifecycleBus();
        var cid = Guid.NewGuid();

        var received1 = new List<ContainerLifecycleEnvelope>();
        var received2 = new List<ContainerLifecycleEnvelope>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var sub1 = Task.Run(async () =>
        {
            await foreach (var env in bus.SubscribeAsync(null, cts.Token))
            {
                received1.Add(env);
                if (received1.Count >= 2) break;
            }
        });
        var sub2 = Task.Run(async () =>
        {
            await foreach (var env in bus.SubscribeAsync(null, cts.Token))
            {
                received2.Add(env);
                if (received2.Count >= 2) break;
            }
        });

        await Task.Delay(50);
        bus.Publish(MakeEvent(cid, "creating"));
        bus.Publish(MakeEvent(cid, "running"));

        await Task.WhenAll(sub1, sub2).WaitAsync(TimeSpan.FromSeconds(5));

        received1.Should().HaveCount(2);
        received2.Should().HaveCount(2);
    }

    [Fact]
    public void Publish_AfterDispose_DoesNotThrow()
    {
        var bus = new InMemoryContainerLifecycleBus();
        bus.Dispose();

        // Publish after dispose: may silently drop but must not throw
        var act = () => bus.Publish(MakeEvent(Guid.NewGuid(), "running"));
        act.Should().NotThrow();
    }

    /// <summary>
    /// Acceptance criteria §2: the SSE event for every ContainerLifecyclePhase
    /// value can be emitted and received with the correct phase string.
    /// </summary>
    [Theory]
    [InlineData("pending")]
    [InlineData("pulling")]
    [InlineData("creating")]
    [InlineData("starting")]
    [InlineData("running")]
    [InlineData("stopping")]
    [InlineData("stopped")]
    [InlineData("exited")]
    [InlineData("oom")]
    [InlineData("failed")]
    [InlineData("destroying")]
    [InlineData("destroyed")]
    public async Task AllContractPhases_CanBePublishedAndReceived(string phase)
    {
        var bus = new InMemoryContainerLifecycleBus();
        var cid = Guid.NewGuid();
        bus.Publish(MakeEvent(cid, phase));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<ContainerLifecycleEnvelope>();
        await foreach (var env in bus.SubscribeAsync(null, cts.Token))
        {
            received.Add(env);
            if (received.Count >= 1) break;
        }

        received.Should().HaveCount(1);
        received[0].Event.Phase.Should().Be(phase);
        received[0].Event.ContainerId.Should().Be(cid);
    }
}
