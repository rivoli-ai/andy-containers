using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
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

    [Fact]
    public async Task SyncAll_ContainerNotFoundOnProvider_ShouldMarkDestroyed()
    {
        var provider = CreateProvider();
        var container = new Container
        {
            Name = "gone",
            OwnerId = "user1",
            ProviderId = provider.Id,
            ExternalId = "ext-gone",
            Status = ContainerStatus.Running
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        _mockProvider.Setup(p => p.GetContainerInfoAsync("ext-gone", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Container not found"));

        await _worker.SyncAllAsync(CancellationToken.None);

        var updated = await _db.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Destroyed);
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
