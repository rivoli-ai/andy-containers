using System.Text;
using Andy.Containers.Abstractions.Images;
using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Build.Events;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

// SM.2.7 (rivoli-ai/conductor#2009). SSE wire-format tests for the two
// new event types added by this story:
//
//   BuildFailureEvent  →  event: build-failed
//   BuildCachedEvent   →  event: cached
//
// Tests mirror the existing ImagesControllerSseTests pattern (MemoryStream-
// backed DefaultHttpContext, pre-published events, synchronous drain).
// Each test verifies the discriminator name, JSON payload shape, and that
// the frame separator is correct.
public class ImagesControllerSseSm27Tests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly InMemoryBuildEventBus _bus;
    private readonly InMemoryBuildExecutionRegistry _registry;
    private readonly ImagesController _controller;

    public ImagesControllerSseSm27Tests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _bus = new InMemoryBuildEventBus();
        _registry = new InMemoryBuildExecutionRegistry();

        var mockManifest = new Mock<IImageManifestService>();
        var mockDiff     = new Mock<IImageDiffService>();
        var mockUser     = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.GetUserId()).Returns("test-user");
        mockUser.Setup(u => u.IsAdmin()).Returns(true);
        var mockOrg          = new Mock<IOrganizationMembershipService>();
        var mockOrchestrator = new Mock<IImageBuildOrchestrator>();
        var mockExecutor     = new Mock<IAsyncBuildExecutor>();
        var mockArtifacts    = new Mock<IBuildArtifactStore>();

        _controller = new ImagesController(
            _db,
            mockManifest.Object,
            mockDiff.Object,
            mockUser.Object,
            mockOrg.Object,
            mockOrchestrator.Object,
            mockExecutor.Object,
            _bus,
            _registry,
            mockArtifacts.Object);
    }

    public void Dispose()
    {
        _db.Dispose();
        _bus.Dispose();
    }

    // ------------------------------------------------------------------ //
    //  BuildFailureEvent → wire type "build-failed"                       //
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task BuildEvents_BuildFailureEvent_EmitsBuildFailedFrame()
    {
        var buildId        = Guid.NewGuid();
        var responseStream = new MemoryStream();
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Response = { Body = responseStream } },
        };

        var now = DateTimeOffset.UtcNow;
        _bus.Publish(buildId, new BuildFailureEvent
        {
            Timestamp = now,
            Reason    = BuildFailureReason.DigestMismatch,
            Transient = false,
            Detail    = "got sha256:bad, expected sha256:good",
        });
        _bus.Publish(buildId, new BuildCompletedEvent
        {
            Timestamp = now,
            Outcome   = BuildOutcome.Failed,
            FailureReason = "digest mismatch",
        });

        await _controller.BuildEvents(buildId, CancellationToken.None);

        responseStream.Position = 0;
        var body = Encoding.UTF8.GetString(responseStream.ToArray());

        // 1. Discriminator — must be "build-failed" not "unknown".
        body.Should().Contain("event: build-failed\n",
            because: "the BuildFailureEvent wire discriminator must be 'build-failed' per the SM.2.7 spec");

        // 2. JSON payload — reason and transient fields present and correct.
        body.Should().Contain("\"reason\":\"DigestMismatch\"",
            because: "the failure reason must serialise as a string enum value");
        body.Should().Contain("\"transient\":false",
            because: "DigestMismatch is a permanent failure; transient must be false");
        body.Should().Contain("\"detail\":\"got sha256:bad, expected sha256:good\"",
            because: "the detail text must reach the wire intact for operator diagnostics");

        // 3. Frame count — 2 events: build-failed + complete.
        var frameCount = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length;
        frameCount.Should().Be(2,
            because: "two published events → two SSE frames");
    }

    [Theory]
    [InlineData(BuildFailureReason.RegistryUnreachable, true)]
    [InlineData(BuildFailureReason.EngineUnavailable,   true)]
    [InlineData(BuildFailureReason.PullInterrupted,     true)]
    [InlineData(BuildFailureReason.ManifestUnknown,     false)]
    [InlineData(BuildFailureReason.DigestMismatch,      false)]
    [InlineData(BuildFailureReason.ImagePullFailed,     false)]
    [InlineData(BuildFailureReason.Unknown,             false)]
    public async Task BuildEvents_EachFailureReason_SerialiseWithCorrectTransientFlag(
        BuildFailureReason reason, bool expectedTransient)
    {
        var buildId        = Guid.NewGuid();
        var responseStream = new MemoryStream();
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Response = { Body = responseStream } },
        };

        var now = DateTimeOffset.UtcNow;
        _bus.Publish(buildId, new BuildFailureEvent
        {
            Timestamp = now,
            Reason    = reason,
            Transient = reason.IsTransient(),
        });
        _bus.Publish(buildId, new BuildCompletedEvent
        {
            Timestamp = now,
            Outcome   = BuildOutcome.Failed,
        });

        await _controller.BuildEvents(buildId, CancellationToken.None);

        responseStream.Position = 0;
        var body = Encoding.UTF8.GetString(responseStream.ToArray());

        body.Should().Contain("event: build-failed\n",
            because: $"BuildFailureEvent({reason}) must arrive as 'build-failed' frame");
        body.Should().Contain($"\"reason\":\"{reason}\"",
            because: $"reason must be the string enum value '{reason}'");
        body.Should().Contain($"\"transient\":{expectedTransient.ToString().ToLower()}",
            because: $"transient for {reason} must be {expectedTransient}");
    }

    // ------------------------------------------------------------------ //
    //  BuildCachedEvent → wire type "cached"                              //
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task BuildEvents_BuildCachedEvent_EmitsCachedFrame()
    {
        var buildId        = Guid.NewGuid();
        var responseStream = new MemoryStream();
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Response = { Body = responseStream } },
        };

        var now = DateTimeOffset.UtcNow;
        _bus.Publish(buildId, new BuildCachedEvent
        {
            Timestamp = now,
            Digest    = "sha256:abc123",
        });
        _bus.Publish(buildId, new BuildCompletedEvent
        {
            Timestamp = now,
            Outcome   = BuildOutcome.Succeeded,
            Digest    = "sha256:abc123",
        });

        await _controller.BuildEvents(buildId, CancellationToken.None);

        responseStream.Position = 0;
        var body = Encoding.UTF8.GetString(responseStream.ToArray());

        // 1. Discriminator — must be "cached".
        body.Should().Contain("event: cached\n",
            because: "the BuildCachedEvent wire discriminator must be 'cached' per SM.2.7; " +
                     "consumers reconcile a cache hit without inferring from silence");

        // 2. JSON payload — digest present.
        body.Should().Contain("\"digest\":\"sha256:abc123\"",
            because: "the digest must reach the wire so the consumer can match the artifact");

        // 3. Frame count — 2 events: cached + complete.
        var frameCount = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length;
        frameCount.Should().Be(2,
            because: "two published events → two SSE frames");
    }

    [Fact]
    public async Task BuildEvents_BuildCachedEventWithNullDigest_SerialisesWithoutError()
    {
        // Digest may be null when the cache hit was detected before the
        // digest was resolved.
        var buildId        = Guid.NewGuid();
        var responseStream = new MemoryStream();
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Response = { Body = responseStream } },
        };

        var now = DateTimeOffset.UtcNow;
        _bus.Publish(buildId, new BuildCachedEvent { Timestamp = now, Digest = null });
        _bus.Publish(buildId, new BuildCompletedEvent { Timestamp = now, Outcome = BuildOutcome.Succeeded });

        await _controller.BuildEvents(buildId, CancellationToken.None);

        responseStream.Position = 0;
        var body = Encoding.UTF8.GetString(responseStream.ToArray());

        body.Should().Contain("event: cached\n",
            because: "a null digest must not prevent the cached event from being serialised");
    }

    // ------------------------------------------------------------------ //
    //  Sequence: cached + complete both appear (non-silent cache hit)     //
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task BuildEvents_CacheHitSequence_BothCachedAndCompleteFramesAppear()
    {
        // Verifies the full SM.2.7 requirement: a cache hit emits an
        // explicit .cached SSE event so the consumer does NOT have to
        // infer "no bytes transferred" from silence.
        var buildId        = Guid.NewGuid();
        var responseStream = new MemoryStream();
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Response = { Body = responseStream } },
        };

        var now = DateTimeOffset.UtcNow;
        _bus.Publish(buildId, new BuildCachedEvent
        {
            Timestamp = now,
            Digest    = "sha256:deadbeef",
        });
        _bus.Publish(buildId, new BuildCompletedEvent
        {
            Timestamp = now,
            Outcome   = BuildOutcome.Succeeded,
            Digest    = "sha256:deadbeef",
        });

        await _controller.BuildEvents(buildId, CancellationToken.None);

        responseStream.Position = 0;
        var body = Encoding.UTF8.GetString(responseStream.ToArray());

        // Both frames must appear — not just the terminal complete.
        body.Should().Contain("event: cached\n",
            because: "the explicit .cached event aligns with Conductor's registrySeedingPullCompleted(alreadyPresent:true)");
        body.Should().Contain("event: complete\n",
            because: "the terminal complete event must still fire so consumers with a single terminal handler keep working");
        body.Should().Contain("\"digest\":\"sha256:deadbeef\"",
            because: "the digest must appear on the cached event for the consumer to reconcile");
    }
}
