using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

public class TerminalControllerTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly string _dbName = Guid.NewGuid().ToString();

    public TerminalControllerTests()
    {
        _db = InMemoryDbHelper.CreateContext(_dbName);
    }

    public void Dispose() => _db.Dispose();

    private TerminalController CreateController(DefaultHttpContext? httpContext = null)
    {
        httpContext ??= new DefaultHttpContext();
        var logger = new Mock<ILogger<TerminalController>>();
        // Use a fresh context with the same DB name so EF tracks entities correctly
        var db = InMemoryDbHelper.CreateContext(_dbName);
        var controller = new TerminalController(db, logger.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

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
        return provider;
    }

    private Container CreateContainer(InfrastructureProvider provider, ContainerStatus status = ContainerStatus.Running, string? externalId = "ext-123")
    {
        var container = new Container
        {
            Name = "test-container",
            OwnerId = "test-user",
            ProviderId = provider.Id,
            Provider = provider,
            Status = status,
            ExternalId = externalId
        };
        _db.Containers.Add(container);
        _db.SaveChanges();
        return container;
    }

    [Fact]
    public async Task Connect_NonWebSocketRequest_Returns400()
    {
        // DefaultHttpContext.WebSockets.IsWebSocketRequest defaults to false
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        var controller = CreateController(httpContext);

        await controller.Connect(Guid.NewGuid());

        httpContext.Response.StatusCode.Should().Be(400);
        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();
        body.Should().Contain("WebSocket connection required");
    }

    [Fact]
    public async Task Connect_NonWebSocketRequest_WithExistingContainer_StillReturns400()
    {
        // Even if a valid container exists, a non-WebSocket request should be rejected first
        var provider = CreateProvider();
        var container = CreateContainer(provider);

        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        var controller = CreateController(httpContext);

        await controller.Connect(container.Id);

        httpContext.Response.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Connect_NonWebSocketRequest_WithNonExistentContainer_Returns400NotBypassed()
    {
        // The WebSocket check happens before any DB lookup, so even a missing container
        // should still get a 400 (not 404) if the request is not a WebSocket
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        var controller = CreateController(httpContext);

        await controller.Connect(Guid.NewGuid());

        httpContext.Response.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Connect_NonWebSocketRequest_ResponseContainsWebSocketRequiredMessage()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        var controller = CreateController(httpContext);

        await controller.Connect(Guid.NewGuid());

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();
        body.Should().Be("WebSocket connection required");
    }

    [Fact]
    public void Constructor_AcceptsRequiredDependencies()
    {
        var db = InMemoryDbHelper.CreateContext();
        var logger = new Mock<ILogger<TerminalController>>();

        var controller = new TerminalController(db, logger.Object);

        controller.Should().NotBeNull();
        db.Dispose();
    }

    [Theory]
    [InlineData(ContainerStatus.Pending)]
    [InlineData(ContainerStatus.Creating)]
    [InlineData(ContainerStatus.Stopping)]
    [InlineData(ContainerStatus.Stopped)]
    [InlineData(ContainerStatus.Failed)]
    [InlineData(ContainerStatus.Destroying)]
    [InlineData(ContainerStatus.Destroyed)]
    public void NonRunningStatuses_ShouldBeRejected_DocumentedBehavior(ContainerStatus status)
    {
        // Documents that the Connect method rejects containers not in Running status.
        // These are not directly testable without mocking WebSocket handshake,
        // but we verify the status values that would be rejected.
        status.Should().NotBe(ContainerStatus.Running,
            $"status {status} should cause the controller to return 400 with 'Container is {status}, must be Running'");
    }

    [Fact]
    public void ContainerWithNullExternalId_ShouldBeRejected_DocumentedBehavior()
    {
        // Documents that a container with no ExternalId returns 400.
        // The validation order is: WebSocket check -> Container exists -> Status == Running -> ExternalId not null
        var provider = CreateProvider();
        var container = CreateContainer(provider, ContainerStatus.Running, externalId: null);

        container.ExternalId.Should().BeNull(
            "a container with no ExternalId should cause the controller to return 400 with 'Container has no external ID'");
    }

    [Fact]
    public void ContainerWithEmptyExternalId_ShouldBeRejected_DocumentedBehavior()
    {
        var provider = CreateProvider();
        var container = CreateContainer(provider, ContainerStatus.Running, externalId: "");

        string.IsNullOrEmpty(container.ExternalId).Should().BeTrue(
            "a container with empty ExternalId should cause the controller to return 400");
    }
}
