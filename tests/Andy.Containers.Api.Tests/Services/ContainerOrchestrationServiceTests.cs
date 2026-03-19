using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class ContainerOrchestrationServiceTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IInfrastructureRoutingService> _mockRouting;
    private readonly Mock<IInfrastructureProviderFactory> _mockFactory;
    private readonly Mock<IInfrastructureProvider> _mockInfraProvider;
    private readonly ContainerProvisioningQueue _queue;
    private readonly ContainerOrchestrationService _service;

    public ContainerOrchestrationServiceTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockRouting = new Mock<IInfrastructureRoutingService>();
        _mockFactory = new Mock<IInfrastructureProviderFactory>();
        _mockInfraProvider = new Mock<IInfrastructureProvider>();
        _queue = new ContainerProvisioningQueue();
        var logger = new Mock<ILogger<ContainerOrchestrationService>>();

        _mockFactory.Setup(f => f.GetProvider(It.IsAny<InfrastructureProvider>()))
            .Returns(_mockInfraProvider.Object);

        _service = new ContainerOrchestrationService(_db, _mockRouting.Object, _mockFactory.Object, _queue, logger.Object);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private async Task<(ContainerTemplate template, InfrastructureProvider provider)> SeedTemplateAndProvider()
    {
        var template = new ContainerTemplate
        {
            Code = "test-template",
            Name = "Test Template",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
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

    [Fact]
    public async Task CreateContainer_WithTemplateId_ShouldCreateContainerWithPendingStatus()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        _mockInfraProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "ext-1", Status = ContainerStatus.Running });

        var request = new CreateContainerRequest
        {
            Name = "my-container",
            TemplateId = template.Id,
            ProviderId = provider.Id
        };

        var container = await _service.CreateContainerAsync(request, CancellationToken.None);

        container.Name.Should().Be("my-container");
        container.TemplateId.Should().Be(template.Id);
        container.ProviderId.Should().Be(provider.Id);
        container.Status.Should().Be(ContainerStatus.Pending);
        container.OwnerId.Should().Be("system");
    }

    [Fact]
    public async Task CreateContainer_WithInvalidTemplateId_ShouldThrowArgumentException()
    {
        var request = new CreateContainerRequest
        {
            Name = "bad-container",
            TemplateId = Guid.NewGuid(),
            ProviderId = Guid.NewGuid()
        };

        var act = () => _service.CreateContainerAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Template not found*");
    }

    [Fact]
    public async Task CreateContainer_WithInvalidProviderId_ShouldThrowArgumentException()
    {
        var template = new ContainerTemplate
        {
            Code = "t1",
            Name = "T1",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var request = new CreateContainerRequest
        {
            Name = "bad-container",
            TemplateId = template.Id,
            ProviderId = Guid.NewGuid()
        };

        var act = () => _service.CreateContainerAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Provider not found*");
    }

    [Fact]
    public async Task GetContainer_ExistingId_ShouldReturnContainer()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = new Container
        {
            Name = "existing",
            OwnerId = "user1",
            TemplateId = template.Id,
            ProviderId = provider.Id
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var result = await _service.GetContainerAsync(container.Id, CancellationToken.None);

        result.Name.Should().Be("existing");
        result.Id.Should().Be(container.Id);
    }

    [Fact]
    public async Task GetContainer_NonExistentId_ShouldThrowKeyNotFoundException()
    {
        var act = () => _service.GetContainerAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task StopContainer_NotRunning_ShouldThrowInvalidOperation()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = new Container
        {
            Name = "stopped-container",
            OwnerId = "user1",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            Status = ContainerStatus.Stopped
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        var act = () => _service.StopContainerAsync(container.Id, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Stopped*cannot stop*");
    }

    [Fact]
    public async Task DestroyContainer_WithExternalId_ShouldCallProvider()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = new Container
        {
            Name = "to-destroy",
            OwnerId = "user1",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            ExternalId = "ext-destroy-1",
            Status = ContainerStatus.Running
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        _mockInfraProvider.Setup(p => p.DestroyContainerAsync("ext-destroy-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _service.DestroyContainerAsync(container.Id, CancellationToken.None);

        _mockInfraProvider.Verify(p => p.DestroyContainerAsync("ext-destroy-1", It.IsAny<CancellationToken>()), Times.Once);
        var updated = await _db.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Destroyed);
    }

    [Fact]
    public async Task DestroyContainer_WithoutExternalId_ShouldNotCallProviderButStillDestroy()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var container = new Container
        {
            Name = "pending-destroy",
            OwnerId = "user1",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            ExternalId = null,
            Status = ContainerStatus.Pending
        };
        _db.Containers.Add(container);
        await _db.SaveChangesAsync();

        await _service.DestroyContainerAsync(container.Id, CancellationToken.None);

        _mockInfraProvider.Verify(p => p.DestroyContainerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var updated = await _db.Containers.FindAsync(container.Id);
        updated!.Status.Should().Be(ContainerStatus.Destroyed);
    }

    // === Story 10: SSH auto-provisioning orchestration tests ===

    [Fact]
    public async Task CreateContainer_WithSshEnabled_SetsSshEnabledOnContainer()
    {
        var (template, provider) = await SeedTemplateAndProvider();

        var request = new CreateContainerRequest
        {
            Name = "ssh-container",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            SshEnabled = true
        };

        var container = await _service.CreateContainerAsync(request, CancellationToken.None);

        container.SshEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task CreateContainer_WithSshEnabledTemplate_SetsSshEnabledOnContainer()
    {
        var template = new ContainerTemplate
        {
            Code = "ssh-template",
            Name = "SSH Template",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            SshConfiguration = System.Text.Json.JsonSerializer.Serialize(new SshConfig { Enabled = true })
        };
        var provider = new InfrastructureProvider
        {
            Code = "p1", Name = "P1", Type = ProviderType.Docker, IsEnabled = true
        };
        _db.Templates.Add(template);
        _db.Providers.Add(provider);
        await _db.SaveChangesAsync();

        var request = new CreateContainerRequest
        {
            Name = "template-ssh",
            TemplateId = template.Id,
            ProviderId = provider.Id
        };

        var container = await _service.CreateContainerAsync(request, CancellationToken.None);

        container.SshEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task CreateContainer_WithoutSsh_LeavesSshDisabled()
    {
        var (template, provider) = await SeedTemplateAndProvider();

        var request = new CreateContainerRequest
        {
            Name = "no-ssh",
            TemplateId = template.Id,
            ProviderId = provider.Id
        };

        var container = await _service.CreateContainerAsync(request, CancellationToken.None);

        container.SshEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task CreateContainer_WithSshPublicKeys_EnqueuesJobWithKeys()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var keys = new[] { "ssh-ed25519 AAAA user@host" };

        var request = new CreateContainerRequest
        {
            Name = "ssh-keys-container",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            SshEnabled = true,
            SshPublicKeys = keys
        };

        await _service.CreateContainerAsync(request, CancellationToken.None);

        // Read the enqueued job from the queue
        var job = await _queue.Reader.ReadAsync(CancellationToken.None);
        job.SshEnabled.Should().BeTrue();
        job.SshPublicKeys.Should().BeEquivalentTo(keys);
    }
}
