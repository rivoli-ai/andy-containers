// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Containers.Infrastructure.Messaging;
using Andy.Containers.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream.Models;
using Xunit;

namespace Andy.Containers.Integration.Tests.Messaging;

// End-to-end publish/subscribe tests against a real NATS JetStream
// server. Each test uses a randomized subject under andy.containers.>
// and a randomized durable consumer name so runs don't cross-pollinate.
//
// Run locally with:
//   docker compose up -d nats
//   ANDY_CONTAINERS_TEST_NATS=true dotnet test tests/Andy.Containers.Integration.Tests
public class NatsMessageBusTests
{
    private static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(10);

    private static string NatsUrl =>
        Environment.GetEnvironmentVariable("NATS_URL")
        ?? Environment.GetEnvironmentVariable("Messaging__Nats__Url")
        ?? "nats://localhost:4222";

    // Each test gets a unique stream name + subject prefix so concurrent
    // tests don't hit "subjects overlap with an existing stream" from
    // JetStream when running in parallel, and leftover state from a
    // previous run can't leak in.
    private static async Task<(NatsMessageBus Bus, string SubjectPrefix)> CreateAndConnectAsync()
    {
        var testId = Guid.NewGuid().ToString("N");
        var subjectPrefix = $"andy.test-{testId}";

        var opts = Options.Create(new NatsOptions
        {
            Url = NatsUrl,
            StreamName = $"ANDY_TEST_{testId}",
            StreamSubjects = [$"{subjectPrefix}.>"]
        });

        var bus = new NatsMessageBus(opts, NullLogger<NatsMessageBus>.Instance);
        await bus.ConnectAsync();

        var streamConfig = new StreamConfig(opts.Value.StreamName, opts.Value.StreamSubjects)
        {
            MaxAge = TimeSpan.FromMinutes(5)
        };
        await bus.JetStream.CreateOrUpdateStreamAsync(streamConfig);

        return (bus, subjectPrefix);
    }

    [NatsFact]
    public async Task PublishAndSubscribe_RoundTripWithHeaders()
    {
        var (bus, prefix) = await CreateAndConnectAsync();
        await using var _ = bus;

        var headers = MessageHeaders.NewRoot();
        var subject = $"{prefix}.events.run.{Guid.NewGuid():N}.finished";
        var payload = new { runId = "abc", status = "success", schema_version = 1 };

        await bus.PublishAsync(subject, payload, headers);

        var options = new SubscriptionOptions(
            DurableName: $"test-roundtrip-{Guid.NewGuid():N}");

        using var cts = new CancellationTokenSource(MessageTimeout);
        IncomingMessage? received = null;

        await foreach (var msg in bus.SubscribeAsync(subject, options, cts.Token))
        {
            received = msg;
            await msg.AckAsync(cts.Token);
            break;
        }

        received.Should().NotBeNull();
        received!.Subject.Should().Be(subject);
        received.Headers.MsgId.Should().Be(headers.MsgId);
        received.Headers.CorrelationId.Should().Be(headers.CorrelationId);
        received.Headers.CausationId.Should().BeNull();
        received.Headers.Generation.Should().Be(0);

        var body = JsonSerializer.Deserialize<JsonElement>(received.Payload.Span);
        body.GetProperty("run_id").GetString().Should().Be("abc");
        body.GetProperty("status").GetString().Should().Be("success");
    }

    [NatsFact]
    public async Task Publish_GenerationExceeded_DropsMessage()
    {
        var (bus, prefix) = await CreateAndConnectAsync();
        await using var _ = bus;

        var subject = $"{prefix}.events.run.{Guid.NewGuid():N}.finished";
        var headers = new MessageHeaders(
            MsgId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            Generation: MessageHeaders.MaxGeneration + 1);

        // ADR 0001 circuit breaker: silently dropped, no exception.
        await bus.PublishAsync(subject, new { }, headers);

        // Subscribe and verify nothing arrives within a short window.
        var options = new SubscriptionOptions(
            DurableName: $"test-genlimit-{Guid.NewGuid():N}");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var count = 0;

        try
        {
            await foreach (var msg in bus.SubscribeAsync(subject, options, cts.Token))
            {
                count++;
                await msg.AckAsync(cts.Token);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Expected — timed out with no messages.
        }

        count.Should().Be(0);
    }

    [NatsFact]
    public async Task SubscribeAsync_DurableWithDottedName_SanitizesToDashes()
    {
        // NATS 2.10+ rejects consumer names with '.' because the CREATE
        // API embeds them in a subject. NatsMessageBus sanitizes
        // internally; verify a dotted DurableName still works.
        var (bus, prefix) = await CreateAndConnectAsync();
        await using var _ = bus;

        var subject = $"{prefix}.events.run.{Guid.NewGuid():N}.finished";
        await bus.PublishAsync(subject, new { runId = "x" }, MessageHeaders.NewRoot());

        var options = new SubscriptionOptions(
            DurableName: $"andy.containers.test.{Guid.NewGuid():N}");

        using var cts = new CancellationTokenSource(MessageTimeout);
        IncomingMessage? received = null;

        await foreach (var msg in bus.SubscribeAsync(subject, options, cts.Token))
        {
            received = msg;
            await msg.AckAsync(cts.Token);
            break;
        }

        received.Should().NotBeNull();
    }
}
