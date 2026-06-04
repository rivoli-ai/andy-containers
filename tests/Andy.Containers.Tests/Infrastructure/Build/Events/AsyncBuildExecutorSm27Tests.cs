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

// SM.2.7 (rivoli-ai/conductor#2009). Integration tests for the two new
// SSE events emitted by AsyncBuildExecutor:
//
//   1. BuildCachedEvent — emitted on cache hit so the consumer can
//      reconcile against "present" without inferring from silence.
//   2. BuildFailureEvent — emitted before the terminal BuildCompletedEvent
//      on all failure paths, carrying structured Reason + Transient.
//
// These tests use a mock IImageBuildOrchestrator + real InMemoryBuildEventBus
// + real InMemoryBuildExecutionRegistry — the same pattern used by
// AsyncBuildExecutorTests.
public class AsyncBuildExecutorSm27Tests
{
    // ------------------------------------------------------------------ //
    //  Cache-hit → BuildCachedEvent + BuildCompletedEvent                 //
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task StartAsync_CacheHit_EmitsCachedEventBeforeComplete()
    {
        // SM.2.7 AC2: a cache hit emits an explicit .cached SSE event.
        var digest  = "sha256:cached-abc";
        var hitResult = new BuildResult
        {
            BuildId    = Guid.NewGuid(),
            Status     = BuildResultStatus.Cached,
            Digest     = digest,
            References = [],
        };
        var orchestrator = new Mock<IImageBuildOrchestrator>();
        orchestrator.Setup(o => o.TryCacheHitAsync(
                It.IsAny<ImageBuildRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(hitResult);

        var (executor, _, bus) = MakeExecutor(orchestrator.Object);

        var handle = await executor.StartAsync(
            new ImageBuildRequest(Guid.NewGuid(), null, false, "user"),
            CancellationToken.None);

        handle.Status.Should().Be(AsyncBuildHandleStatus.Cached);

        // Drain the bus for the cache-hit buildId (not handle.BuildId —
        // the executor uses hitResult.BuildId for the bus events).
        var events = await DrainAsync(bus, hitResult.BuildId, TimeSpan.FromSeconds(2));

        // The cached event must be present.
        var cachedEvent = events.OfType<BuildCachedEvent>().FirstOrDefault();
        cachedEvent.Should().NotBeNull(
            because: "AC2 requires a non-silent .cached SSE event on a cache hit");
        cachedEvent!.Digest.Should().Be(digest,
            because: "the cached event must carry the artifact digest for consumer reconciliation");

        // The terminal complete event must also fire.
        var completedEvent = events.OfType<BuildCompletedEvent>().FirstOrDefault();
        completedEvent.Should().NotBeNull(
            because: "the terminal complete event must still fire so consumers with a single handler keep working");
        completedEvent!.Outcome.Should().Be(BuildOutcome.Succeeded);
        completedEvent.Digest.Should().Be(digest);
    }

    // ------------------------------------------------------------------ //
    //  Build failure → BuildFailureEvent (structured) + BuildCompletedEvent
    // ------------------------------------------------------------------ //

    [Theory]
    [InlineData("ensure_pull_docker_launch_failed.Pull", BuildFailureReason.EngineUnavailable, true)]
    [InlineData("registry.unreachable",                  BuildFailureReason.RegistryUnreachable, true)]
    [InlineData("manifest_unknown",                      BuildFailureReason.ManifestUnknown, false)]
    [InlineData("digest_mismatch",                       BuildFailureReason.DigestMismatch, false)]
    [InlineData("unknown_new_code",                      BuildFailureReason.Unknown, false)]
    public async Task StartAsync_BuildFails_EmitsStructuredFailureEvent(
        string errorCode,
        BuildFailureReason expectedReason,
        bool expectedTransient)
    {
        // SM.2.7 AC1: the SSE emits a structured BuildFailureEvent,
        // not just a free-text buildLog line.
        var orchestrator = new Mock<IImageBuildOrchestrator>();
        orchestrator.Setup(o => o.TryCacheHitAsync(
                It.IsAny<ImageBuildRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BuildResult?)null);
        orchestrator.Setup(o => o.BuildAsync(
                It.IsAny<ImageBuildRequest>(),
                It.IsAny<IProgress<BuildProgressEvent>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BuildResult
            {
                BuildId      = Guid.NewGuid(),
                Status       = BuildResultStatus.Failed,
                ErrorCode    = errorCode,
                ErrorMessage = $"simulated failure: {errorCode}",
            });

        var (executor, _, bus) = MakeExecutor(orchestrator.Object);

        var handle = await executor.StartAsync(
            new ImageBuildRequest(Guid.NewGuid(), null, false, "user"),
            CancellationToken.None);

        var events = await DrainAsync(bus, handle.BuildId, TimeSpan.FromSeconds(3));

        // A structured BuildFailureEvent must be present.
        var failureEvent = events.OfType<BuildFailureEvent>().FirstOrDefault();
        failureEvent.Should().NotBeNull(
            because: $"AC1 requires a structured BuildFailureEvent for error code '{errorCode}'");
        failureEvent!.Reason.Should().Be(expectedReason,
            because: $"'{errorCode}' must map to {expectedReason}");
        failureEvent.Transient.Should().Be(expectedTransient,
            because: $"{expectedReason} must be classified {(expectedTransient ? "transient" : "permanent")}");

        // The failure event must appear BEFORE the terminal complete.
        var failureIndex   = events.IndexOf(failureEvent);
        var completedEvent = events.OfType<BuildCompletedEvent>().FirstOrDefault();
        completedEvent.Should().NotBeNull(
            because: "the terminal complete event must still fire after the failure event");
        var completedIndex = events.IndexOf(completedEvent!);
        failureIndex.Should().BeLessThan(completedIndex,
            because: "the structured failure event must precede the terminal complete event in the stream");
    }

    [Fact]
    public async Task StartAsync_OrchestratorThrows_EmitsUnknownPermanentFailureEvent()
    {
        // Unexpected exceptions (not BuildResult.Failed) also produce a
        // BuildFailureEvent so the consumer sees a structured signal.
        var orchestrator = new Mock<IImageBuildOrchestrator>();
        orchestrator.Setup(o => o.TryCacheHitAsync(
                It.IsAny<ImageBuildRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BuildResult?)null);
        orchestrator.Setup(o => o.BuildAsync(
                It.IsAny<ImageBuildRequest>(),
                It.IsAny<IProgress<BuildProgressEvent>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("unexpected crash"));

        var (executor, _, bus) = MakeExecutor(orchestrator.Object);

        var handle = await executor.StartAsync(
            new ImageBuildRequest(Guid.NewGuid(), null, false, "user"),
            CancellationToken.None);

        var events = await DrainAsync(bus, handle.BuildId, TimeSpan.FromSeconds(3));

        var failureEvent = events.OfType<BuildFailureEvent>().FirstOrDefault();
        failureEvent.Should().NotBeNull(
            because: "unexpected exceptions must also produce a structured BuildFailureEvent");
        failureEvent!.Reason.Should().Be(BuildFailureReason.Unknown);
        failureEvent.Transient.Should().BeFalse(
            because: "Unknown failures are classified permanent to surface to the operator");
    }

    // ------------------------------------------------------------------ //
    //  Helpers                                                             //
    // ------------------------------------------------------------------ //

    private static (AsyncBuildExecutor, InMemoryBuildExecutionRegistry, InMemoryBuildEventBus) MakeExecutor(
        IImageBuildOrchestrator orchestrator)
    {
        var bus      = new InMemoryBuildEventBus();
        var registry = new InMemoryBuildExecutionRegistry();

        var services = new ServiceCollection();
        services.AddSingleton(orchestrator);
        var rootProvider = services.BuildServiceProvider();
        var scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();
        var lifetime     = new ApplicationLifetime(NullLogger<ApplicationLifetime>.Instance);

        var executor = new AsyncBuildExecutor(
            scopeFactory,
            bus,
            registry,
            lifetime,
            NullLogger<AsyncBuildExecutor>.Instance);
        return (executor, registry, bus);
    }

    /// <summary>
    /// Drain all events up to and including the terminal
    /// <see cref="BuildCompletedEvent"/>, or until <paramref name="timeout"/>.
    /// </summary>
    private static async Task<List<BuildProgressEvent>> DrainAsync(
        IBuildEventBus bus,
        Guid buildId,
        TimeSpan timeout)
    {
        var events = new List<BuildProgressEvent>();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var envelope in bus.SubscribeAsync(buildId, null, cts.Token))
            {
                events.Add(envelope.Event);
                if (envelope.Event is BuildCompletedEvent)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout — return what we have so the caller can assert on it.
        }
        return events;
    }
}
