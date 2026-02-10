using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class InfrastructureRoutingServiceTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly InfrastructureRoutingService _service;

    public InfrastructureRoutingServiceTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _service = new InfrastructureRoutingService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private ContainerSpec CreateSpec(string name = "test", GpuSpec? gpu = null) => new()
    {
        ImageReference = "ubuntu:24.04",
        Name = name,
        Gpu = gpu
    };

    [Fact]
    public async Task SelectProvider_NoProviders_ShouldThrowInvalidOperation()
    {
        var spec = CreateSpec();

        var act = () => _service.SelectProviderAsync(spec, null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No available infrastructure provider*");
    }

    [Fact]
    public async Task SelectProvider_SingleEnabledProvider_ShouldReturnIt()
    {
        var provider = new InfrastructureProvider { Code = "docker", Name = "Docker", Type = ProviderType.Docker, IsEnabled = true };
        _db.Providers.Add(provider);
        await _db.SaveChangesAsync();

        var result = await _service.SelectProviderAsync(CreateSpec(), null, CancellationToken.None);

        result.Id.Should().Be(provider.Id);
        result.Code.Should().Be("docker");
    }

    [Fact]
    public async Task SelectProvider_DisabledProviders_ShouldNotBeSelected()
    {
        _db.Providers.Add(new InfrastructureProvider { Code = "disabled", Name = "Disabled", Type = ProviderType.Docker, IsEnabled = false });
        await _db.SaveChangesAsync();

        var act = () => _service.SelectProviderAsync(CreateSpec(), null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SelectProvider_WithPreferredProviderId_ShouldReturnPreferred()
    {
        var provider1 = new InfrastructureProvider { Code = "p1", Name = "P1", Type = ProviderType.Docker, IsEnabled = true };
        var provider2 = new InfrastructureProvider { Code = "p2", Name = "P2", Type = ProviderType.AppleContainer, IsEnabled = true };
        _db.Providers.AddRange(provider1, provider2);
        await _db.SaveChangesAsync();

        var prefs = new RoutingPreferences { PreferredProviderId = provider2.Id };
        var result = await _service.SelectProviderAsync(CreateSpec(), prefs, CancellationToken.None);

        result.Id.Should().Be(provider2.Id);
    }

    [Fact]
    public async Task SelectProvider_WithPreferredProviderType_ShouldReturnMatchingType()
    {
        var docker = new InfrastructureProvider { Code = "docker", Name = "Docker", Type = ProviderType.Docker, IsEnabled = true };
        var apple = new InfrastructureProvider { Code = "apple", Name = "Apple", Type = ProviderType.AppleContainer, IsEnabled = true };
        _db.Providers.AddRange(docker, apple);
        await _db.SaveChangesAsync();

        var prefs = new RoutingPreferences { PreferredProviderType = ProviderType.Docker };
        var result = await _service.SelectProviderAsync(CreateSpec(), prefs, CancellationToken.None);

        result.Type.Should().Be(ProviderType.Docker);
    }

    [Fact]
    public async Task SelectProvider_HealthyProviderPreferredOverDegraded()
    {
        var degraded = new InfrastructureProvider { Code = "degraded", Name = "Degraded", Type = ProviderType.Docker, IsEnabled = true, HealthStatus = ProviderHealth.Degraded };
        var healthy = new InfrastructureProvider { Code = "healthy", Name = "Healthy", Type = ProviderType.Docker, IsEnabled = true, HealthStatus = ProviderHealth.Healthy };
        _db.Providers.AddRange(degraded, healthy);
        await _db.SaveChangesAsync();

        var result = await _service.SelectProviderAsync(CreateSpec(), null, CancellationToken.None);

        result.Code.Should().Be("healthy");
    }

    [Fact]
    public async Task GetCandidateProviders_GpuRequired_ShouldExcludeNonGpuProviders()
    {
        var noGpu = new InfrastructureProvider { Code = "no-gpu", Name = "No GPU", Type = ProviderType.Docker, IsEnabled = true };
        var withGpu = new InfrastructureProvider
        {
            Code = "with-gpu",
            Name = "With GPU",
            Type = ProviderType.Docker,
            IsEnabled = true,
            Capabilities = "{\"supportsGpu\":true}"
        };
        _db.Providers.AddRange(noGpu, withGpu);
        await _db.SaveChangesAsync();

        var spec = CreateSpec(gpu: new GpuSpec { Required = true });
        var candidates = await _service.GetCandidateProvidersAsync(spec, CancellationToken.None);

        candidates.Should().HaveCount(1);
        candidates[0].Provider.Code.Should().Be("with-gpu");
        candidates[0].MeetsGpuRequirement.Should().BeTrue();
    }

    [Fact]
    public async Task GetCandidateProviders_NoGpuRequired_ShouldReturnAll()
    {
        _db.Providers.AddRange(
            new InfrastructureProvider { Code = "p1", Name = "P1", Type = ProviderType.Docker, IsEnabled = true },
            new InfrastructureProvider { Code = "p2", Name = "P2", Type = ProviderType.AppleContainer, IsEnabled = true }
        );
        await _db.SaveChangesAsync();

        var candidates = await _service.GetCandidateProvidersAsync(CreateSpec(), CancellationToken.None);

        candidates.Should().HaveCount(2);
        candidates.Should().AllSatisfy(c => c.MeetsGpuRequirement.Should().BeTrue());
    }
}
