// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Messaging;
using Andy.Containers.Messaging;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Messaging;

/// <summary>
/// SM.2.6 (rivoli-ai/conductor#2008). Unit tests for
/// <see cref="RunEventOutbox.AppendProvisioningAbortedEvent"/>: verifies that
/// every abort reason produces a correctly-formed outbox row with the expected
/// subject, payload, and correlationId.
/// </summary>
public class ProvisioningAbortOutboxTests : IDisposable
{
    private readonly Andy.Containers.Infrastructure.Data.ContainersDbContext _db;

    public ProvisioningAbortOutboxTests()
    {
        _db = InMemoryDbHelper.CreateContext();
    }

    public void Dispose() => _db.Dispose();

    private Container MakeContainer(Guid? storyId = null)
    {
        var c = new Container
        {
            Id = Guid.NewGuid(),
            Name = "test",
            OwnerId = "user-1",
            StoryId = storyId,
        };
        _db.Containers.Add(c);
        return c;
    }

    [Theory]
    [InlineData(ProvisioningAbortReason.QuotaDenied,       "quota_denied")]
    [InlineData(ProvisioningAbortReason.ImageNotFound,     "image_not_found")]
    [InlineData(ProvisioningAbortReason.EngineUnavailable, "engine_unavailable")]
    [InlineData(ProvisioningAbortReason.Cancelled,         "cancelled")]
    [InlineData(ProvisioningAbortReason.Timeout,           "timeout")]
    [InlineData(ProvisioningAbortReason.Unknown,           "unknown")]
    public async Task AppendProvisioningAbortedEvent_AllReasons_EmitCorrectPayload(
        ProvisioningAbortReason reason, string expectedWire)
    {
        var container = MakeContainer();
        var correlationId = container.Id;

        _db.AppendProvisioningAbortedEvent(container, reason, "test detail", correlationId);
        await _db.SaveChangesAsync();

        var entries = _db.OutboxEntries.ToList();
        entries.Should().HaveCount(1);

        var entry = entries[0];
        entry.Subject.Should().Be(
            $"andy.containers.events.container.{container.Id}.provisioning_aborted");
        entry.CorrelationId.Should().Be(correlationId);

        var payload = JsonSerializer.Deserialize<ContainerProvisioningAbortedEvent>(
            entry.PayloadJson, EventJson.Options);
        payload.Should().NotBeNull();
        payload!.ContainerId.Should().Be(container.Id);
        payload.Reason.Should().Be(expectedWire);
        payload.Detail.Should().Be("test detail");
        payload.CorrelationId.Should().Be(correlationId);
    }

    [Fact]
    public async Task AppendProvisioningAbortedEvent_WithStoryId_UsesStoryIdAsCorrelation()
    {
        var storyId = Guid.NewGuid();
        var container = MakeContainer(storyId: storyId);
        var correlationId = storyId; // caller passes storyId as correlationId

        _db.AppendProvisioningAbortedEvent(container, ProvisioningAbortReason.QuotaDenied,
            "quota hit", correlationId);
        await _db.SaveChangesAsync();

        var entry = _db.OutboxEntries.Single();
        entry.CorrelationId.Should().Be(storyId);

        var payload = JsonSerializer.Deserialize<ContainerProvisioningAbortedEvent>(
            entry.PayloadJson, EventJson.Options);
        payload!.CorrelationId.Should().Be(storyId);
    }

    [Fact]
    public async Task AppendProvisioningAbortedEvent_PayloadType_MatchesFullTypeName()
    {
        var container = MakeContainer();
        _db.AppendProvisioningAbortedEvent(container, ProvisioningAbortReason.Unknown, "test", container.Id);
        await _db.SaveChangesAsync();

        var entry = _db.OutboxEntries.Single();
        entry.PayloadType.Should().Be(
            typeof(ContainerProvisioningAbortedEvent).FullName);
    }
}
