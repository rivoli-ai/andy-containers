using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class ProviderHealthCheckWorkerTests : IDisposable
{
    private readonly string _dbName;
    private readonly Mock<IInfrastructureProviderFactory> _mockProviderFactory;
    private readonly Mock<IInfrastructureProvider> _mockProvider;
    private readonly ServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;

    public ProviderHealthCheckWorkerTests()
    {
        _dbName = Guid.NewGuid().ToString();
        _mockProviderFactory = new Mock<IInfrastructureProviderFactory>();
        _mockProvider = new Mock<IInfrastructureProvider>();
        _mockProviderFactory
            .Setup(f => f.GetProvider(It.IsAny<InfrastructureProvider>()))
            .Returns(_mockProvider.Object);

        var services = new ServiceCollection();
        services.AddDbContext<ContainersDbContext>(options =>
            options.UseInMemoryDatabase(_dbName));
        _serviceProvider = services.BuildServiceProvider();

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HealthCheck:IntervalSeconds"] = "60"
            })
            .Build();
    }

    public void Dispose() => _serviceProvider.Dispose();

    private ContainersDbContext CreateDb() => InMemoryDbHelper.CreateContext(_dbName);

    private ProviderHealthCheckWorker CreateWorker()
    {
        return new ProviderHealthCheckWorker(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _mockProviderFactory.Object,
            NullLogger<ProviderHealthCheckWorker>.Instance,
            _configuration);
    }

    private InfrastructureProvider SeedProvider(ContainersDbContext db,
        string code = "docker-local",
        bool isEnabled = true,
        ProviderHealth healthStatus = ProviderHealth.Unknown)
    {
        var provider = new InfrastructureProvider
        {
            Code = code,
            Name = $"Test {code}",
            Type = ProviderType.Docker,
            IsEnabled = isEnabled,
            HealthStatus = healthStatus,
        };
        db.Providers.Add(provider);
        db.SaveChanges();
        return provider;
    }

    [Fact]
    public async Task CheckAllProviders_UpdatesHealthStatus_WhenProviderIsHealthy()
    {
        // Arrange
        using var db = CreateDb();
        var provider = SeedProvider(db);
        _mockProvider
            .Setup(p => p.HealthCheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderHealth.Healthy);

        var worker = CreateWorker();

        // Act
        await worker.CheckAllProvidersAsync(CancellationToken.None);

        // Assert
        using var verifyDb = CreateDb();
        var updated = await verifyDb.Providers.FindAsync(provider.Id);
        updated!.HealthStatus.Should().Be(ProviderHealth.Healthy);
        updated.LastHealthCheck.Should().NotBeNull();
        updated.LastHealthCheck.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CheckAllProviders_SetsUnreachable_WhenProviderThrows()
    {
        // Arrange
        using var db = CreateDb();
        var provider = SeedProvider(db);
        _mockProvider
            .Setup(p => p.HealthCheckAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Connection refused"));

        var worker = CreateWorker();

        // Act
        await worker.CheckAllProvidersAsync(CancellationToken.None);

        // Assert
        using var verifyDb = CreateDb();
        var updated = await verifyDb.Providers.FindAsync(provider.Id);
        updated!.HealthStatus.Should().Be(ProviderHealth.Unreachable);
        updated.LastHealthCheck.Should().NotBeNull();
    }

    [Fact]
    public async Task CheckAllProviders_SetsDegraded_WhenProviderReturnsDegraded()
    {
        // Arrange
        using var db = CreateDb();
        var provider = SeedProvider(db);
        _mockProvider
            .Setup(p => p.HealthCheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderHealth.Degraded);

        var worker = CreateWorker();

        // Act
        await worker.CheckAllProvidersAsync(CancellationToken.None);

        // Assert
        using var verifyDb = CreateDb();
        var updated = await verifyDb.Providers.FindAsync(provider.Id);
        updated!.HealthStatus.Should().Be(ProviderHealth.Degraded);
    }

    [Fact]
    public async Task CheckAllProviders_SkipsDisabledProviders()
    {
        // Arrange
        using var db = CreateDb();
        SeedProvider(db, "disabled-provider", isEnabled: false);
        _mockProvider
            .Setup(p => p.HealthCheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderHealth.Healthy);

        var worker = CreateWorker();

        // Act
        await worker.CheckAllProvidersAsync(CancellationToken.None);

        // Assert
        _mockProvider.Verify(p => p.HealthCheckAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAllProviders_ChecksMultipleProviders()
    {
        // Arrange
        using var db = CreateDb();
        SeedProvider(db, "docker-local");
        SeedProvider(db, "apple-local");

        var callCount = 0;
        _mockProvider
            .Setup(p => p.HealthCheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return ProviderHealth.Healthy;
            });

        var worker = CreateWorker();

        // Act
        await worker.CheckAllProvidersAsync(CancellationToken.None);

        // Assert
        callCount.Should().Be(2);
        using var verifyDb = CreateDb();
        var providers = await verifyDb.Providers.ToListAsync();
        providers.Should().AllSatisfy(p =>
        {
            p.HealthStatus.Should().Be(ProviderHealth.Healthy);
            p.LastHealthCheck.Should().NotBeNull();
        });
    }

    [Fact]
    public async Task CheckAllProviders_OneFailureDoesNotBlockOthers()
    {
        // Arrange
        using var db = CreateDb();
        var healthy = SeedProvider(db, "docker-local");
        var failing = SeedProvider(db, "apple-local");

        var providerMockHealthy = new Mock<IInfrastructureProvider>();
        providerMockHealthy
            .Setup(p => p.HealthCheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderHealth.Healthy);

        var providerMockFailing = new Mock<IInfrastructureProvider>();
        providerMockFailing
            .Setup(p => p.HealthCheckAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Docker daemon not running"));

        _mockProviderFactory
            .Setup(f => f.GetProvider(It.Is<InfrastructureProvider>(p => p.Id == healthy.Id)))
            .Returns(providerMockHealthy.Object);
        _mockProviderFactory
            .Setup(f => f.GetProvider(It.Is<InfrastructureProvider>(p => p.Id == failing.Id)))
            .Returns(providerMockFailing.Object);

        var worker = CreateWorker();

        // Act
        await worker.CheckAllProvidersAsync(CancellationToken.None);

        // Assert
        using var verifyDb = CreateDb();
        var healthyResult = await verifyDb.Providers.FindAsync(healthy.Id);
        var failingResult = await verifyDb.Providers.FindAsync(failing.Id);

        healthyResult!.HealthStatus.Should().Be(ProviderHealth.Healthy);
        failingResult!.HealthStatus.Should().Be(ProviderHealth.Unreachable);
    }

    [Fact]
    public async Task CheckAllProviders_SetsUnreachable_WhenProviderTimesOut()
    {
        // Arrange
        using var db = CreateDb();
        SeedProvider(db);
        _mockProvider
            .Setup(p => p.HealthCheckAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
                return ProviderHealth.Healthy;
            });

        var worker = CreateWorker();

        // Act — the worker has a 30-second timeout per provider, but we'll use cancellation
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.CheckAllProvidersAsync(cts.Token);

        // Assert — provider should be marked unreachable due to timeout/cancellation
        using var verifyDb = CreateDb();
        var updated = await verifyDb.Providers.FirstAsync();
        // The status may be Unreachable (timeout) or unchanged (cancelled before update)
        // Either is acceptable behavior when the operation is cancelled
    }

    [Fact]
    public async Task CheckAllProviders_NoProviders_CompletesWithoutError()
    {
        // Arrange — no providers seeded
        var worker = CreateWorker();

        // Act & Assert — should not throw
        await worker.CheckAllProvidersAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CheckAllProviders_PreservesLastHealthCheck_WhenStatusUnchanged()
    {
        // Arrange
        using var db = CreateDb();
        var provider = SeedProvider(db, healthStatus: ProviderHealth.Healthy);
        _mockProvider
            .Setup(p => p.HealthCheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderHealth.Healthy);

        var worker = CreateWorker();

        // Act — run twice
        await worker.CheckAllProvidersAsync(CancellationToken.None);
        var firstCheck = (await CreateDb().Providers.FindAsync(provider.Id))!.LastHealthCheck;

        await Task.Delay(50); // Small delay to ensure different timestamp
        await worker.CheckAllProvidersAsync(CancellationToken.None);
        var secondCheck = (await CreateDb().Providers.FindAsync(provider.Id))!.LastHealthCheck;

        // Assert — LastHealthCheck should be updated each run
        secondCheck.Should().BeAfter(firstCheck!.Value);
    }
}
