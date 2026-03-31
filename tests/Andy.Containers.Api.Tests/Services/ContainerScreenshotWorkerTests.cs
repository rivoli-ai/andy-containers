using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class ContainerScreenshotWorkerTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IInfrastructureProviderFactory> _providerFactory;
    private readonly Mock<IInfrastructureProvider> _mockProvider;
    private readonly ContainerScreenshotWorker _worker;

    public ContainerScreenshotWorkerTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _providerFactory = new Mock<IInfrastructureProviderFactory>();
        _mockProvider = new Mock<IInfrastructureProvider>();

        var scopeFactory = InMemoryDbHelper.CreateScopeFactory(_db);
        var config = new ConfigurationBuilder().Build();

        _worker = new ContainerScreenshotWorker(
            scopeFactory,
            _providerFactory.Object,
            new Mock<ILogger<ContainerScreenshotWorker>>().Object,
            config);
    }

    public void Dispose() => _db.Dispose();

    private InfrastructureProvider CreateProvider()
    {
        var provider = new InfrastructureProvider
        {
            Code = "test-docker",
            Name = "Test Docker",
            Type = ProviderType.Docker,
            IsEnabled = true
        };
        _db.Providers.Add(provider);
        _db.SaveChanges();
        _providerFactory.Setup(f => f.GetProvider(It.Is<InfrastructureProvider>(p => p.Id == provider.Id)))
            .Returns(_mockProvider.Object);
        return provider;
    }

    [Fact]
    public async Task CaptureAllScreenshots_NoContainers_CompletesWithoutError()
    {
        // No containers in DB - should return immediately without errors
        await _worker.CaptureAllScreenshotsAsync(CancellationToken.None);

        // No provider calls should have been made
        _providerFactory.Verify(
            f => f.GetProvider(It.IsAny<InfrastructureProvider>()),
            Times.Never);
    }

    [Fact]
    public async Task CaptureAllScreenshots_OnlyPendingContainers_SkipsThem()
    {
        var provider = CreateProvider();
        _db.Containers.Add(new Container
        {
            Name = "pending-container",
            OwnerId = "user1",
            ProviderId = provider.Id,
            Status = ContainerStatus.Pending,
            ExternalId = "ext-1"
        });
        _db.SaveChanges();

        await _worker.CaptureAllScreenshotsAsync(CancellationToken.None);

        _mockProvider.Verify(
            p => p.ExecAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CaptureAllScreenshots_OnlyStoppedContainers_SkipsThem()
    {
        var provider = CreateProvider();
        _db.Containers.Add(new Container
        {
            Name = "stopped-container",
            OwnerId = "user1",
            ProviderId = provider.Id,
            Status = ContainerStatus.Stopped,
            ExternalId = "ext-1"
        });
        _db.SaveChanges();

        await _worker.CaptureAllScreenshotsAsync(CancellationToken.None);

        _mockProvider.Verify(
            p => p.ExecAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CaptureAllScreenshots_ContainerWithNullExternalId_SkipsIt()
    {
        var provider = CreateProvider();
        _db.Containers.Add(new Container
        {
            Name = "no-external-id",
            OwnerId = "user1",
            ProviderId = provider.Id,
            Status = ContainerStatus.Running,
            ExternalId = null
        });
        _db.SaveChanges();

        await _worker.CaptureAllScreenshotsAsync(CancellationToken.None);

        _mockProvider.Verify(
            p => p.ExecAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CaptureAllScreenshots_RunningContainer_CapturesScreenshot()
    {
        var provider = CreateProvider();
        var container = new Container
        {
            Name = "running-container",
            OwnerId = "user1",
            ProviderId = provider.Id,
            Status = ContainerStatus.Running,
            ExternalId = "ext-running-1"
        };
        _db.Containers.Add(container);
        _db.SaveChanges();

        _mockProvider.Setup(p => p.ExecAsync(
                "ext-running-1",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "user@host:~$ ls\nfile1.txt\nfile2.txt" });

        await _worker.CaptureAllScreenshotsAsync(CancellationToken.None);

        _mockProvider.Verify(
            p => p.ExecAsync("ext-running-1", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Reload from DB to check metadata was saved
        var updated = _db.Containers.First(c => c.Id == container.Id);
        updated.Metadata.Should().NotBeNullOrEmpty();
        updated.Metadata.Should().Contain("file1.txt");
    }

    [Fact]
    public async Task CaptureAllScreenshots_EmptyStdOut_DoesNotUpdateMetadata()
    {
        var provider = CreateProvider();
        var container = new Container
        {
            Name = "empty-output",
            OwnerId = "user1",
            ProviderId = provider.Id,
            Status = ContainerStatus.Running,
            ExternalId = "ext-empty-1"
        };
        _db.Containers.Add(container);
        _db.SaveChanges();

        _mockProvider.Setup(p => p.ExecAsync(
                "ext-empty-1",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "" });

        await _worker.CaptureAllScreenshotsAsync(CancellationToken.None);

        var updated = _db.Containers.First(c => c.Id == container.Id);
        updated.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task CaptureAllScreenshots_WhitespaceStdOut_DoesNotUpdateMetadata()
    {
        var provider = CreateProvider();
        var container = new Container
        {
            Name = "whitespace-output",
            OwnerId = "user1",
            ProviderId = provider.Id,
            Status = ContainerStatus.Running,
            ExternalId = "ext-ws-1"
        };
        _db.Containers.Add(container);
        _db.SaveChanges();

        _mockProvider.Setup(p => p.ExecAsync(
                "ext-ws-1",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "   \n  \n  " });

        await _worker.CaptureAllScreenshotsAsync(CancellationToken.None);

        var updated = _db.Containers.First(c => c.Id == container.Id);
        updated.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task CaptureAllScreenshots_ProviderThrowsException_DoesNotCrash()
    {
        var provider = CreateProvider();
        _db.Containers.Add(new Container
        {
            Name = "error-container",
            OwnerId = "user1",
            ProviderId = provider.Id,
            Status = ContainerStatus.Running,
            ExternalId = "ext-error-1"
        });
        _db.SaveChanges();

        _mockProvider.Setup(p => p.ExecAsync(
                "ext-error-1",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Docker daemon not available"));

        // Should not throw
        await _worker.CaptureAllScreenshotsAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CaptureAllScreenshots_MultipleRunningContainers_CapturesAll()
    {
        var provider = CreateProvider();
        for (int i = 0; i < 3; i++)
        {
            _db.Containers.Add(new Container
            {
                Name = $"container-{i}",
                OwnerId = "user1",
                ProviderId = provider.Id,
                Status = ContainerStatus.Running,
                ExternalId = $"ext-multi-{i}"
            });
        }
        _db.SaveChanges();

        _mockProvider.Setup(p => p.ExecAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "terminal output" });

        await _worker.CaptureAllScreenshotsAsync(CancellationToken.None);

        _mockProvider.Verify(
            p => p.ExecAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task CaptureAllScreenshots_ExistingMetadata_PreservesAndUpdatesScreenshot()
    {
        var provider = CreateProvider();
        var container = new Container
        {
            Name = "existing-metadata",
            OwnerId = "user1",
            ProviderId = provider.Id,
            Status = ContainerStatus.Running,
            ExternalId = "ext-meta-1",
            Metadata = "{\"screenshot\":{\"ansiText\":\"old content\",\"capturedAt\":\"2025-01-01T00:00:00Z\",\"cols\":120,\"rows\":40,\"source\":\"tmux\"}}"
        };
        _db.Containers.Add(container);
        _db.SaveChanges();

        _mockProvider.Setup(p => p.ExecAsync(
                "ext-meta-1",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "new terminal output" });

        await _worker.CaptureAllScreenshotsAsync(CancellationToken.None);

        var updated = _db.Containers.First(c => c.Id == container.Id);
        updated.Metadata.Should().Contain("new terminal output");
        updated.Metadata.Should().NotContain("old content");
    }

    [Fact]
    public async Task CaptureAllScreenshots_CancellationRequested_StopsGracefully()
    {
        var provider = CreateProvider();
        for (int i = 0; i < 5; i++)
        {
            _db.Containers.Add(new Container
            {
                Name = $"cancel-container-{i}",
                OwnerId = "user1",
                ProviderId = provider.Id,
                Status = ContainerStatus.Running,
                ExternalId = $"ext-cancel-{i}"
            });
        }
        _db.SaveChanges();

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Should not throw, should handle cancellation gracefully
        await _worker.CaptureAllScreenshotsAsync(cts.Token);
    }

    [Fact]
    public async Task CaptureAllScreenshots_MixedStatuses_OnlyCapturesRunning()
    {
        var provider = CreateProvider();

        _db.Containers.Add(new Container
        {
            Name = "running",
            OwnerId = "user1",
            ProviderId = provider.Id,
            Status = ContainerStatus.Running,
            ExternalId = "ext-run"
        });
        _db.Containers.Add(new Container
        {
            Name = "stopped",
            OwnerId = "user1",
            ProviderId = provider.Id,
            Status = ContainerStatus.Stopped,
            ExternalId = "ext-stop"
        });
        _db.Containers.Add(new Container
        {
            Name = "pending",
            OwnerId = "user1",
            ProviderId = provider.Id,
            Status = ContainerStatus.Pending,
            ExternalId = "ext-pend"
        });
        _db.SaveChanges();

        _mockProvider.Setup(p => p.ExecAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "output" });

        await _worker.CaptureAllScreenshotsAsync(CancellationToken.None);

        // Only the running container should have been captured
        _mockProvider.Verify(
            p => p.ExecAsync("ext-run", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockProvider.Verify(
            p => p.ExecAsync("ext-stop", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockProvider.Verify(
            p => p.ExecAsync("ext-pend", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CaptureAllScreenshots_UseTmuxCapturePane()
    {
        var provider = CreateProvider();
        _db.Containers.Add(new Container
        {
            Name = "tmux-test",
            OwnerId = "user1",
            ProviderId = provider.Id,
            Status = ContainerStatus.Running,
            ExternalId = "ext-tmux-1"
        });
        _db.SaveChanges();

        _mockProvider.Setup(p => p.ExecAsync(
                "ext-tmux-1",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "content" });

        await _worker.CaptureAllScreenshotsAsync(CancellationToken.None);

        // Verify the exec command uses tmux capture-pane
        _mockProvider.Verify(
            p => p.ExecAsync(
                "ext-tmux-1",
                It.Is<string>(cmd => cmd.Contains("tmux capture-pane")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
