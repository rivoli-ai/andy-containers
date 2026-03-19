using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class ContainerProvisioningWorkerTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly ContainerProvisioningQueue _queue;
    private readonly Mock<IInfrastructureProviderFactory> _mockFactory;
    private readonly Mock<IInfrastructureProvider> _mockInfraProvider;
    private readonly Mock<ISshKeyService> _mockSshKeyService;
    private readonly Mock<ISshProvisioningService> _mockSshProvisioning;
    private readonly ContainerProvisioningWorker _worker;
    private readonly ServiceProvider _serviceProvider;

    private const string TestUserId = "test-user";

    public ContainerProvisioningWorkerTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _queue = new ContainerProvisioningQueue();
        _mockFactory = new Mock<IInfrastructureProviderFactory>();
        _mockInfraProvider = new Mock<IInfrastructureProvider>();
        _mockSshKeyService = new Mock<ISshKeyService>();
        _mockSshProvisioning = new Mock<ISshProvisioningService>();

        _mockFactory.Setup(f => f.GetProvider(It.IsAny<InfrastructureProvider>()))
            .Returns(_mockInfraProvider.Object);

        // Build a real ServiceProvider for DI resolution in the worker
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton<ContainersDbContext>(_ => _db);
        services.AddSingleton<ISshKeyService>(_mockSshKeyService.Object);
        services.AddSingleton<ISshProvisioningService>(_mockSshProvisioning.Object);
        _serviceProvider = services.BuildServiceProvider();

        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        var mockScope = new Mock<IServiceScope>();
        var mockAsyncScope = new Mock<IAsyncDisposable>();

        // Wire up scope factory to return our service provider
        mockScope.Setup(s => s.ServiceProvider).Returns(_serviceProvider);
        mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        // Use a wrapper that returns our DB context from scope
        var scopeFactory = new TestServiceScopeFactory(_serviceProvider);

        _worker = new ContainerProvisioningWorker(
            _queue, scopeFactory, _mockFactory.Object,
            new Mock<ILogger<ContainerProvisioningWorker>>().Object);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _db.Dispose();
    }

    private async Task<(ContainerTemplate template, InfrastructureProvider provider)> SeedTemplateAndProvider(
        string? sshConfiguration = null)
    {
        var template = new ContainerTemplate
        {
            Code = "test-template",
            Name = "Test Template",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            SshConfiguration = sshConfiguration
        };
        var provider = new InfrastructureProvider
        {
            Code = "test-provider",
            Name = "Test Provider",
            Type = ProviderType.Docker,
            IsEnabled = true
        };
        _db.Templates.Add(template);
        _db.Providers.Add(provider);
        await _db.SaveChangesAsync();
        return (template, provider);
    }

    private async Task<Container> SeedContainer(Guid templateId, Guid providerId, bool sshEnabled = false)
    {
        var container = new Container
        {
            Name = "test-container",
            OwnerId = TestUserId,
            TemplateId = templateId,
            ProviderId = providerId,
            Status = ContainerStatus.Pending,
            SshEnabled = sshEnabled
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();
        return container;
    }

    private ContainerProvisionJob CreateJob(Container container, InfrastructureProvider provider,
        string baseImage = "ubuntu:24.04", bool sshEnabled = false, string[]? sshPublicKeys = null)
    {
        return new ContainerProvisionJob(
            ContainerId: container.Id,
            ProviderId: provider.Id,
            ProviderCode: provider.Code,
            TemplateBaseImage: baseImage,
            ContainerName: container.Name,
            OwnerId: container.OwnerId,
            Resources: null,
            Gpu: null,
            SshEnabled: sshEnabled,
            SshPublicKeys: sshPublicKeys);
    }

    private async Task ProcessSingleJob(ContainerProvisionJob job)
    {
        // Enqueue and start the worker, then stop it after one job
        await _queue.EnqueueAsync(job);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Start the worker — it will process one job then we cancel
        var workerTask = _worker.StartAsync(cts.Token);

        // Wait a bit for processing, then stop
        await Task.Delay(500, cts.Token);
        await _worker.StopAsync(cts.Token);
    }

    [Fact]
    public async Task ProcessJob_SshEnabled_CallsGenerateSetupScript()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = await SeedContainer(template.Id, provider.Id, sshEnabled: true);
        var job = CreateJob(container, provider, sshEnabled: true);

        var registeredKeys = new List<UserSshKey>
        {
            new() { UserId = TestUserId, Label = "Laptop", PublicKey = "ssh-ed25519 AAAA1", Fingerprint = "SHA256:a", KeyType = "ed25519" }
        };
        _mockSshKeyService.Setup(s => s.ListKeysAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(registeredKeys);
        _mockSshProvisioning.Setup(s => s.GenerateSetupScript(It.IsAny<SshConfig>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns("#!/bin/bash\necho setup");
        _mockInfraProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });
        _mockInfraProvider.Setup(p => p.ExecAsync("ext-1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        await ProcessSingleJob(job);

        _mockSshProvisioning.Verify(s => s.GenerateSetupScript(
            It.Is<SshConfig>(c => c.Enabled),
            It.Is<IReadOnlyList<string>>(keys => keys.Contains("ssh-ed25519 AAAA1"))),
            Times.Once);
    }

    [Fact]
    public async Task ProcessJob_SshEnabled_FetchesUserRegisteredKeys()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = await SeedContainer(template.Id, provider.Id, sshEnabled: true);
        var job = CreateJob(container, provider, sshEnabled: true);

        _mockSshKeyService.Setup(s => s.ListKeysAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserSshKey>());
        _mockSshProvisioning.Setup(s => s.GenerateSetupScript(It.IsAny<SshConfig>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns("#!/bin/bash\necho setup");
        _mockInfraProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });
        _mockInfraProvider.Setup(p => p.ExecAsync("ext-1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        await ProcessSingleJob(job);

        _mockSshKeyService.Verify(s => s.ListKeysAsync(TestUserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessJob_SshEnabled_MergesOneTimeKeysWithRegistered()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = await SeedContainer(template.Id, provider.Id, sshEnabled: true);
        var oneTimeKeys = new[] { "ssh-rsa BBBB1 oneoff@host" };
        var job = CreateJob(container, provider, sshEnabled: true, sshPublicKeys: oneTimeKeys);

        var registeredKeys = new List<UserSshKey>
        {
            new() { UserId = TestUserId, Label = "Laptop", PublicKey = "ssh-ed25519 AAAA1", Fingerprint = "SHA256:a", KeyType = "ed25519" }
        };
        _mockSshKeyService.Setup(s => s.ListKeysAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(registeredKeys);
        _mockSshProvisioning.Setup(s => s.GenerateSetupScript(It.IsAny<SshConfig>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns("#!/bin/bash\necho setup");
        _mockInfraProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });
        _mockInfraProvider.Setup(p => p.ExecAsync("ext-1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        await ProcessSingleJob(job);

        _mockSshProvisioning.Verify(s => s.GenerateSetupScript(
            It.IsAny<SshConfig>(),
            It.Is<IReadOnlyList<string>>(keys => keys.Count == 2
                && keys.Contains("ssh-ed25519 AAAA1")
                && keys.Contains("ssh-rsa BBBB1 oneoff@host"))),
            Times.Once);
    }

    [Fact]
    public async Task ProcessJob_SshEnabled_CallsExecAsyncWithScript()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = await SeedContainer(template.Id, provider.Id, sshEnabled: true);
        var job = CreateJob(container, provider, sshEnabled: true);

        _mockSshKeyService.Setup(s => s.ListKeysAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserSshKey>());
        _mockSshProvisioning.Setup(s => s.GenerateSetupScript(It.IsAny<SshConfig>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns("#!/bin/bash\nsetup-ssh");
        _mockInfraProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });
        _mockInfraProvider.Setup(p => p.ExecAsync("ext-1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        await ProcessSingleJob(job);

        _mockInfraProvider.Verify(p => p.ExecAsync("ext-1", "#!/bin/bash\nsetup-ssh", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessJob_SshEnabled_LogsContainerEvent()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = await SeedContainer(template.Id, provider.Id, sshEnabled: true);
        var job = CreateJob(container, provider, sshEnabled: true);

        _mockSshKeyService.Setup(s => s.ListKeysAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserSshKey>());
        _mockSshProvisioning.Setup(s => s.GenerateSetupScript(It.IsAny<SshConfig>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns("#!/bin/bash\necho setup");
        _mockInfraProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });
        _mockInfraProvider.Setup(p => p.ExecAsync("ext-1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        await ProcessSingleJob(job);

        var events = await _db.Events.Where(e => e.ContainerId == container.Id).ToListAsync();
        events.Should().Contain(e => e.Details != null && e.Details.Contains("ssh_provisioned"));
    }

    [Fact]
    public async Task ProcessJob_SshSetupFails_ContainerStillRunning()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = await SeedContainer(template.Id, provider.Id, sshEnabled: true);
        var job = CreateJob(container, provider, sshEnabled: true);

        _mockSshKeyService.Setup(s => s.ListKeysAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("SSH service unavailable"));
        _mockInfraProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        await ProcessSingleJob(job);

        var updated = await _db.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Running);

        // Should have logged an error event for SSH failure
        var events = await _db.Events.Where(e => e.ContainerId == container.Id).ToListAsync();
        events.Should().Contain(e => e.Details != null && e.Details.Contains("ssh_setup_failed"));
    }

    [Fact]
    public async Task ProcessJob_SshNotEnabled_SkipsSshSetup()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = await SeedContainer(template.Id, provider.Id, sshEnabled: false);
        var job = CreateJob(container, provider, sshEnabled: false);

        _mockInfraProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        await ProcessSingleJob(job);

        _mockSshKeyService.Verify(s => s.ListKeysAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockSshProvisioning.Verify(s => s.GenerateSetupScript(It.IsAny<SshConfig>(), It.IsAny<IReadOnlyList<string>>()), Times.Never);
    }
}

internal class TestServiceScopeFactory : IServiceScopeFactory
{
    private readonly IServiceProvider _serviceProvider;

    public TestServiceScopeFactory(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

    public IServiceScope CreateScope() => new TestServiceScope(_serviceProvider);
}

internal class TestServiceScope : IServiceScope, IAsyncDisposable
{
    public IServiceProvider ServiceProvider { get; }

    public TestServiceScope(IServiceProvider serviceProvider) => ServiceProvider = serviceProvider;

    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
