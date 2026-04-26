using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

/// <summary>
/// Unit tests for ContainerExternalIdReconciler (conductor #840).
///
/// The reconciler runs once at startup, groups Running rows by their
/// provider, asks each provider for the bulk live ID set, and flips
/// any row whose ExternalId is missing to Destroyed.
/// </summary>
public class ContainerExternalIdReconcilerTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IInfrastructureProviderFactory> _providerFactory;
    private readonly Mock<IInfrastructureProvider> _mockProvider;
    private readonly ContainerExternalIdReconciler _reconciler;

    public ContainerExternalIdReconcilerTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _providerFactory = new Mock<IInfrastructureProviderFactory>();
        _mockProvider = new Mock<IInfrastructureProvider>();

        var scopeFactory = InMemoryDbHelper.CreateScopeFactory(_db);
        _reconciler = new ContainerExternalIdReconciler(
            scopeFactory,
            new Mock<ILogger<ContainerExternalIdReconciler>>().Object);

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        // The factory is also registered into the scope so the reconciler
        // can resolve it. InMemoryDbHelper.CreateScopeFactory only wires
        // ContainersDbContext; we override scope resolution here via
        // shimming the factory mock through the scope.
    }

    public void Dispose() => _db.Dispose();

    private InfrastructureProvider AddProvider(string name = "test-provider")
    {
        var provider = new InfrastructureProvider
        {
            Code = name,
            Name = name,
            Type = ProviderType.Docker,
            IsEnabled = true
        };
        _db.Providers.Add(provider);
        _db.SaveChanges();
        _providerFactory
            .Setup(f => f.GetProvider(It.Is<InfrastructureProvider>(p => p.Id == provider.Id)))
            .Returns(_mockProvider.Object);
        return provider;
    }

    private Container AddContainer(
        InfrastructureProvider provider,
        string name,
        string? externalId,
        ContainerStatus status = ContainerStatus.Running)
    {
        var container = new Container
        {
            Name = name,
            OwnerId = "test-user",
            ProviderId = provider.Id,
            ExternalId = externalId,
            Status = status
        };
        _db.Containers.Add(container);
        _db.SaveChanges();
        return container;
    }

    /// <summary>
    /// Builds a reconciler whose scope resolves both DbContext AND the
    /// mocked provider factory. The default
    /// <c>InMemoryDbHelper.CreateScopeFactory</c> only wires the db
    /// context; the reconciler also asks the scope for an
    /// <c>IInfrastructureProviderFactory</c>.
    /// </summary>
    private ContainerExternalIdReconciler CreateReconcilerWithFactoryInScope()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton(_providerFactory.Object);
        var provider = services.BuildServiceProvider();

        var scopeFactoryMock = new Mock<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
        scopeFactoryMock.Setup(f => f.CreateScope())
            .Returns(() =>
            {
                var scope = new Mock<Microsoft.Extensions.DependencyInjection.IServiceScope>();
                scope.Setup(s => s.ServiceProvider).Returns(provider);
                return scope.Object;
            });

        return new ContainerExternalIdReconciler(
            scopeFactoryMock.Object,
            new Mock<ILogger<ContainerExternalIdReconciler>>().Object);
    }

    // MARK: - Tests

    [Fact]
    public async Task ReconcileAsync_NoRunningRows_NoOp()
    {
        var reconciler = CreateReconcilerWithFactoryInScope();
        await reconciler.ReconcileAsync(CancellationToken.None);

        // Mock provider should never be asked for the live ID set.
        _mockProvider.Verify(
            p => p.ListExternalIdsAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReconcileAsync_AllRowsAlive_StatusUnchanged()
    {
        var provider = AddProvider();
        var alive1 = AddContainer(provider, "alive-1", "ext-1");
        var alive2 = AddContainer(provider, "alive-2", "ext-2");

        _mockProvider
            .Setup(p => p.ListExternalIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(["ext-1", "ext-2"]));

        var reconciler = CreateReconcilerWithFactoryInScope();
        await reconciler.ReconcileAsync(CancellationToken.None);

        var refreshed1 = await _db.Containers.FindAsync(alive1.Id);
        var refreshed2 = await _db.Containers.FindAsync(alive2.Id);
        refreshed1!.Status.Should().Be(ContainerStatus.Running);
        refreshed2!.Status.Should().Be(ContainerStatus.Running);
    }

    [Fact]
    public async Task ReconcileAsync_OrphanRow_FlipsToDestroyed()
    {
        var provider = AddProvider();
        var orphan = AddContainer(provider, "orphan", "ext-gone");

        _mockProvider
            .Setup(p => p.ListExternalIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>()); // empty live set

        var reconciler = CreateReconcilerWithFactoryInScope();
        await reconciler.ReconcileAsync(CancellationToken.None);

        var refreshed = await _db.Containers.FindAsync(orphan.Id);
        refreshed!.Status.Should().Be(ContainerStatus.Destroyed,
            "row whose ExternalId disappeared off the host should be marked Destroyed");
        refreshed.StoppedAt.Should().NotBeNull(
            "Destroyed transition stamps StoppedAt for the audit trail");
    }

    [Fact]
    public async Task ReconcileAsync_OnlyMissingRowsAreFlipped()
    {
        var provider = AddProvider();
        var alive = AddContainer(provider, "alive", "ext-keep");
        var orphan = AddContainer(provider, "orphan", "ext-gone");

        _mockProvider
            .Setup(p => p.ListExternalIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(["ext-keep"]));

        var reconciler = CreateReconcilerWithFactoryInScope();
        await reconciler.ReconcileAsync(CancellationToken.None);

        (await _db.Containers.FindAsync(alive.Id))!.Status.Should().Be(ContainerStatus.Running);
        (await _db.Containers.FindAsync(orphan.Id))!.Status.Should().Be(ContainerStatus.Destroyed);
    }

    [Fact]
    public async Task ReconcileAsync_NonRunningRowsAreIgnored()
    {
        var provider = AddProvider();
        // Orphan ExternalId, but row is Stopped — out of scope for this
        // worker, the periodic ContainerStatusSyncWorker handles
        // running drift. We only fix the cold-start "Running but gone"
        // window.
        var stopped = AddContainer(provider, "stopped-orphan", "ext-stale", ContainerStatus.Stopped);

        _mockProvider
            .Setup(p => p.ListExternalIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());

        var reconciler = CreateReconcilerWithFactoryInScope();
        await reconciler.ReconcileAsync(CancellationToken.None);

        // Stopped row should remain Stopped — the reconciler only
        // touches Running rows.
        (await _db.Containers.FindAsync(stopped.Id))!.Status.Should().Be(ContainerStatus.Stopped);
    }

    [Fact]
    public async Task ReconcileAsync_NullProviderReturn_SkipsProvider()
    {
        var provider = AddProvider();
        var orphan = AddContainer(provider, "orphan", "ext-gone");

        _mockProvider
            .Setup(p => p.ListExternalIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((HashSet<string>?)null);

        var reconciler = CreateReconcilerWithFactoryInScope();
        await reconciler.ReconcileAsync(CancellationToken.None);

        // Provider doesn't support bulk listing — periodic worker will
        // catch this row, reconciler must NOT mark it Destroyed.
        (await _db.Containers.FindAsync(orphan.Id))!.Status.Should().Be(
            ContainerStatus.Running,
            "providers that return null from ListExternalIdsAsync are skipped — periodic worker handles them");
    }

    [Fact]
    public async Task ReconcileAsync_ProviderThrows_DoesNotThrow()
    {
        var provider = AddProvider();
        var orphan = AddContainer(provider, "orphan", "ext-gone");

        _mockProvider
            .Setup(p => p.ListExternalIdsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("docker daemon unreachable"));

        var reconciler = CreateReconcilerWithFactoryInScope();
        // Should swallow + log, not crash.
        await reconciler.ReconcileAsync(CancellationToken.None);

        // Same row, untouched — better to leave the UI showing "Running"
        // than to flip every container to Destroyed because docker is
        // momentarily unreachable.
        (await _db.Containers.FindAsync(orphan.Id))!.Status.Should().Be(ContainerStatus.Running);
    }

    [Fact]
    public async Task ReconcileAsync_RowsWithEmptyExternalId_AreIgnored()
    {
        var provider = AddProvider();
        AddContainer(provider, "no-id", externalId: "");
        AddContainer(provider, "null-id", externalId: null);

        _mockProvider
            .Setup(p => p.ListExternalIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());

        var reconciler = CreateReconcilerWithFactoryInScope();
        await reconciler.ReconcileAsync(CancellationToken.None);

        // Provider should never be asked — both rows are filtered out
        // before the bulk listing call.
        _mockProvider.Verify(
            p => p.ListExternalIdsAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
