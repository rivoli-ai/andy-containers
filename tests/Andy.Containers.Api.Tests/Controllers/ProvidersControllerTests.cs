using Andy.Containers.Abstractions;
using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

public class ProvidersControllerTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IInfrastructureProviderFactory> _mockFactory;
    private readonly ProvidersController _controller;

    public ProvidersControllerTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockFactory = new Mock<IInfrastructureProviderFactory>();
        _controller = new ProvidersController(_db, _mockFactory.Object);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task List_ShouldReturnAllProviders()
    {
        _db.Providers.AddRange(
            new InfrastructureProvider { Code = "docker", Name = "Docker", Type = ProviderType.Docker },
            new InfrastructureProvider { Code = "apple", Name = "Apple", Type = ProviderType.AppleContainer }
        );
        await _db.SaveChangesAsync();

        var result = await _controller.List(CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var providers = okResult.Value.Should().BeAssignableTo<List<InfrastructureProvider>>().Subject;
        providers.Should().HaveCount(2);
    }

    [Fact]
    public async Task Get_ExistingProvider_ShouldReturnOk()
    {
        var provider = new InfrastructureProvider { Code = "docker-local", Name = "Local Docker", Type = ProviderType.Docker };
        _db.Providers.Add(provider);
        await _db.SaveChangesAsync();

        var result = await _controller.Get(provider.Id, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<InfrastructureProvider>().Subject;
        returned.Code.Should().Be("docker-local");
    }

    [Fact]
    public async Task Get_NonExistentProvider_ShouldReturnNotFound()
    {
        var result = await _controller.Get(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Create_ShouldPersistAndReturnCreated()
    {
        var provider = new InfrastructureProvider
        {
            Code = "new-provider",
            Name = "New Provider",
            Type = ProviderType.Ssh,
            Region = "westus"
        };

        var result = await _controller.Create(provider, CancellationToken.None);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(201);
        var persisted = await _db.Providers.FindAsync(provider.Id);
        persisted.Should().NotBeNull();
        persisted!.Code.Should().Be("new-provider");
    }

    [Fact]
    public async Task HealthCheck_ExistingProvider_Healthy_ShouldReturnHealthAndCapabilities()
    {
        var provider = new InfrastructureProvider { Code = "docker", Name = "Docker", Type = ProviderType.Docker };
        _db.Providers.Add(provider);
        await _db.SaveChangesAsync();

        var mockInfra = new Mock<IInfrastructureProvider>();
        mockInfra.Setup(i => i.HealthCheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderHealth.Healthy);
        mockInfra.Setup(i => i.GetCapabilitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderCapabilities
            {
                Type = ProviderType.Docker,
                SupportedArchitectures = new[] { "amd64", "arm64" },
                SupportedOperatingSystems = new[] { "linux" },
                SupportsExec = true
            });
        _mockFactory.Setup(f => f.GetProvider(It.IsAny<InfrastructureProvider>())).Returns(mockInfra.Object);

        var result = await _controller.HealthCheck(provider.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var updated = await _db.Providers.FindAsync(provider.Id);
        updated!.HealthStatus.Should().Be(ProviderHealth.Healthy);
        updated.LastHealthCheck.Should().NotBeNull();
    }

    [Fact]
    public async Task HealthCheck_NonExistentProvider_ShouldReturnNotFound()
    {
        var result = await _controller.HealthCheck(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task HealthCheck_WhenProviderThrows_ShouldReturnUnreachable()
    {
        var provider = new InfrastructureProvider { Code = "failing", Name = "Failing", Type = ProviderType.Docker };
        _db.Providers.Add(provider);
        await _db.SaveChangesAsync();

        var mockInfra = new Mock<IInfrastructureProvider>();
        mockInfra.Setup(i => i.HealthCheckAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Connection refused"));
        _mockFactory.Setup(f => f.GetProvider(It.IsAny<InfrastructureProvider>())).Returns(mockInfra.Object);

        var result = await _controller.HealthCheck(provider.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var updated = await _db.Providers.FindAsync(provider.Id);
        updated!.HealthStatus.Should().Be(ProviderHealth.Unreachable);
    }

    [Fact]
    public async Task Delete_ExistingProvider_ShouldRemoveAndReturnNoContent()
    {
        var provider = new InfrastructureProvider { Code = "to-delete", Name = "Delete Me", Type = ProviderType.Docker };
        _db.Providers.Add(provider);
        await _db.SaveChangesAsync();

        var result = await _controller.Delete(provider.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        var found = await _db.Providers.FindAsync(provider.Id);
        found.Should().BeNull();
    }
}
