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
        root.GetProperty("schema_version").GetInt32().Should().Be(3,
            "DocsRef on RunOutputArtifact bumped the wire shape to v3 (#320)");

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

    // ----- rivoli-ai/andy-containers#320 DocsRef coverage -----

    [Fact]
    public void RunEventPayload_WithDocsRef_RoundTripsThroughEventJson()
    {
        // After #320 each RunOutputArtifact carries an optional DocsRef.
        // Serialise + deserialise asserts both that the payload reaches
        // consumers with DocsRef intact AND that the JSON property name
        // is snake_cased to `docs_ref` per EventJson's policy.
        var docDocId = Guid.NewGuid();
        var docLinkId = Guid.NewGuid();
        var payload = new RunEventPayload(
            RunId: Guid.NewGuid(),
            StoryId: null,
            Status: "Succeeded",
            ExitCode: 0,
            DurationSeconds: 2.0,
            OutputArtifacts: new[]
            {
                new RunOutputArtifact(
                    Name: "report.pdf",
                    RelativePath: "report.pdf",
                    SizeBytes: 12345,
                    Sha256: new string('a', 64),
                    ContentType: "application/pdf",
                    DocsRef: new DocsRef(docDocId, docLinkId)),
            });

        var json = JsonSerializer.Serialize(payload,
            Andy.Containers.Messaging.EventJson.Options);

        // Wire-form: snake_case property, nested object with snake_case keys.
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.GetProperty("output_artifacts");
        arr.GetArrayLength().Should().Be(1);
        var artifact = arr[0];
        artifact.TryGetProperty("docs_ref", out var docsRefEl).Should().BeTrue(
            "DocsRef must serialise as snake_case `docs_ref` for consumer compat");
        docsRefEl.GetProperty("document_id").GetString().Should().Be(docDocId.ToString());
        docsRefEl.GetProperty("link_id").GetString().Should().Be(docLinkId.ToString());

        // Round-trip parses back into the typed record.
        var back = JsonSerializer.Deserialize<RunEventPayload>(json,
            Andy.Containers.Messaging.EventJson.Options);
        back.Should().NotBeNull();
        back!.OutputArtifacts!.Single().DocsRef.Should().NotBeNull();
        back.OutputArtifacts!.Single().DocsRef!.DocumentId.Should().Be(docDocId);
        back.OutputArtifacts!.Single().DocsRef!.LinkId.Should().Be(docLinkId);
    }

    [Fact]
    public void RunEventPayload_WithNullDocsRef_OmitsFieldFromWire()
    {
        // Metadata-only fallback (andy-docs down / not configured) leaves
        // DocsRef null. The JSON wire shape must OMIT the property so v2
        // consumers see exactly the v2 payload they expect — no surprise
        // null on a field they don't know about.
        var payload = new RunEventPayload(
            RunId: Guid.NewGuid(),
            StoryId: null,
            Status: "Succeeded",
            ExitCode: 0,
            DurationSeconds: 1.0,
            OutputArtifacts: new[]
            {
                new RunOutputArtifact("a.txt", "a.txt", 3, new string('a', 64), "text/plain"),
            });

        var json = JsonSerializer.Serialize(payload,
            Andy.Containers.Messaging.EventJson.Options);

        using var doc = JsonDocument.Parse(json);
        var artifact = doc.RootElement.GetProperty("output_artifacts")[0];
        artifact.TryGetProperty("docs_ref", out _).Should().BeFalse(
            "EventJson omits null properties on write — pre-v3 consumers see no docs_ref key");
    }

    [Fact]
    public async Task AppendAgentRunEvent_WithDocsRefPopulated_RoundTripsThroughOutboxAndPersists()
    {
        // Integration: write through AppendAgentRunEvent + the EF
        // JSON-column converter on Run.OutputArtifacts, then read back
        // and verify DocsRef survives both the outbox payload AND the
        // database row. Together these prove the v3 wire shape is
        // wired end-to-end (collector → outbox → entity → consumer).
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

        var docDocId = Guid.NewGuid();
        var docLinkId = Guid.NewGuid();
        var artifacts = new List<RunOutputArtifact>
        {
            new("report.pdf", "report.pdf", 12345, new string('a', 64), "application/pdf",
                DocsRef: new DocsRef(docDocId, docLinkId)),
        };

        db.AppendAgentRunEvent(run, RunEventKind.Finished,
            exitCode: 0, durationSeconds: 4.2, outputArtifacts: artifacts);
        await db.SaveChangesAsync();

        // Outbox payload carries the DocsRef.
        var entry = await db.OutboxEntries.SingleAsync();
        using var doc = JsonDocument.Parse(entry.PayloadJson);
        var arr = doc.RootElement.GetProperty("output_artifacts");
        arr[0].GetProperty("docs_ref").GetProperty("document_id").GetString()
            .Should().Be(docDocId.ToString());
        arr[0].GetProperty("docs_ref").GetProperty("link_id").GetString()
            .Should().Be(docLinkId.ToString());

        // Schema version reflects the v3 bump.
        doc.RootElement.GetProperty("schema_version").GetInt32().Should().Be(3);

        // Persisted Run row also carries the DocsRef (through the
        // EF JSON converter on Run.OutputArtifacts).
        var persisted = await db.Runs.FindAsync(run.Id);
        persisted!.OutputArtifacts.Should().NotBeNull();
        persisted.OutputArtifacts!.Single().DocsRef.Should().NotBeNull();
        persisted.OutputArtifacts!.Single().DocsRef!.DocumentId.Should().Be(docDocId);
        persisted.OutputArtifacts!.Single().DocsRef!.LinkId.Should().Be(docLinkId);
    }
}
