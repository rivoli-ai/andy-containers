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

// IM9 (#263) ships the SSE endpoint at GET /api/images/build/{id}/events.
// The unit tests in ImagesControllerTests + InMemoryBuildEventBusTests
// cover the moving parts in isolation; this suite exercises the
// controller's serialisation onto the HTTP response — Content-Type
// header, the `id: N\nevent: kind\ndata: {json}\n\n` wire format, and
// the close-on-terminal contract — using a MemoryStream-backed
// DefaultHttpContext rather than a full WebApplicationFactory<Program>
// (the existing pattern in RunsControllerTests for the same kind of
// streaming-endpoint wire test).
//
// Catches:
//   - missing/wrong Content-Type header
//   - blank-line frame separator missing or in the wrong place
//   - event-id propagation (Last-Event-ID resumability needs id: lines
//     to be parseable)
//   - terminal `complete` event closing the response stream
public class ImagesControllerSseTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly InMemoryBuildEventBus _bus;
    private readonly InMemoryBuildExecutionRegistry _registry;
    private readonly ImagesController _controller;

    public ImagesControllerSseTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _bus = new InMemoryBuildEventBus();
        _registry = new InMemoryBuildExecutionRegistry();

        var mockManifest = new Mock<IImageManifestService>();
        var mockDiff = new Mock<IImageDiffService>();
        var mockCurrentUser = new Mock<ICurrentUserService>();
        mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        var mockOrg = new Mock<IOrganizationMembershipService>();
        var mockOrchestrator = new Mock<IImageBuildOrchestrator>();
        var mockExecutor = new Mock<IAsyncBuildExecutor>();
        var mockArtifactStore = new Mock<IBuildArtifactStore>();

        _controller = new ImagesController(
            _db,
            mockManifest.Object,
            mockDiff.Object,
            mockCurrentUser.Object,
            mockOrg.Object,
            mockOrchestrator.Object,
            mockExecutor.Object,
            _bus,
            _registry,
            mockArtifactStore.Object);
    }

    public void Dispose()
    {
        _db.Dispose();
        _bus.Dispose();
    }

    [Fact]
    public async Task BuildEvents_StreamsEventsInWireFormatAndClosesOnTerminal()
    {
        var buildId = Guid.NewGuid();
        var responseStream = new MemoryStream();
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Response = { Body = responseStream } },
        };

        // Pre-publish: the bus's buffer holds these for the
        // subscriber's initial replay. The stream ends on the
        // terminal `complete` event (BuildCompletedEvent).
        var now = DateTimeOffset.UtcNow;
        _bus.Publish(buildId, new BuildStepStartedEvent
        {
            Timestamp = now,
            StepName = "build",
            StepIndex = 1,
            TotalSteps = 1,
        });
        _bus.Publish(buildId, new BuildStepStdoutEvent
        {
            Timestamp = now,
            StepName = "build",
            Line = "hello from step 1",
        });
        _bus.Publish(buildId, new BuildCompletedEvent
        {
            Timestamp = now,
            Outcome = BuildOutcome.Succeeded,
            Digest = "sha256:abc",
        });

        // The controller writes until the terminal event closes the
        // bus stream. With buffered events already in the bus, this
        // returns quickly.
        await _controller.BuildEvents(buildId, CancellationToken.None);

        responseStream.Position = 0;
        var body = Encoding.UTF8.GetString(responseStream.ToArray());

        // 1. Headers — Content-Type must announce SSE.
        _controller.Response.Headers.ContentType.ToString()
            .Should().Be("text/event-stream",
                "the SSE wire format requires this exact Content-Type so clients dispatch via EventSource.");
        _controller.Response.Headers.CacheControl.ToString()
            .Should().Be("no-store");
        _controller.Response.Headers["X-Accel-Buffering"].ToString()
            .Should().Be("no",
                "nginx/proxy buffering would coalesce events; the header tells reverse proxies to flush per write.");

        // 2. Frame structure — id, event, data, blank line. xUnit
        //    string comparison suffices; the framing is small enough
        //    to assert as a contiguous substring per event.
        body.Should().Contain("id: 1\nevent: step-start\ndata: ",
            "first event is the step-start, sequence 1; the wire format is id/event/data lines per SSE spec.");
        body.Should().Contain("\nevent: step-stdout\n",
            "stdout events use the lowercase-kebab type name — clients dispatch on it.");
        body.Should().Contain("\nevent: complete\n",
            "the terminal complete event surfaces the outcome to the SSE consumer.");

        // 3. JSON payload presence — the data line carries the
        //    serialised event. The actual JSON shape is defined by
        //    System.Text.Json's defaults; the assertion here just
        //    confirms the line carries the event data.
        body.Should().Contain("\"line\":\"hello from step 1\"",
            "the BuildStepStdoutEvent's Line field reaches the wire intact.");
        body.Should().Contain("sha256:abc",
            "the BuildCompletedEvent's digest reaches the wire — clients use it to attach to the resulting artifact.");

        // 4. Frame separation — every event ends with `\n\n` so the
        //    consumer's parser can dispatch one frame at a time.
        var frameCount = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length;
        frameCount.Should().Be(3,
            "three published events ⇒ three frames, each terminated by a blank line.");
    }

    [Fact]
    public async Task BuildEvents_HonoursLastEventIdHeader()
    {
        var buildId = Guid.NewGuid();
        var responseStream = new MemoryStream();
        var context = new DefaultHttpContext { Response = { Body = responseStream } };
        // SSE reconnection contract: the client sends the last event
        // id it saw; the server resumes from after that id.
        context.Request.Headers["Last-Event-ID"] = "1";
        _controller.ControllerContext = new ControllerContext { HttpContext = context };

        var now = DateTimeOffset.UtcNow;
        _bus.Publish(buildId, new BuildStepStartedEvent
        {
            Timestamp = now,
            StepName = "build",
            StepIndex = 1,
            TotalSteps = 1,
        }); // id=1 — should be skipped on replay
        _bus.Publish(buildId, new BuildStepStdoutEvent
        {
            Timestamp = now,
            StepName = "build",
            Line = "second event",
        }); // id=2 — first one client should see
        _bus.Publish(buildId, new BuildCompletedEvent
        {
            Timestamp = now,
            Outcome = BuildOutcome.Succeeded,
        }); // id=3

        await _controller.BuildEvents(buildId, CancellationToken.None);

        responseStream.Position = 0;
        var body = Encoding.UTF8.GetString(responseStream.ToArray());

        body.Should().NotContain("id: 1\n",
            "Last-Event-ID=1 means the client already saw id=1; it must not appear again on the wire.");
        body.Should().Contain("id: 2\n",
            "id=2 is the first event after the supplied Last-Event-ID — first one to surface.");
        body.Should().Contain("id: 3\n",
            "id=3 (the terminal event) must reach the client so it knows the build completed.");
    }

    [Fact]
    public async Task BuildEvents_AlreadyCompletedBuild_ClosesImmediately()
    {
        var buildId = Guid.NewGuid();
        var responseStream = new MemoryStream();
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Response = { Body = responseStream } },
        };

        // Build completed before the SSE attach. The bus's buffered
        // replay should surface the terminal event and close the
        // subscription cleanly — not hang the client waiting for
        // events that won't come.
        _bus.Publish(buildId, new BuildCompletedEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Outcome = BuildOutcome.Failed,
            FailureReason = "build went sideways",
        });

        var subscribeTask = _controller.BuildEvents(buildId, CancellationToken.None);
        var completed = await Task.WhenAny(subscribeTask, Task.Delay(2000));

        completed.Should().BeSameAs(subscribeTask,
            "an attach to an already-completed build must replay the terminal event and close — not hang.");

        responseStream.Position = 0;
        var body = Encoding.UTF8.GetString(responseStream.ToArray());
        body.Should().Contain("event: complete\n");
        body.Should().Contain("\"outcome\":\"Failed\"",
            "the complete event surfaces the failure outcome so the client can react without polling status.");
    }
}
