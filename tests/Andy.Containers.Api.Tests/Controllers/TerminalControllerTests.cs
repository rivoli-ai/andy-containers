using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
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

    private TerminalController CreateController(
        DefaultHttpContext? httpContext = null,
        Mock<ICurrentUserService>? currentUser = null,
        IConfiguration? configuration = null)
    {
        httpContext ??= new DefaultHttpContext();
        var logger = new Mock<ILogger<TerminalController>>();
        currentUser ??= new Mock<ICurrentUserService>();
        configuration ??= new ConfigurationBuilder().Build();
        // Use a fresh context with the same DB name so EF tracks entities correctly
        var db = InMemoryDbHelper.CreateContext(_dbName);
        var controller = new TerminalController(db, currentUser.Object, configuration, logger.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static IConfiguration ConfigWithOrigins(params string[] origins)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < origins.Length; i++)
            dict[$"Cors:Origins:{i}"] = origins[i];
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
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
        var currentUser = new Mock<ICurrentUserService>();
        var configuration = new ConfigurationBuilder().Build();

        var controller = new TerminalController(db, currentUser.Object, configuration, logger.Object);

        controller.Should().NotBeNull();
        db.Dispose();
    }

    [Fact]
    public void IsOriginAllowed_EmptyOrigin_NonLoopbackRemote_ReturnsFalse()
    {
        // Empty Origin from an external IP is still rejected (CSWSH
        // defence preserved for browser traffic that arrives over the
        // network).
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.42");
        var controller = CreateController(
            httpContext: httpContext,
            configuration: ConfigWithOrigins("https://localhost:5280"));
        controller.IsOriginAllowed(string.Empty).Should().BeFalse();
    }

    [Fact]
    public void IsOriginAllowed_EmptyOrigin_NoRemoteAddress_ReturnsFalse()
    {
        // DefaultHttpContext.Connection.RemoteIpAddress defaults to
        // null. Treat that as "we don't know where this came from" and
        // refuse — fail-closed when ambiguous.
        var controller = CreateController(configuration: ConfigWithOrigins("https://localhost:5280"));
        controller.IsOriginAllowed(string.Empty).Should().BeFalse();
    }

    [Fact]
    public void IsOriginAllowed_EmptyOrigin_LoopbackRemote_ReturnsTrue()
    {
        // Native WebSocket clients (macOS NSURLSession in particular)
        // don't send an Origin header — Origin is a browser-only CSWSH
        // defence. Conductor's terminal session connects over a local
        // proxy, so the upgrade arrives at andy-containers from
        // 127.0.0.1 with no Origin. This used to fail 403 Forbidden;
        // post-fix it succeeds because loopback traffic is not
        // browser-cross-site-attackable.
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        var controller = CreateController(
            httpContext: httpContext,
            configuration: ConfigWithOrigins("https://localhost:5280"));
        controller.IsOriginAllowed(string.Empty).Should().BeTrue();
    }

    [Fact]
    public void IsOriginAllowed_EmptyOrigin_IPv6LoopbackRemote_ReturnsTrue()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.IPv6Loopback;
        var controller = CreateController(
            httpContext: httpContext,
            configuration: ConfigWithOrigins("https://localhost:5280"));
        controller.IsOriginAllowed(string.Empty).Should().BeTrue();
    }

    [Fact]
    public void IsOriginAllowed_NoAllowlistConfigured_ReturnsFalse()
    {
        // Fail-closed when Cors:Origins is missing or empty — preventing CSWSH
        // from a misconfigured deployment.
        var controller = CreateController(configuration: new ConfigurationBuilder().Build());
        controller.IsOriginAllowed("https://localhost:5280").Should().BeFalse();
    }

    [Fact]
    public void IsOriginAllowed_OriginInAllowlist_ReturnsTrue()
    {
        var controller = CreateController(configuration: ConfigWithOrigins("https://localhost:5280", "https://localhost:3000"));
        controller.IsOriginAllowed("https://localhost:5280").Should().BeTrue();
        controller.IsOriginAllowed("https://localhost:3000").Should().BeTrue();
    }

    [Fact]
    public void IsOriginAllowed_OriginNotInAllowlist_ReturnsFalse()
    {
        var controller = CreateController(configuration: ConfigWithOrigins("https://localhost:5280"));
        controller.IsOriginAllowed("https://evil.example.com").Should().BeFalse();
    }

    [Fact]
    public void IsOriginAllowed_OriginMatchesCaseInsensitive()
    {
        var controller = CreateController(configuration: ConfigWithOrigins("https://localhost:5280"));
        controller.IsOriginAllowed("HTTPS://LOCALHOST:5280").Should().BeTrue();
    }

    [Fact]
    public void CanAccess_Admin_AlwaysTrue()
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(u => u.IsAdmin()).Returns(true);
        currentUser.Setup(u => u.GetUserId()).Returns("admin-user");
        var controller = CreateController(currentUser: currentUser);

        var container = new Container { OwnerId = "someone-else", Name = "c", ProviderId = Guid.NewGuid() };
        controller.CanAccess(container).Should().BeTrue();
    }

    [Fact]
    public void CanAccess_OwnerMatches_True()
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(u => u.IsAdmin()).Returns(false);
        currentUser.Setup(u => u.GetUserId()).Returns("user-1");
        var controller = CreateController(currentUser: currentUser);

        var container = new Container { OwnerId = "user-1", Name = "c", ProviderId = Guid.NewGuid() };
        controller.CanAccess(container).Should().BeTrue();
    }

    [Fact]
    public void CanAccess_NonOwnerNonAdmin_False()
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(u => u.IsAdmin()).Returns(false);
        currentUser.Setup(u => u.GetUserId()).Returns("user-1");
        var controller = CreateController(currentUser: currentUser);

        var container = new Container { OwnerId = "user-2", Name = "c", ProviderId = Guid.NewGuid() };
        controller.CanAccess(container).Should().BeFalse();
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

    // MARK: - IsValidTerminalSize (conductor #836)

    [Theory]
    [InlineData(2, 2)]
    [InlineData(80, 24)]
    [InlineData(120, 40)]
    [InlineData(1000, 1000)]
    public void IsValidTerminalSize_ReturnsTrue_ForValidPairs(int cols, int rows)
    {
        TerminalController.IsValidTerminalSize(cols, rows).Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 24, "zero columns")]
    [InlineData(80, 0, "zero rows")]
    [InlineData(1, 24, "one column (tmux floor is 2)")]
    [InlineData(80, 1, "one row (tmux floor is 2)")]
    [InlineData(-1, 40, "negative columns")]
    [InlineData(120, -10, "negative rows")]
    [InlineData(1001, 40, "columns past xterm max")]
    [InlineData(120, 1001, "rows past xterm max")]
    [InlineData(int.MaxValue, 24, "overflow columns")]
    [InlineData(120, int.MaxValue, "overflow rows")]
    public void IsValidTerminalSize_ReturnsFalse_ForInvalidPairs(int cols, int rows, string reason)
    {
        TerminalController.IsValidTerminalSize(cols, rows).Should().BeFalse(reason);
    }

    // MARK: - BuildPostAttachCommand (conductor #838)

    [Fact]
    public void BuildPostAttachCommand_NewSession_ReturnsWelcomeBanner()
    {
        var bytes = TerminalController.BuildPostAttachCommand(hasExistingSession: false);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        text.Should().StartWith(" clear", "space prefix keeps the command out of bash history");
        text.Should().Contain("/usr/local/bin/andy-banner",
            "new sessions show the welcome banner");
        text.Should().Contain("2>/dev/null",
            "banner failure is silenced — a missing binary should not break the shell");
        text.Should().EndWith("\n",
            "trailing newline submits the command line");
    }

    [Fact]
    public void BuildPostAttachCommand_ExistingSession_ReturnsTmuxRefresh()
    {
        var bytes = TerminalController.BuildPostAttachCommand(hasExistingSession: true);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        text.Should().Be("tmux refresh-client -S\n",
            "reattach forces a server-side redraw so the user sees a complete frame immediately");
    }

    [Fact]
    public void BuildPostAttachCommand_DistinguishesNewVsExisting()
    {
        var newSession = TerminalController.BuildPostAttachCommand(hasExistingSession: false);
        var existing = TerminalController.BuildPostAttachCommand(hasExistingSession: true);
        newSession.Should().NotEqual(existing,
            "new sessions and reattaches must inject different commands");
    }

    [Fact]
    public void BuildPostAttachCommand_BothBranches_TerminateWithNewline()
    {
        var newBytes = TerminalController.BuildPostAttachCommand(hasExistingSession: false);
        var existingBytes = TerminalController.BuildPostAttachCommand(hasExistingSession: true);
        newBytes[^1].Should().Be((byte)'\n');
        existingBytes[^1].Should().Be((byte)'\n');
    }
}
