using Andy.Containers.Abstractions.Images;
using Andy.Containers.Infrastructure.Build.Events;
using Andy.Containers.Storage;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Tests.Infrastructure.Build.Events;

// IM9 (rivoli-ai/andy-containers#263). The executor's contract is
// thin but load-bearing: cache hits resolve in the request thread,
// cache misses queue a background task and return immediately. The
// async path must publish events to the bus and update the
// registry's terminal state. These tests verify both paths via a
// mock orchestrator + real registry + real bus.
public class AsyncBuildExecutorTests
{
    [Fact]
    public async Task StartAsync_CacheHit_ReturnsCachedSynchronously()
    {
        var orchestrator = new Mock<IImageBuildOrchestrator>();
        var hitResult = new BuildResult
        {
            BuildId = Guid.NewGuid(),
            Status = BuildResultStatus.Cached,
            Digest = "sha256:abc",
            References = [new BuildResultReference("local-zot", "test", "tag", DateTimeOffset.UtcNow)],
        };
        orchestrator.Setup(o => o.TryCacheHitAsync(It.IsAny<ImageBuildRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(hitResult);

        var (executor, _, _) = MakeExecutor(orchestrator.Object);

        var handle = await executor.StartAsync(
            new ImageBuildRequest(Guid.NewGuid(), null, false, "user"),
            CancellationToken.None);

        handle.Status.Should().Be(AsyncBuildHandleStatus.Cached);
        handle.Result.Should().BeSameAs(hitResult,
            "the cache-hit result is returned directly without copy/transformation.");

        // Crucially: the orchestrator's BuildAsync must NOT have been
        // invoked — that would have spawned a background build task.
        orchestrator.Verify(o => o.BuildAsync(
            It.IsAny<ImageBuildRequest>(),
            It.IsAny<IProgress<BuildProgressEvent>>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StartAsync_CacheMiss_QueuesBackgroundBuild()
    {
        var orchestrator = new Mock<IImageBuildOrchestrator>();
        orchestrator.Setup(o => o.TryCacheHitAsync(It.IsAny<ImageBuildRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BuildResult?)null);
        orchestrator.Setup(o => o.BuildAsync(
                It.IsAny<ImageBuildRequest>(),
                It.IsAny<IProgress<BuildProgressEvent>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BuildResult
            {
                BuildId = Guid.NewGuid(),
                Status = BuildResultStatus.Succeeded,
                Digest = "sha256:built",
                References = [new BuildResultReference("local-zot", "test", "tag", DateTimeOffset.UtcNow)],
            });

        var (executor, registry, bus) = MakeExecutor(orchestrator.Object);

        var handle = await executor.StartAsync(
            new ImageBuildRequest(Guid.NewGuid(), null, false, "user"),
            CancellationToken.None);

        handle.Status.Should().Be(AsyncBuildHandleStatus.Queued,
            "cache misses must return immediately with status=queued; the build runs in the background.");
        handle.Result.Should().BeNull();

        // Wait for the background task to publish its terminal event.
        await WaitForTerminalAsync(bus, handle.BuildId, TimeSpan.FromSeconds(2));

        var state = registry.TryGet(handle.BuildId);
        state.Should().NotBeNull();
        state!.Status.Should().Be(BuildExecutionStatus.Succeeded);
        state.Digest.Should().Be("sha256:built");
    }

    [Fact]
    public async Task StartAsync_OrchestratorThrows_RegistryShowsFailedTerminal()
    {
        var orchestrator = new Mock<IImageBuildOrchestrator>();
        orchestrator.Setup(o => o.TryCacheHitAsync(It.IsAny<ImageBuildRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BuildResult?)null);
        orchestrator.Setup(o => o.BuildAsync(
                It.IsAny<ImageBuildRequest>(),
                It.IsAny<IProgress<BuildProgressEvent>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("backend exploded"));

        var (executor, registry, bus) = MakeExecutor(orchestrator.Object);

        var handle = await executor.StartAsync(
            new ImageBuildRequest(Guid.NewGuid(), null, false, "user"),
            CancellationToken.None);

        await WaitForTerminalAsync(bus, handle.BuildId, TimeSpan.FromSeconds(2));

        var state = registry.TryGet(handle.BuildId);
        state.Should().NotBeNull();
        state!.Status.Should().Be(BuildExecutionStatus.Failed,
            "background-task exceptions must surface as Failed terminal state, not silently lose the build.");
        state.ErrorCode.Should().Be("build.unexpected");
    }

    [Fact]
    public async Task StartAsync_ForceTrue_BypassesCacheHitCheck()
    {
        var orchestrator = new Mock<IImageBuildOrchestrator>();
        orchestrator.Setup(o => o.BuildAsync(
                It.IsAny<ImageBuildRequest>(),
                It.IsAny<IProgress<BuildProgressEvent>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BuildResult
            {
                BuildId = Guid.NewGuid(),
                Status = BuildResultStatus.Succeeded,
            });

        var (executor, _, bus) = MakeExecutor(orchestrator.Object);

        var handle = await executor.StartAsync(
            new ImageBuildRequest(Guid.NewGuid(), null, Force: true, "user"),
            CancellationToken.None);

        handle.Status.Should().Be(AsyncBuildHandleStatus.Queued);

        // Force=true must skip the TryCacheHitAsync probe entirely.
        orchestrator.Verify(
            o => o.TryCacheHitAsync(It.IsAny<ImageBuildRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);

        await WaitForTerminalAsync(bus, handle.BuildId, TimeSpan.FromSeconds(2));
    }

    private static (AsyncBuildExecutor, InMemoryBuildExecutionRegistry, InMemoryBuildEventBus) MakeExecutor(
        IImageBuildOrchestrator orchestrator)
    {
        var bus = new InMemoryBuildEventBus();
        var registry = new InMemoryBuildExecutionRegistry();

        // The executor needs an IServiceScopeFactory so it can spawn
        // a fresh scope per build. Build a minimal DI tree that
        // resolves the supplied orchestrator instance.
        var services = new ServiceCollection();
        services.AddSingleton(orchestrator);
        var rootProvider = services.BuildServiceProvider();
        var scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();

        var lifetime = new ApplicationLifetime(NullLogger<ApplicationLifetime>.Instance);

        var executor = new AsyncBuildExecutor(
            scopeFactory,
            bus,
            registry,
            lifetime,
            NullLogger<AsyncBuildExecutor>.Instance);
        return (executor, registry, bus);
    }

    private static async Task WaitForTerminalAsync(IBuildEventBus bus, Guid buildId, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await foreach (var envelope in bus.SubscribeAsync(buildId, null, cts.Token))
        {
            if (envelope.Event is BuildCompletedEvent)
            {
                return;
            }
        }
    }
}
