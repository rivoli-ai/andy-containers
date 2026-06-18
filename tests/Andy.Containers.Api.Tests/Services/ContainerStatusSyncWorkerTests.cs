using System.Net;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Docker.DotNet;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class ContainerStatusSyncWorkerTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IInfrastructureProviderFactory> _providerFactory;
    private readonly Mock<IInfrastructureProvider> _mockProvider;
    private readonly ContainerStatusSyncWorker _worker;

    public ContainerStatusSyncWorkerTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _providerFactory = new Mock<IInfrastructureProviderFactory>();
        _mockProvider = new Mock<IInfrastructureProvider>();

        var scopeFactory = InMemoryDbHelper.CreateScopeFactory(_db);
        var config = new ConfigurationBuilder().Build();

        _worker = new ContainerStatusSyncWorker(
            scopeFactory,
            _providerFactory.Object,
            new Mock<ILogger<ContainerStatusSyncWorker>>().Object,
            config);
    }

    public void Dispose() => _db.Dispose();

    private InfrastructureProvider CreateProvider()
    {
        var provider = new InfrastructureProvider
        {
            Code = "test-provider",
            Name = "Test",
            Type = ProviderType.Docker,
            IsEnabled = true
        };
        _db.Providers.Add(provider);
        _providerFactory.Setup(f => f.GetProvider(It.Is<InfrastructureProvider>(p => p.Id == provider.Id)))
            .Returns(_mockProvider.Object);
        return provider;
    }

    [Fact]
    public async Task SyncAll_RunningContainerStoppedOnProvider_ShouldUpdateStatus()
    {
        var provider = CreateProvider();
        var container = new Container
        {
            Name = "test1",
            OwnerId = "user1",
            ProviderId = provider.Id,
            ExternalId = "ext-1",
            Status = ContainerStatus.Running
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        _mockProvider.Setup(p => p.GetContainerInfoAsync("ext-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerRuntimeInfo { ExternalId = "ext-1", Status = ContainerStatus.Stopped });

        await _worker.SyncAllAsync(CancellationToken.None);

        var updated = await _db.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Stopped);
        updated.StoppedAt.Should().NotBeNull();
    }

    // rivoli-ai/conductor#2204. A container deleted out-of-band (docker
    // prune / reboot) must NOT be reconciled on the first miss — a
    // transient docker daemon restart looks identical for one or two
    // probes.
    [Fact]
    public async Task SyncAll_TransientNotFound_UnderThreshold_DoesNotReconcile()
    {
        var provider = CreateProvider();
        var container = new Container
        {
            Name = "blip",
            OwnerId = "user1",
            ProviderId = provider.Id,
            ExternalId = "ext-blip",
            Status = ContainerStatus.Running
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        _mockProvider.Setup(p => p.GetContainerInfoAsync("ext-blip", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DockerContainerNotFoundException(HttpStatusCode.NotFound,
                """{"message":"No such container: ext-blip"}"""));

        for (var i = 0; i < ContainerStatusSyncWorker.MissingContainerThreshold - 1; i++)
            await _worker.SyncAllAsync(CancellationToken.None);

        var updated = await _db.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Running); // not yet reconciled
        (await _db.Events.AnyAsync(e => e.ContainerId == container.Id)).Should().BeFalse();
        (await _db.OutboxEntries.AnyAsync()).Should().BeFalse();
    }

    // rivoli-ai/conductor#2204. A successful probe resets the consecutive
    // miss counter — only an UNBROKEN streak reconciles.
    [Fact]
    public async Task SyncAll_NotFoundStreakBrokenBySuccess_ResetsCounter()
    {
        var provider = CreateProvider();
        var container = new Container
        {
            Name = "flaky",
            OwnerId = "user1",
            ProviderId = provider.Id,
            ExternalId = "ext-flaky",
            Status = ContainerStatus.Running
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var notFound = new DockerContainerNotFoundException(HttpStatusCode.NotFound,
            """{"message":"No such container: ext-flaky"}""");
        var healthy = new ContainerRuntimeInfo { ExternalId = "ext-flaky", Status = ContainerStatus.Running };

        // miss, miss, hit, miss, miss — never threshold consecutive misses.
        var responses = new Queue<Func<ContainerRuntimeInfo>>(new Func<ContainerRuntimeInfo>[]
        {
            () => throw notFound,
            () => throw notFound,
            () => healthy,
            () => throw notFound,
            () => throw notFound
        });
        _mockProvider.Setup(p => p.GetContainerInfoAsync("ext-flaky", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => responses.Dequeue()());

        for (var i = 0; i < 5; i++)
            await _worker.SyncAllAsync(CancellationToken.None);

        var updated = await _db.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Running);
    }

    // rivoli-ai/conductor#2204. Sustained NotFound reconciles the record
    // to Failed with the machine-readable reason, emits the run.failed
    // outbox event, and removes the container from the polling set —
    // never an infinite retry loop.
    [Fact]
    public async Task SyncAll_SustainedNotFound_ReconcilesToFailed_AndStopsPolling()
    {
        var provider = CreateProvider();
        var container = new Container
        {
            Name = "gone",
            OwnerId = "user1",
            ProviderId = provider.Id,
            ExternalId = "ext-gone",
            Status = ContainerStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        _mockProvider.Setup(p => p.GetContainerInfoAsync("ext-gone", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DockerContainerNotFoundException(HttpStatusCode.NotFound,
                """{"message":"No such container: ext-gone"}"""));

        for (var i = 0; i < ContainerStatusSyncWorker.MissingContainerThreshold; i++)
            await _worker.SyncAllAsync(CancellationToken.None);

        var updated = await _db.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Failed);
        updated.StoppedAt.Should().NotBeNull();

        var lifecycleEvent = await _db.Events.SingleAsync(e => e.ContainerId == container.Id);
        lifecycleEvent.EventType.Should().Be(ContainerEventType.Failed);
        lifecycleEvent.Details.Should().Be(ContainerStatusSyncWorker.MissingContainerReason);

        var outbox = await _db.OutboxEntries.SingleAsync();
        outbox.Subject.Should().Be($"andy.containers.events.run.{container.Id}.failed");

        // The record is now terminal — a further sync cycle must NOT
        // probe the provider for it again.
        await _worker.SyncAllAsync(CancellationToken.None);
        _mockProvider.Verify(p => p.GetContainerInfoAsync("ext-gone", It.IsAny<CancellationToken>()),
            Times.Exactly(ContainerStatusSyncWorker.MissingContainerThreshold));
    }

    // Providers that signal a missing container via InvalidOperationException
    // (e.g. AWS Fargate) take the same reconcile path.
    [Fact]
    public async Task SyncAll_SustainedNotFound_InvalidOperationException_AlsoReconciles()
    {
        var provider = CreateProvider();
        var container = new Container
        {
            Name = "gone-ioe",
            OwnerId = "user1",
            ProviderId = provider.Id,
            ExternalId = "ext-gone-ioe",
            Status = ContainerStatus.Running
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        _mockProvider.Setup(p => p.GetContainerInfoAsync("ext-gone-ioe", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Container not found"));

        for (var i = 0; i < ContainerStatusSyncWorker.MissingContainerThreshold; i++)
            await _worker.SyncAllAsync(CancellationToken.None);

        var updated = await _db.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Failed);
    }

    // rivoli-ai/conductor#2204. A healthy sibling container is untouched
    // while a vanished one reconciles.
    [Fact]
    public async Task SyncAll_HealthyContainer_UntouchedWhileSiblingReconciles()
    {
        var provider = CreateProvider();
        var healthy = new Container
        {
            Name = "healthy",
            OwnerId = "user1",
            ProviderId = provider.Id,
            ExternalId = "ext-healthy",
            Status = ContainerStatus.Running,
            HostIp = "192.168.64.10"
        };
        var missing = new Container
        {
            Name = "missing",
            OwnerId = "user1",
            ProviderId = provider.Id,
            ExternalId = "ext-missing",
            Status = ContainerStatus.Running
        };
        _db.Containers.AddRange(healthy, missing);
        await _db.SaveChangesAsync();

        _mockProvider.Setup(p => p.GetContainerInfoAsync("ext-healthy", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerRuntimeInfo
            {
                ExternalId = "ext-healthy",
                Status = ContainerStatus.Running,
                IpAddress = "192.168.64.10"
            });
        _mockProvider.Setup(p => p.GetContainerInfoAsync("ext-missing", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DockerContainerNotFoundException(HttpStatusCode.NotFound,
                """{"message":"No such container: ext-missing"}"""));

        for (var i = 0; i < ContainerStatusSyncWorker.MissingContainerThreshold; i++)
            await _worker.SyncAllAsync(CancellationToken.None);

        (await _db.Containers.FindAsync(healthy.Id))!.Status.Should().Be(ContainerStatus.Running);
        (await _db.Containers.FindAsync(missing.Id))!.Status.Should().Be(ContainerStatus.Failed);
        (await _db.Events.AnyAsync(e => e.ContainerId == healthy.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task SyncAll_ShouldUpdateHostIp()
    {
        var provider = CreateProvider();
        var container = new Container
        {
            Name = "ip-test",
            OwnerId = "user1",
            ProviderId = provider.Id,
            ExternalId = "ext-ip",
            Status = ContainerStatus.Running,
            HostIp = null
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        _mockProvider.Setup(p => p.GetContainerInfoAsync("ext-ip", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerRuntimeInfo
            {
                ExternalId = "ext-ip",
                Status = ContainerStatus.Running,
                IpAddress = "192.168.64.100"
            });

        await _worker.SyncAllAsync(CancellationToken.None);

        var updated = await _db.Containers.FindAsync(container.Id);
        updated!.HostIp.Should().Be("192.168.64.100");
    }

    [Fact]
    public async Task SyncAll_NoChangeNeeded_ShouldNotModify()
    {
        var provider = CreateProvider();
        var container = new Container
        {
            Name = "stable",
            OwnerId = "user1",
            ProviderId = provider.Id,
            ExternalId = "ext-stable",
            Status = ContainerStatus.Running,
            HostIp = "192.168.64.50"
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        _mockProvider.Setup(p => p.GetContainerInfoAsync("ext-stable", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerRuntimeInfo
            {
                ExternalId = "ext-stable",
                Status = ContainerStatus.Running,
                IpAddress = "192.168.64.50"
            });

        await _worker.SyncAllAsync(CancellationToken.None);

        var updated = await _db.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Running);
        updated.HostIp.Should().Be("192.168.64.50");
    }

    [Fact]
    public async Task SyncAll_SkipsDestroyedContainers()
    {
        var provider = CreateProvider();
        _db.Containers.Add(new Container
        {
            Name = "destroyed",
            OwnerId = "user1",
            ProviderId = provider.Id,
            ExternalId = "ext-destroyed",
            Status = ContainerStatus.Destroyed
        });
        await _db.SaveChangesAsync();

        await _worker.SyncAllAsync(CancellationToken.None);

        _mockProvider.Verify(p => p.GetContainerInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncAll_ProviderTimeout_ShouldNotCrash()
    {
        var provider = CreateProvider();
        var container = new Container
        {
            Name = "timeout",
            OwnerId = "user1",
            ProviderId = provider.Id,
            ExternalId = "ext-timeout",
            Status = ContainerStatus.Running
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        _mockProvider.Setup(p => p.GetContainerInfoAsync("ext-timeout", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await _worker.SyncAllAsync(CancellationToken.None);

        var updated = await _db.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Running); // unchanged
    }
}
