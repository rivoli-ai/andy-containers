using System.Text;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Andy.Containers.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

/// <summary>
/// Contract coverage for #279. These tests pin the public SSE framing used by
/// andy-agents and verify that disconnect cancellation reaches the exec layer.
/// </summary>
public sealed class ContainersControllerExecStreamTests : IDisposable
{
    private readonly ContainersDbContext _db = InMemoryDbHelper.CreateContext();
    private readonly Mock<IContainerService> _containers = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ExecStream_WritesStdoutStderrAndDoneWireContract()
    {
        var id = Guid.NewGuid();
        var controller = CreateController(id);
        var bodyStream = SetupResponse(controller);

        _containers
            .Setup(c => c.ExecStreamingAsync(
                id,
                "agent-runner",
                TimeSpan.FromSeconds(42),
                It.IsAny<Func<ExecOutputChunk, CancellationToken, ValueTask>>(),
                It.IsAny<CancellationToken>()))
            .Returns<Guid, string, TimeSpan, Func<ExecOutputChunk, CancellationToken, ValueTask>, CancellationToken>(
                async (_, _, _, onLine, ct) =>
                {
                    await onLine(new ExecOutputChunk(ExecStreamKind.Stdout, "first"), ct);
                    await onLine(new ExecOutputChunk(ExecStreamKind.Stderr, "warning"), ct);
                    await onLine(new ExecOutputChunk(
                        ExecStreamKind.Stdout,
                        """{"type":"token","value":"hello"}"""), ct);
                    return new ExecResult { ExitCode = 7 };
                });

        var result = await controller.ExecStream(
            id,
            new ExecRequest { Command = "agent-runner", TimeoutSeconds = 42 },
            CancellationToken.None);

        result.Should().BeOfType<EmptyResult>();
        controller.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        controller.Response.ContentType.Should().Be("text/event-stream");
        controller.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        controller.Response.Headers["X-Accel-Buffering"].ToString().Should().Be("no");

        ReadBody(bodyStream).Should().Be(
            """
            event: stdout
            data: {"line":"first"}

            event: stderr
            data: {"line":"warning"}

            event: stdout
            data: {"line":"{\u0022type\u0022:\u0022token\u0022,\u0022value\u0022:\u0022hello\u0022}"}

            event: done
            data: {"exitCode":7}


            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task ExecStream_DisconnectCancelsUnderlyingExecAndOmitsDone()
    {
        var id = Guid.NewGuid();
        var controller = CreateController(id);
        var bodyStream = SetupResponse(controller);
        var execStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = false;

        _containers
            .Setup(c => c.ExecStreamingAsync(
                id,
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<Func<ExecOutputChunk, CancellationToken, ValueTask>>(),
                It.IsAny<CancellationToken>()))
            .Returns<Guid, string, TimeSpan, Func<ExecOutputChunk, CancellationToken, ValueTask>, CancellationToken>(
                async (_, _, _, onLine, ct) =>
                {
                    await onLine(new ExecOutputChunk(ExecStreamKind.Stdout, "started"), ct);
                    execStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        cancellationObserved = true;
                        throw;
                    }

                    return new ExecResult();
                });

        using var disconnect = new CancellationTokenSource();
        var streamTask = controller.ExecStream(
            id,
            new ExecRequest { Command = "long-running" },
            disconnect.Token);

        await execStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        disconnect.Cancel();

        (await streamTask).Should().BeOfType<EmptyResult>();
        cancellationObserved.Should().BeTrue();
        var body = ReadBody(bodyStream);
        body.Should().Contain("event: stdout");
        body.Should().NotContain("event: done");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(86_401)]
    public async Task ExecStream_RejectsUnsafeTimeoutBeforeStartingResponse(int timeoutSeconds)
    {
        var id = Guid.NewGuid();
        var controller = CreateController(id);
        SetupResponse(controller);

        var result = await controller.ExecStream(
            id,
            new ExecRequest { Command = "echo ok", TimeoutSeconds = timeoutSeconds },
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        _containers.Verify(c => c.ExecStreamingAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<TimeSpan>(),
            It.IsAny<Func<ExecOutputChunk, CancellationToken, ValueTask>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private ContainersController CreateController(Guid id)
    {
        _containers
            .Setup(c => c.GetContainerAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Container
            {
                Id = id,
                Name = "stream-target",
                OwnerId = "owner",
                Status = ContainerStatus.Running,
                ExternalId = "docker-stream-target",
            });

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(u => u.IsAdmin()).Returns(true);
        currentUser.Setup(u => u.GetUserId()).Returns("admin");
        currentUser.Setup(u => u.IsAuthenticated()).Returns(true);

        return new ContainersController(
            _containers.Object,
            currentUser.Object,
            _db,
            Mock.Of<IGitCloneService>(),
            Mock.Of<IGitCredentialService>(),
            Mock.Of<IGitRepositoryProbeService>(),
            Mock.Of<IOrganizationMembershipService>(),
            Mock.Of<IGitDiffService>(),
            Mock.Of<IPortDiscoveryService>(),
            Mock.Of<IContainerLifecycleBus>(),
            Mock.Of<IRunOutputBus>());
    }

    private static MemoryStream SetupResponse(ContainersController controller)
    {
        var stream = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = stream;
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return stream;
    }

    private static string ReadBody(MemoryStream stream)
    {
        stream.Position = 0;
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
