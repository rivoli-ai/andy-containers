using System.Text;
using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Configurator;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Runs.Events;
using Andy.Containers.Models;
using Andy.Containers.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

// F4.1 (rivoli-ai/conductor#1934). GET /api/runs/{id}/output is the
// mid-run agent output SSE stream. The bus unit tests cover the moving
// parts in isolation; this suite exercises the controller's
// serialisation onto the HTTP response — Content-Type header, the
// `id: N\nevent: log\ndata: {json}\n\n` wire format, Last-Event-ID
// resumption, terminal-close, and 404 for an unknown run — using a
// MemoryStream-backed DefaultHttpContext, mirroring ImagesControllerSseTests.
public class RunsControllerOutputSseTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly InMemoryRunOutputBus _bus = new();
    private readonly RunsController _controller;

    public RunsControllerOutputSseTests()
    {
        _db = InMemoryDbHelper.CreateContext();

        var configurator = new Mock<IRunConfigurator>();
        var dispatcher = new Mock<IRunModeDispatcher>();
        var cancellation = new RunCancellationRegistry();

        _controller = new RunsController(
            _db, configurator.Object, dispatcher.Object, cancellation,
            _bus, NullLogger<RunsController>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _bus.Dispose();
    }

    [Fact]
    public async Task Output_StreamsLinesInWireFormatAndClosesOnTerminal()
    {
        var run = SeedRun(RunStatus.Running);
        var responseStream = SetupResponse();

        var now = DateTimeOffset.UtcNow;
        _bus.Publish(run.Id, new RunOutputLine(RunOutputStream.Stdout, "hello from the agent", now));
        _bus.Publish(run.Id, new RunOutputLine(RunOutputStream.Stderr, "a warning", now));
        _bus.Complete(run.Id);

        await _controller.Output(run.Id, CancellationToken.None);

        var body = ReadBody(responseStream);

        // 1. Headers — Content-Type must announce SSE.
        _controller.Response.Headers.ContentType.ToString().Should().Be("text/event-stream");
        _controller.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        _controller.Response.Headers["X-Accel-Buffering"].ToString().Should().Be("no");

        // 2. Frame structure — id, event, data, blank line.
        body.Should().Contain("id: 1\nevent: log\ndata: ");
        body.Should().Contain("\nevent: log\n");

        // 3. JSON payload — stream kind is the camelCase string the Swift
        //    ContainerLogStream decoder keys off, line carried intact.
        body.Should().Contain("\"stream\":\"stdout\"");
        body.Should().Contain("\"stream\":\"stderr\"");
        body.Should().Contain("\"line\":\"hello from the agent\"");

        // 4. Frame separation — every line ends with \n\n.
        var frameCount = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length;
        frameCount.Should().Be(2, "two published lines ⇒ two frames, each terminated by a blank line.");
    }

    [Fact]
    public async Task Output_HonoursLastEventIdHeader_NoDupesNoGaps()
    {
        var run = SeedRun(RunStatus.Running);
        var responseStream = SetupResponse(lastEventId: "1");

        var now = DateTimeOffset.UtcNow;
        _bus.Publish(run.Id, new RunOutputLine(RunOutputStream.Stdout, "one", now));   // id 1 — skipped
        _bus.Publish(run.Id, new RunOutputLine(RunOutputStream.Stdout, "two", now));   // id 2 — first seen
        _bus.Publish(run.Id, new RunOutputLine(RunOutputStream.Stdout, "three", now)); // id 3
        _bus.Complete(run.Id);

        await _controller.Output(run.Id, CancellationToken.None);

        var body = ReadBody(responseStream);

        body.Should().NotContain("id: 1\n", "Last-Event-ID=1 means the client already saw id=1.");
        body.Should().Contain("id: 2\n");
        body.Should().Contain("id: 3\n");
        body.Should().NotContain("\"line\":\"one\"");
        body.Should().Contain("\"line\":\"two\"");
    }

    [Fact]
    public async Task Output_AlreadyTerminalRun_DrainsAndClosesImmediately()
    {
        var run = SeedRun(RunStatus.Succeeded);
        var responseStream = SetupResponse();

        _bus.Publish(run.Id, new RunOutputLine(RunOutputStream.Stdout, "final line", DateTimeOffset.UtcNow));
        _bus.Complete(run.Id);

        var task = _controller.Output(run.Id, CancellationToken.None);
        var done = await Task.WhenAny(task, Task.Delay(2000));

        done.Should().BeSameAs(task, "an attach to an already-terminal run must drain then close — not hang.");
        ReadBody(responseStream).Should().Contain("\"line\":\"final line\"");
    }

    [Fact]
    public async Task Output_EmptyOutputThenTerminal_ClosesWithNoFrames()
    {
        var run = SeedRun(RunStatus.Succeeded);
        var responseStream = SetupResponse();

        // No lines were ever published — the late subscriber sees an
        // empty, immediately-closed stream.
        _bus.Complete(run.Id);

        var task = _controller.Output(run.Id, CancellationToken.None);
        var done = await Task.WhenAny(task, Task.Delay(2000));

        done.Should().BeSameAs(task);
        ReadBody(responseStream).Should().BeEmpty();
    }

    [Fact]
    public async Task Output_UnknownRun_Returns404()
    {
        SetupResponse();

        await _controller.Output(Guid.NewGuid(), CancellationToken.None);

        _controller.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    private MemoryStream SetupResponse(string? lastEventId = null)
    {
        var responseStream = new MemoryStream();
        var context = new DefaultHttpContext { Response = { Body = responseStream } };
        if (lastEventId is not null)
        {
            context.Request.Headers["Last-Event-ID"] = lastEventId;
        }
        _controller.ControllerContext = new ControllerContext { HttpContext = context };
        return responseStream;
    }

    private static string ReadBody(MemoryStream stream)
    {
        stream.Position = 0;
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private Run SeedRun(RunStatus status)
    {
        var run = new Run
        {
            Id = Guid.NewGuid(),
            AgentId = "seed-agent",
            Mode = RunMode.Headless,
            EnvironmentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Status = status,
        };
        _db.Runs.Add(run);
        _db.SaveChanges();
        return run;
    }
}
