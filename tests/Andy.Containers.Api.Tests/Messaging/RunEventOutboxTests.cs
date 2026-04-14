// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Messaging;
using Andy.Containers.Messaging.Events;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Containers.Api.Tests.Messaging;

public class RunEventOutboxTests
{
    [Fact]
    public async Task AppendRunEvent_WritesRowWithExpectedSubjectAndPayload()
    {
        using var db = InMemoryDbHelper.CreateContext();

        var storyId = Guid.NewGuid();
        var container = new Container
        {
            Id = Guid.NewGuid(),
            Name = "test-container",
            OwnerId = "tester",
            StoryId = storyId,
            Status = ContainerStatus.Stopped
        };

        db.AppendRunEvent(container, RunEventKind.Finished, exitCode: 0, durationSeconds: 42.5);
        await db.SaveChangesAsync();

        var entry = await db.OutboxEntries.SingleAsync();
        entry.Subject.Should().Be($"andy.containers.events.run.{container.Id}.finished");
        entry.PublishedAt.Should().BeNull();
        entry.CorrelationId.Should().Be(storyId);
        entry.Generation.Should().Be(0);

        using var doc = JsonDocument.Parse(entry.PayloadJson);
        var root = doc.RootElement;
        root.GetProperty("run_id").GetString().Should().Be(container.Id.ToString());
        root.GetProperty("story_id").GetString().Should().Be(storyId.ToString());
        root.GetProperty("status").GetString().Should().Be("Stopped");
        root.GetProperty("exit_code").GetInt32().Should().Be(0);
        root.GetProperty("duration_seconds").GetDouble().Should().Be(42.5);
        root.GetProperty("schema_version").GetInt32().Should().Be(RunEventPayload.SchemaVersion);
    }

    [Fact]
    public async Task AppendRunEvent_WithoutStoryId_OmitsStoryIdAndCorrelatesToRunId()
    {
        using var db = InMemoryDbHelper.CreateContext();

        var container = new Container
        {
            Id = Guid.NewGuid(),
            Name = "no-story",
            OwnerId = "tester",
            StoryId = null,
            Status = ContainerStatus.Failed
        };

        db.AppendRunEvent(container, RunEventKind.Failed);
        await db.SaveChangesAsync();

        var entry = await db.OutboxEntries.SingleAsync();
        entry.Subject.Should().Be($"andy.containers.events.run.{container.Id}.failed");
        entry.CorrelationId.Should().Be(container.Id);

        using var doc = JsonDocument.Parse(entry.PayloadJson);
        var root = doc.RootElement;
        root.TryGetProperty("story_id", out _).Should().BeFalse(
            "story_id should be omitted when null (EventJson ignores nulls on write)");
        root.GetProperty("status").GetString().Should().Be("Failed");
    }

    [Theory]
    [InlineData(RunEventKind.Finished, "finished")]
    [InlineData(RunEventKind.Failed, "failed")]
    [InlineData(RunEventKind.Cancelled, "cancelled")]
    public async Task AppendRunEvent_SubjectKindMatchesEnum(RunEventKind kind, string expectedSuffix)
    {
        using var db = InMemoryDbHelper.CreateContext();
        var container = new Container
        {
            Id = Guid.NewGuid(),
            Name = "x",
            OwnerId = "t",
            Status = ContainerStatus.Destroyed
        };

        db.AppendRunEvent(container, kind);
        await db.SaveChangesAsync();

        var entry = await db.OutboxEntries.SingleAsync();
        entry.Subject.Should().EndWith($".{expectedSuffix}");
    }
}
