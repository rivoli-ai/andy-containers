using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class ContainerProvisioningWorkerTests : IDisposable
{
    private readonly string _dbName;
    private readonly ContainerProvisioningQueue _queue;
    private readonly Mock<IInfrastructureProviderFactory> _mockProviderFactory;
    private readonly Mock<IInfrastructureProvider> _mockProvider;
    private readonly Mock<IGitCloneService> _mockGitCloneService;
    private readonly ServiceProvider _serviceProvider;

    public ContainerProvisioningWorkerTests()
    {
        _dbName = Guid.NewGuid().ToString();
        _queue = new ContainerProvisioningQueue();
        _mockProviderFactory = new Mock<IInfrastructureProviderFactory>();
        _mockProvider = new Mock<IInfrastructureProvider>();
        _mockProviderFactory.Setup(f => f.GetProvider(It.IsAny<InfrastructureProvider>())).Returns(_mockProvider.Object);
        _mockGitCloneService = new Mock<IGitCloneService>();

        var services = new ServiceCollection();
        services.AddDbContext<ContainersDbContext>(options =>
            options.UseInMemoryDatabase(_dbName));
        services.AddScoped<IGitCloneService>(_ => _mockGitCloneService.Object);
        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose() => _serviceProvider.Dispose();

    private ContainersDbContext CreateDb() => InMemoryDbHelper.CreateContext(_dbName);

    private ContainerProvisioningWorker CreateWorker()
    {
        return new ContainerProvisioningWorker(
            _queue,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _mockProviderFactory.Object,
            NullLogger<ContainerProvisioningWorker>.Instance);
    }

    private (Container container, InfrastructureProvider provider) SeedContainerAndProvider(ContainersDbContext db)
    {
        var provider = new InfrastructureProvider
        {
            Code = "docker-local",
            Name = "Local Docker",
            Type = ProviderType.Docker
        };
        var template = new ContainerTemplate
        {
            Code = "test",
            Name = "Test",
            Version = "1.0",
            BaseImage = "ubuntu:24.04"
        };
        var container = new Container
        {
            Name = "test-container",
            OwnerId = "user1",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            Status = ContainerStatus.Pending
        };
        db.Providers.Add(provider);
        db.Templates.Add(template);
        db.Containers.Add(container);
        db.SaveChanges();
        return (container, provider);
    }

    [Fact]
    public async Task ProcessJob_SuccessfulProvision_ShouldSetRunning()
    {
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult
            {
                ExternalId = "ext-123",
                Status = ContainerStatus.Running,
                ConnectionInfo = new ConnectionInfo
                {
                    IdeEndpoint = "https://ide.test:8080",
                    VncEndpoint = "https://vnc.test:6080"
                }
            });

        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null);

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Start worker, let it process one job, then cancel
        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500); // give it time to process
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        // Verify
        using var verifyDb = CreateDb();
        var updated = await verifyDb.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Running);
        updated.ExternalId.Should().Be("ext-123");
        updated.IdeEndpoint.Should().Be("https://ide.test:8080");
        updated.VncEndpoint.Should().Be("https://vnc.test:6080");
        updated.StartedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessJob_ProviderThrows_ShouldMarkFailed()
    {
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Docker daemon not running"));

        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null);

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        using var verifyDb = CreateDb();
        var updated = await verifyDb.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Failed);

        var events = verifyDb.Events.Where(e => e.ContainerId == container.Id).ToList();
        events.Should().Contain(e => e.EventType == ContainerEventType.Failed);
    }

    [Fact]
    public async Task ProcessJob_ContainerNotFound_ShouldSkip()
    {
        using var db = CreateDb();
        var provider = new InfrastructureProvider { Code = "docker", Name = "Docker", Type = ProviderType.Docker };
        db.Providers.Add(provider);
        db.SaveChanges();

        var job = new ContainerProvisionJob(
            Guid.NewGuid(), provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null);

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        // Should not throw, just skip
        _mockProvider.Verify(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessJob_WithGitRepos_ShouldCloneAfterProvisioning()
    {
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null, HasGitRepositories: true);

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        _mockGitCloneService.Verify(s => s.CloneRepositoriesAsync(container.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessJob_GitCloneFails_ShouldNotFailContainer()
    {
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        _mockGitCloneService.Setup(s => s.CloneRepositoriesAsync(container.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Clone failed"));

        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null, HasGitRepositories: true);

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        // Container should still be Running despite git clone failure
        using var verifyDb = CreateDb();
        var updated = await verifyDb.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Running);
    }

    [Fact]
    public async Task ProcessJob_NoGitRepos_ShouldNotCallClone()
    {
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null, HasGitRepositories: false);

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        _mockGitCloneService.Verify(s => s.CloneRepositoriesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecoverStuckContainers_ShouldMarkOldCreatingAsFailed()
    {
        using var db = CreateDb();
        var provider = new InfrastructureProvider { Code = "docker", Name = "Docker", Type = ProviderType.Docker };
        var template = new ContainerTemplate { Code = "t", Name = "T", Version = "1.0", BaseImage = "img" };

        var stuckContainer = new Container
        {
            Name = "stuck",
            OwnerId = "user1",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            Status = ContainerStatus.Creating,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10) // well past the 2-minute threshold
        };
        var recentContainer = new Container
        {
            Name = "recent",
            OwnerId = "user1",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            Status = ContainerStatus.Creating,
            CreatedAt = DateTime.UtcNow // just created, should NOT be recovered
        };

        db.Providers.Add(provider);
        db.Templates.Add(template);
        db.Containers.AddRange(stuckContainer, recentContainer);
        db.SaveChanges();

        // Start worker with empty queue — it will run recovery then wait for jobs
        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        using var verifyDb = CreateDb();
        var stuck = await verifyDb.Containers.FindAsync(stuckContainer.Id);
        stuck!.Status.Should().Be(ContainerStatus.Failed);

        var recent = await verifyDb.Containers.FindAsync(recentContainer.Id);
        recent!.Status.Should().Be(ContainerStatus.Creating); // should NOT be recovered
    }

    [Fact]
    public async Task ProcessJob_WithConnectionInfo_ShouldPersistNetworkConfig()
    {
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult
            {
                ExternalId = "ext-1",
                Status = ContainerStatus.Running,
                ConnectionInfo = new ConnectionInfo
                {
                    IdeEndpoint = "https://ide:8080",
                    VncEndpoint = "https://vnc:6080",
                    IpAddress = "10.0.0.5"
                }
            });

        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null);

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        using var verifyDb = CreateDb();
        var updated = await verifyDb.Containers.FindAsync(container.Id);
        updated!.NetworkConfig.Should().NotBeNullOrEmpty();
        updated.NetworkConfig.Should().Contain("10.0.0.5");
    }

    [Fact]
    public async Task ProcessJob_ShouldCreateStartedEvent()
    {
        using var db = CreateDb();
        var (container, provider) = SeedContainerAndProvider(db);

        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        var job = new ContainerProvisionJob(
            container.Id, provider.Id, provider.Code,
            "ubuntu:24.04", "test-container", "user1",
            null, null);

        await _queue.EnqueueAsync(job);

        var worker = CreateWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        try { await workerTask; } catch (OperationCanceledException) { }

        using var verifyDb = CreateDb();
        var events = verifyDb.Events.Where(e => e.ContainerId == container.Id).ToList();
        events.Should().Contain(e => e.EventType == ContainerEventType.Started);
        events.First(e => e.EventType == ContainerEventType.Started).SubjectId.Should().Be("user1");
    }
}
