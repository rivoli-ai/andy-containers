// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Containers.Infrastructure.Messaging;
using Andy.Containers.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Containers.Api.Tests.Messaging;

public class InMemoryMessageBusTests
{
    [Fact]
    public void MatchesSubject_ExactMatch()
    {
        InMemoryMessageBus.MatchesSubject("a.b.c", "a.b.c").Should().BeTrue();
        InMemoryMessageBus.MatchesSubject("a.b.c", "a.b.d").Should().BeFalse();
        InMemoryMessageBus.MatchesSubject("a.b.c", "a.b").Should().BeFalse();
        InMemoryMessageBus.MatchesSubject("a.b.c", "a.b.c.d").Should().BeFalse();
    }

    [Fact]
    public void MatchesSubject_StarWildcardMatchesOneToken()
    {
        InMemoryMessageBus.MatchesSubject("a.*.c", "a.b.c").Should().BeTrue();
        InMemoryMessageBus.MatchesSubject("a.*.c", "a.x.c").Should().BeTrue();
        InMemoryMessageBus.MatchesSubject("a.*.c", "a.b.d").Should().BeFalse();
        InMemoryMessageBus.MatchesSubject("a.*.c", "a.b.c.d").Should().BeFalse();
        InMemoryMessageBus.MatchesSubject("a.*", "a").Should().BeFalse();
    }

    [Fact]
    public void MatchesSubject_GreaterThanWildcardMatchesTail()
    {
        InMemoryMessageBus.MatchesSubject("a.>", "a.b").Should().BeTrue();
        InMemoryMessageBus.MatchesSubject("a.>", "a.b.c.d").Should().BeTrue();
        InMemoryMessageBus.MatchesSubject("a.>", "a").Should().BeFalse();
        InMemoryMessageBus.MatchesSubject("andy.containers.events.>", "andy.containers.events.run.123.finished")
            .Should().BeTrue();
    }

    [Fact]
    public async Task PublishAsync_DeliversToMatchingSubscriber()
    {
        var bus = new InMemoryMessageBus(NullLogger<InMemoryMessageBus>.Instance);
        bus.EnsureChannel("andy.containers.events.run.>");

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var received = new List<IncomingMessage>();
        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var msg in bus.SubscribeAsync(
                "andy.containers.events.run.>",
                new SubscriptionOptions("test"),
                cts.Token))
            {
                received.Add(msg);
                if (received.Count == 1) cts.Cancel();
            }
        }, cts.Token);

        await bus.PublishAsync(
            "andy.containers.events.run.abc.finished",
            new { runId = "abc", status = "success" },
            MessageHeaders.NewRoot());

        try { await subscribeTask; } catch (OperationCanceledException) { }

        received.Should().HaveCount(1);
        received[0].Subject.Should().Be("andy.containers.events.run.abc.finished");
    }

    [Fact]
    public async Task PublishAsync_SkipsNonMatchingSubscribers()
    {
        var bus = new InMemoryMessageBus(NullLogger<InMemoryMessageBus>.Instance);
        bus.EnsureChannel("andy.issues.>");   // different filter
        bus.EnsureChannel("andy.containers.>");

        await bus.PublishAsync(
            "andy.containers.events.run.123.finished",
            new { runId = "123" },
            MessageHeaders.NewRoot());

        // Non-matching subscriber's channel stays empty; matching one got the message.
        var matching = bus.EnsureChannel("andy.containers.>");
        matching.Reader.Count.Should().Be(1);

        var nonMatching = bus.EnsureChannel("andy.issues.>");
        nonMatching.Reader.Count.Should().Be(0);
    }

    [Fact]
    public async Task PublishAsync_DropsMessagesExceedingGenerationLimit()
    {
        var bus = new InMemoryMessageBus(NullLogger<InMemoryMessageBus>.Instance);
        bus.EnsureChannel("andy.containers.>");

        var overLimit = new MessageHeaders(
            MsgId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            Generation: MessageHeaders.MaxGeneration + 1);

        await bus.PublishAsync("andy.containers.events.run.x.finished", new { }, overLimit);

        bus.EnsureChannel("andy.containers.>").Reader.Count.Should().Be(0);
    }

    [Fact]
    public void MessageHeaders_Follow_IncrementsGeneration()
    {
        var root = MessageHeaders.NewRoot();
        var child = MessageHeaders.Follow(root);

        child.CorrelationId.Should().Be(root.CorrelationId);
        child.CausationId.Should().Be(root.MsgId);
        child.Generation.Should().Be(1);
        child.ExceedsGenerationLimit.Should().BeFalse();
    }

    [Fact]
    public void MessageHeaders_NewRoot_MakesItsOwnCorrelation()
    {
        var root = MessageHeaders.NewRoot();
        root.CorrelationId.Should().Be(root.MsgId);
        root.CausationId.Should().BeNull();
        root.Generation.Should().Be(0);
    }
}
