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
    [InlineData(RunEventKind.Timeout, "timeout")]
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

    [Fact]
    public async Task AppendAgentRunEvent_KeyedOnRunIdNotContainerId()
    {
        // AP6 (rivoli-ai/andy-containers#108). Agent-run events live on
        // andy.containers.events.run.{Run.Id}.<kind> — distinct from the
        // container-lifecycle path that keys on Container.Id.
        using var db = InMemoryDbHelper.CreateContext();
        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = "triage-agent",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            ContainerId = Guid.NewGuid(),
            Status = RunStatus.Succeeded,
        };

        db.AppendAgentRunEvent(run, RunEventKind.Finished, exitCode: 0, durationSeconds: 12.3);
        await db.SaveChangesAsync();

        var entry = await db.OutboxEntries.SingleAsync();
        entry.Subject.Should().Be($"andy.containers.events.run.{run.Id}.finished");
        entry.Subject.Should().NotContain(run.ContainerId!.Value.ToString(),
            "subject must key on Run.Id, not the assigned Container.Id");
        entry.CorrelationId.Should().Be(run.CorrelationId);

        using var doc = JsonDocument.Parse(entry.PayloadJson);
        var root = doc.RootElement;
        root.GetProperty("run_id").GetString().Should().Be(run.Id.ToString());
        root.GetProperty("status").GetString().Should().Be("Succeeded");
        root.GetProperty("exit_code").GetInt32().Should().Be(0);
        root.GetProperty("duration_seconds").GetDouble().Should().Be(12.3);
    }

    [Fact]
    public async Task AppendAgentRunEvent_EmptyCorrelationFallsBackToRunId()
    {
        using var db = InMemoryDbHelper.CreateContext();
        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = "x",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.Empty,
            Status = RunStatus.Failed,
        };

        db.AppendAgentRunEvent(run, RunEventKind.Failed);
        await db.SaveChangesAsync();

        var entry = await db.OutboxEntries.SingleAsync();
        entry.CorrelationId.Should().Be(run.Id);
    }

    // ----- rivoli-ai/andy-containers#316 OutputArtifacts coverage -----

    [Fact]
    public async Task AppendAgentRunEvent_WithOutputArtifacts_PersistsOnRunAndSerialisesToPayload()
    {
        using var db = InMemoryDbHelper.CreateContext();
        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = "triage-agent",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            ContainerId = Guid.NewGuid(),
            Status = RunStatus.Succeeded,
        };
        db.Runs.Add(run);
        await db.SaveChangesAsync();

        var artifacts = new List<RunOutputArtifact>
        {
            new("report.pdf", "report.pdf", 12345, new string('a', 64), "application/pdf"),
            new("data.json", "sub/data.json", 678, new string('b', 64), "application/json"),
        };

        db.AppendAgentRunEvent(run, RunEventKind.Finished,
            exitCode: 0, durationSeconds: 4.2, outputArtifacts: artifacts);
        await db.SaveChangesAsync();

        // Persisted on the Run row.
        var persisted = await db.Runs.FindAsync(run.Id);
        persisted!.OutputArtifacts.Should().NotBeNull();
        persisted.OutputArtifacts!.Should().HaveCount(2);
        persisted.OutputArtifacts.Should().Contain(a =>
            a.RelativePath == "report.pdf" && a.SizeBytes == 12345);

        // Round-trips through the outbox payload as a snake_case array.
        var entry = await db.OutboxEntries.SingleAsync();
        using var doc = JsonDocument.Parse(entry.PayloadJson);
        var root = doc.RootElement;
        root.GetProperty("schema_version").GetInt32().Should().Be(RunEventPayload.SchemaVersion);
        root.GetProperty("schema_version").GetInt32().Should().Be(2,
            "OutputArtifacts shipped in the v2 wire shape");

        var arr = root.GetProperty("output_artifacts");
        arr.GetArrayLength().Should().Be(2);
        arr[0].GetProperty("name").GetString().Should().Be("report.pdf");
        arr[0].GetProperty("relative_path").GetString().Should().Be("report.pdf");
        arr[0].GetProperty("size_bytes").GetInt64().Should().Be(12345);
        arr[0].GetProperty("sha256").GetString().Should().Be(new string('a', 64));
        arr[0].GetProperty("content_type").GetString().Should().Be("application/pdf");
    }

    [Fact]
    public async Task AppendAgentRunEvent_WithoutOutputArtifacts_OmitsFieldFromPayload()
    {
        // Legacy callers (and the headless runner pre-#316 surface)
        // pass no artifacts. The wire payload must omit the field so
        // existing v1 consumers continue to decode without seeing a
        // null where they expect "no field".
        using var db = InMemoryDbHelper.CreateContext();
        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = "x",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Status = RunStatus.Succeeded,
        };
        db.Runs.Add(run);
        await db.SaveChangesAsync();

        db.AppendAgentRunEvent(run, RunEventKind.Finished, exitCode: 0);
        await db.SaveChangesAsync();

        var entry = await db.OutboxEntries.SingleAsync();
        using var doc = JsonDocument.Parse(entry.PayloadJson);
        doc.RootElement.TryGetProperty("output_artifacts", out _).Should().BeFalse(
            "EventJson omits null properties on write");

        var persisted = await db.Runs.FindAsync(run.Id);
        persisted!.OutputArtifacts.Should().BeNull();
    }

    [Fact]
    public async Task AppendAgentRunEvent_EmptyArtifactList_StillSerialisedAsEmptyArray()
    {
        // The collector returns an empty list (not null) when the
        // outputs directory exists but is empty. The payload should
        // carry the empty array — that's semantically different from
        // "no artifact collection ran".
        using var db = InMemoryDbHelper.CreateContext();
        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = "x",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Status = RunStatus.Succeeded,
        };
        db.Runs.Add(run);
        await db.SaveChangesAsync();

        db.AppendAgentRunEvent(run, RunEventKind.Finished,
            exitCode: 0, outputArtifacts: Array.Empty<RunOutputArtifact>());
        await db.SaveChangesAsync();

        var entry = await db.OutboxEntries.SingleAsync();
        using var doc = JsonDocument.Parse(entry.PayloadJson);
        doc.RootElement.GetProperty("output_artifacts").GetArrayLength().Should().Be(0);

        var persisted = await db.Runs.FindAsync(run.Id);
        persisted!.OutputArtifacts.Should().NotBeNull();
        persisted.OutputArtifacts!.Should().BeEmpty();
    }

    [Fact]
    public async Task AppendRunEvent_WithOutputArtifacts_SurfacesOnContainerPayload()
    {
        // The container-lifecycle path also accepts the manifest.
        // Container has no persisted column for it (decided in #316);
        // we only verify the payload carries the array.
        using var db = InMemoryDbHelper.CreateContext();
        var container = new Container
        {
            Id = Guid.NewGuid(),
            Name = "c",
            OwnerId = "u",
            Status = ContainerStatus.Stopped
        };
        var artifacts = new List<RunOutputArtifact>
        {
            new("out.txt", "out.txt", 11, new string('c', 64), "text/plain"),
        };

        db.AppendRunEvent(container, RunEventKind.Finished,
            exitCode: 0, durationSeconds: 1.0, outputArtifacts: artifacts);
        await db.SaveChangesAsync();

        var entry = await db.OutboxEntries.SingleAsync();
        using var doc = JsonDocument.Parse(entry.PayloadJson);
        var arr = doc.RootElement.GetProperty("output_artifacts");
        arr.GetArrayLength().Should().Be(1);
        arr[0].GetProperty("name").GetString().Should().Be("out.txt");
    }

    [Fact]
    public void RunEventPayload_RoundTripsThroughEventJson()
    {
        // Sanity: serialize then deserialize a populated payload with
        // EventJson.Options. Snake-case property names must reach the
        // consumer side cleanly.
        var payload = new RunEventPayload(
            RunId: Guid.NewGuid(),
            StoryId: Guid.NewGuid(),
            Status: "Succeeded",
            ExitCode: 0,
            DurationSeconds: 1.5,
            OutputArtifacts: new[]
            {
                new RunOutputArtifact("r.pdf", "r.pdf", 99, new string('d', 64), "application/pdf"),
            });

        var json = JsonSerializer.Serialize(payload,
            Andy.Containers.Messaging.EventJson.Options);
        var back = JsonSerializer.Deserialize<RunEventPayload>(json,
            Andy.Containers.Messaging.EventJson.Options);

        back.Should().NotBeNull();
        back!.RunId.Should().Be(payload.RunId);
        back.OutputArtifacts.Should().NotBeNull();
        back.OutputArtifacts!.Should().HaveCount(1);
        back.OutputArtifacts![0].Name.Should().Be("r.pdf");
        back.OutputArtifacts![0].Sha256.Should().Be(new string('d', 64));
    }
}
