// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Messaging;
using Andy.Containers.Messaging;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Andy.Containers.Api.Tests.Messaging;

public class OutboxDispatcherTests
{
    [Fact]
    public async Task DrainOnceAsync_PublishesPendingRowsAndMarksThemPublished()
    {
        var (scopeFactory, bus, _) = BuildScope(nameof(DrainOnceAsync_PublishesPendingRowsAndMarksThemPublished));

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Andy.Containers.Infrastructure.Data.ContainersDbContext>();
            db.OutboxEntries.AddRange(
                new OutboxEntry { Id = Guid.NewGuid(), Subject = "andy.containers.events.run.1.finished", PayloadJson = """{"runId":"1"}""" },
                new OutboxEntry { Id = Guid.NewGuid(), Subject = "andy.containers.events.run.2.finished", PayloadJson = """{"runId":"2"}""" });
            await db.SaveChangesAsync();
        }

        var dispatcher = new OutboxDispatcher(
            scopeFactory,
            NullLogger<OutboxDispatcher>.Instance,
            Options.Create(new OutboxDispatcherOptions()));

        var drained = await dispatcher.DrainOnceAsync(CancellationToken.None);

        drained.Should().Be(2);
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Andy.Containers.Infrastructure.Data.ContainersDbContext>();
            var entries = await db.OutboxEntries.ToListAsync();
            entries.Should().AllSatisfy(e => e.PublishedAt.Should().NotBeNull());
        }
        bus.Published.Should().HaveCount(2);
    }

    [Fact]
    public async Task DrainOnceAsync_ReturnsZeroWhenNoPending()
    {
        var (scopeFactory, _, _) = BuildScope(nameof(DrainOnceAsync_ReturnsZeroWhenNoPending));
        var dispatcher = new OutboxDispatcher(
            scopeFactory,
            NullLogger<OutboxDispatcher>.Instance,
            Options.Create(new OutboxDispatcherOptions()));

        var drained = await dispatcher.DrainOnceAsync(CancellationToken.None);

        drained.Should().Be(0);
    }

    [Fact]
    public async Task DrainOnceAsync_ProcessesInFifoOrder()
    {
        var (scopeFactory, bus, _) = BuildScope(nameof(DrainOnceAsync_ProcessesInFifoOrder));

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Andy.Containers.Infrastructure.Data.ContainersDbContext>();
            db.OutboxEntries.AddRange(
                new OutboxEntry { Id = Guid.NewGuid(), Subject = "s", PayloadJson = "{}", CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-10) },
                new OutboxEntry { Id = Guid.NewGuid(), Subject = "s", PayloadJson = "{}", CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-5) },
                new OutboxEntry { Id = Guid.NewGuid(), Subject = "s", PayloadJson = "{}", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var dispatcher = new OutboxDispatcher(scopeFactory,
            NullLogger<OutboxDispatcher>.Instance,
            Options.Create(new OutboxDispatcherOptions()));

        await dispatcher.DrainOnceAsync(CancellationToken.None);

        // FIFO order: published in ascending CreatedAt
        bus.Published.Select(p => p.CreatedAt).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task DrainOnceAsync_RecordsFailureAndLeavesRowPending()
    {
        var (scopeFactory, bus, _) = BuildScope(nameof(DrainOnceAsync_RecordsFailureAndLeavesRowPending));
        bus.ThrowOnPublish = true;

        Guid entryId;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Andy.Containers.Infrastructure.Data.ContainersDbContext>();
            var entry = new OutboxEntry { Id = Guid.NewGuid(), Subject = "s", PayloadJson = "{}" };
            entryId = entry.Id;
            db.OutboxEntries.Add(entry);
            await db.SaveChangesAsync();
        }

        var dispatcher = new OutboxDispatcher(scopeFactory,
            NullLogger<OutboxDispatcher>.Instance,
            Options.Create(new OutboxDispatcherOptions()));

        await dispatcher.DrainOnceAsync(CancellationToken.None);

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Andy.Containers.Infrastructure.Data.ContainersDbContext>();
            var entry = await db.OutboxEntries.FindAsync(entryId);
            entry.Should().NotBeNull();
            entry!.PublishedAt.Should().BeNull();
            entry.AttemptCount.Should().Be(1);
            entry.LastError.Should().NotBeNull();
        }
    }

    private static (IServiceScopeFactory, RecordingMessageBus, string dbName) BuildScope(string testName)
    {
        var dbName = $"{testName}-{Guid.NewGuid()}";
        var bus = new RecordingMessageBus();

        var services = new ServiceCollection();
        services.AddDbContext<Andy.Containers.Infrastructure.Data.ContainersDbContext>(o =>
            o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IMessageBus>(bus);
        var provider = services.BuildServiceProvider();

        return (provider.GetRequiredService<IServiceScopeFactory>(), bus, dbName);
    }

    private sealed class RecordingMessageBus : IMessageBus
    {
        public List<(string Subject, object Payload, MessageHeaders Headers, DateTimeOffset CreatedAt)> Published { get; } = new();
        public bool ThrowOnPublish { get; set; }

        public Task PublishAsync(string subject, object payload, MessageHeaders headers, CancellationToken ct = default)
        {
            if (ThrowOnPublish) throw new InvalidOperationException("simulated publish failure");
            Published.Add((subject, payload, headers, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<IncomingMessage> SubscribeAsync(string subjectFilter, SubscriptionOptions options, CancellationToken ct = default) =>
            throw new NotSupportedException("Recording bus does not support subscribe.");
    }
}
