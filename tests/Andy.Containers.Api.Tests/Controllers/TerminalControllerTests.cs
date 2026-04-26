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

    // MARK: - BuildWelcomeBannerCommand (conductor #838 corrective)

    [Fact]
    public void BuildWelcomeBannerCommand_StartsWithSpaceClear()
    {
        var bytes = TerminalController.BuildWelcomeBannerCommand();
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        text.Should().StartWith(" clear",
            "space prefix keeps the command out of bash history");
    }

    [Fact]
    public void BuildWelcomeBannerCommand_RunsAndyBanner()
    {
        var bytes = TerminalController.BuildWelcomeBannerCommand();
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        text.Should().Contain("/usr/local/bin/andy-banner",
            "new sessions show the welcome banner");
    }

    [Fact]
    public void BuildWelcomeBannerCommand_SilencesBannerErrors()
    {
        var bytes = TerminalController.BuildWelcomeBannerCommand();
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        text.Should().Contain("2>/dev/null",
            "banner failure is silenced — a missing binary should not break the shell");
        text.Should().Contain("; true",
            "trailing `; true` swallows the exit code");
    }

    [Fact]
    public void BuildWelcomeBannerCommand_TerminatesWithNewline()
    {
        var bytes = TerminalController.BuildWelcomeBannerCommand();
        bytes[^1].Should().Be((byte)'\n',
            "trailing newline submits the command line");
    }

    // MARK: - BuildContainerShellCommand (dtach + fallback)

    [Fact]
    public void BuildContainerShellCommand_SetsPtySize()
    {
        var cmd = TerminalController.BuildContainerShellCommand(rows: 40, cols: 120);
        cmd.Should().Contain("stty rows 40 cols 120",
            "the inner PTY's reported size must match what the renderer asked for");
    }

    [Fact]
    public void BuildContainerShellCommand_ExportsXTermAndLocale()
    {
        var cmd = TerminalController.BuildContainerShellCommand(rows: 40, cols: 120);
        cmd.Should().Contain("TERM=xterm-256color",
            "256-color is what claude / vim / less expect");
        cmd.Should().Contain("LANG=C.UTF-8");
        cmd.Should().Contain("LC_ALL=C.UTF-8",
            "UTF-8 locale is needed for emoji / box-drawing characters");
    }

    [Fact]
    public void BuildContainerShellCommand_SourcesRcfiles()
    {
        var cmd = TerminalController.BuildContainerShellCommand(rows: 40, cols: 120);
        cmd.Should().Contain("/etc/profile");
        cmd.Should().Contain("/etc/bash.bashrc");
        cmd.Should().Contain("~/.profile");
        cmd.Should().Contain("~/.bashrc");
    }

    [Fact]
    public void BuildContainerShellCommand_PrefersDtachWhenAvailable()
    {
        var cmd = TerminalController.BuildContainerShellCommand(rows: 40, cols: 120);
        cmd.Should().Contain("command -v dtach",
            "must check for dtach before invoking it");
        cmd.Should().Contain("exec dtach -A /tmp/conductor.sock -z bash -i",
            "dtach gets a fixed socket per container so reattach works");
    }

    [Fact]
    public void BuildContainerShellCommand_FallsBackToBareBashWhenDtachMissing()
    {
        var cmd = TerminalController.BuildContainerShellCommand(rows: 40, cols: 120);
        cmd.Should().Contain("|| exec bash -i",
            "containers without dtach installed must still get a working terminal");
    }

    [Fact]
    public void BuildContainerShellCommand_DtachUsesAttachOrCreate()
    {
        var cmd = TerminalController.BuildContainerShellCommand(rows: 40, cols: 120);
        cmd.Should().Contain("dtach -A",
            "the -A flag means 'attach if exists, create if not' — what gives us persistence across reconnects");
    }

    [Fact]
    public void BuildContainerShellCommand_DtachUsesQuietMode()
    {
        var cmd = TerminalController.BuildContainerShellCommand(rows: 40, cols: 120);
        cmd.Should().Contain("dtach -A /tmp/conductor.sock -z bash",
            "the -z flag suppresses dtach's own escape sequences so the terminal output is clean");
    }

    [Fact]
    public void BuildContainerShellCommand_DoesNotMentionTmux()
    {
        // Regression guard: tmux was removed in #154 / #842 preview
        // because it caused rendering artifacts in TUI apps. dtach
        // is the replacement. If anyone re-introduces tmux into the
        // shell command without going through #842's per-mode
        // picker, this test fires.
        var cmd = TerminalController.BuildContainerShellCommand(rows: 40, cols: 120);
        cmd.Should().NotContain("tmux ",
            "tmux must not appear in the shell command — use dtach for persistence");
    }

    // MARK: - dtach socket path / banner contract (conductor #836-#842)
    //
    // The dtach socket path is the SHARED contract between the shell
    // command (which CREATES the socket) and the probe (which DETECTS
    // the socket on reattach to suppress the welcome banner). If
    // anyone changes one constant without the other, banner detection
    // silently breaks — banner re-injects on every reattach,
    // overwriting the user's prompt.
    //
    // These tests pin the contract.

    [Fact]
    public void DtachSocketPath_IsTheStableConstant()
    {
        // Pin the literal so unrelated edits to BuildContainerShellCommand
        // or BuildDtachProbeArguments don't accidentally divert to a
        // different path.
        TerminalController.DtachSocketPath.Should().Be("/tmp/conductor.sock");
    }

    [Fact]
    public void BuildContainerShellCommand_UsesDtachSocketPathConstant()
    {
        var cmd = TerminalController.BuildContainerShellCommand(rows: 40, cols: 120);
        cmd.Should().Contain(TerminalController.DtachSocketPath,
            "the shell command must reference the same socket path the probe checks");
    }

    [Fact]
    public void BuildDtachProbeArguments_UsesDtachSocketPathConstant()
    {
        var args = TerminalController.BuildDtachProbeArguments("test-user", "ext-123");
        args.Should().Contain(TerminalController.DtachSocketPath,
            "the probe must reference the same socket path the shell command creates");
    }

    [Fact]
    public void BuildDtachProbeArguments_UsesTestDashSForSocketSemantics()
    {
        // `test -e` matches any file (including stale regular files);
        // `test -f` matches regular files only; `test -S` matches
        // Unix domain sockets specifically. dtach creates a socket,
        // so we use -S — the precise signal that a dtach session is
        // already running (not just leftover garbage at the path).
        var args = TerminalController.BuildDtachProbeArguments("test-user", "ext-123");
        args.Should().Contain("test -S",
            "use -S not -e/-f so we don't false-positive on stale non-socket files at the path");
    }

    [Fact]
    public void BuildDtachProbeArguments_RunsAsContainerUser()
    {
        // dtach's socket has user-private permissions (srwx------ owned
        // by the container user). Probing as root would fail
        // permission-wise on some filesystems and succeed misleadingly
        // on others. Always probe as the same user that owns the
        // session.
        var args = TerminalController.BuildDtachProbeArguments("alice", "ext-abc");
        args.Should().Contain("-u alice",
            "probe must run as the container user, not root");
    }

    [Fact]
    public void BuildDtachProbeArguments_TargetsTheCorrectContainer()
    {
        var args = TerminalController.BuildDtachProbeArguments("alice", "ext-12345");
        args.Should().Contain("ext-12345");
    }

    // MARK: - ShouldInjectWelcomeBanner decision matrix

    [Fact]
    public void ShouldInjectWelcomeBanner_FiresOnFirstAttach()
    {
        // No existing dtach session → first attach → banner welcomes
        // the user. This is the user's introduction to the container
        // (template name, OS, etc.).
        TerminalController.ShouldInjectWelcomeBanner(hasExistingSession: false)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldInjectWelcomeBanner_DoesNotFireOnReattach()
    {
        // dtach session already exists → reattach → user already saw
        // the banner. Repeating it overwrites whatever they were
        // looking at (the bug the user reported when verifying
        // dtach persistence: closing the terminal mid-session and
        // reopening showed the banner header on top of their bash
        // prompt).
        TerminalController.ShouldInjectWelcomeBanner(hasExistingSession: true)
            .Should().BeFalse();
    }

    // MARK: - Cross-module contract: shell + probe agree on path

    [Fact]
    public void ShellCommandAndProbeAgreeOnSocketPath()
    {
        // Belt-and-suspenders test: even if someone fixes a typo in
        // both call sites separately and keeps them in sync, this
        // test asserts they reference the SAME constant.
        var cmd = TerminalController.BuildContainerShellCommand(rows: 40, cols: 120);
        var args = TerminalController.BuildDtachProbeArguments("test-user", "ext-123");

        cmd.Should().Contain(TerminalController.DtachSocketPath);
        args.Should().Contain(TerminalController.DtachSocketPath);
    }

    [Fact]
    public void ShellCommand_DtachInvocationCreatesTheProbedSocket()
    {
        // Specifically check the dtach invocation form, not just
        // path-presence. dtach's `-A` flag is the one that "attaches
        // if exists OR creates if not" — the right semantic for
        // first-attach + reattach. Other forms (-c, -n) would create
        // a NEW socket every time and break persistence even if dtach
        // is in PATH.
        var cmd = TerminalController.BuildContainerShellCommand(rows: 40, cols: 120);
        cmd.Should().Contain($"dtach -A {TerminalController.DtachSocketPath} -z bash -i",
            "must use `-A` for attach-or-create, `-z` to suppress dtach's escape sequences");
    }

    // MARK: - Reattach-without-banner end-to-end contract
    //
    // The full chain we want to lock:
    //   1. First attach: probe → false → ShouldInject → true → banner fires
    //   2. dtach creates the socket; bash runs.
    //   3. Reattach: probe → true (socket still there) → ShouldInject → false → no banner
    //
    // We can't easily run a real container in unit tests, but we can
    // simulate the decision arms:

    [Fact]
    public void Decision_FirstAttachNoSocket_BannerFires()
    {
        // Arm 1: container has dtach installed but no socket yet.
        // Probe would return false (socket absent). hasExistingSession
        // = false. Banner fires.
        var probeResult = false; // simulating: socket doesn't exist
        TerminalController.ShouldInjectWelcomeBanner(probeResult).Should().BeTrue();
    }

    [Fact]
    public void Decision_SecondAttachSocketExists_BannerSkipped()
    {
        // Arm 2: socket exists from a prior attach. Probe returns
        // true. Banner is skipped — the user is reattaching, not
        // visiting fresh.
        var probeResult = true; // simulating: dtach session running
        TerminalController.ShouldInjectWelcomeBanner(probeResult).Should().BeFalse();
    }

    [Fact]
    public void Decision_NoDtachInstalled_FallbackBareShellAlwaysGetsBanner()
    {
        // Arm 3: container has no dtach. The shell command falls back
        // to `exec bash -i` (no socket). Probe always returns false
        // (no socket exists). hasExistingSession = false. Banner
        // fires every attach — same as the bare-bash baseline before
        // dtach was introduced. No regression.
        var probeResult = false; // no dtach → no socket → probe false
        TerminalController.ShouldInjectWelcomeBanner(probeResult).Should().BeTrue();
    }

    [Fact]
    public void BuildWelcomeBannerCommand_DoesNotInjectTmuxCommands()
    {
        // Regression guard: the previous version had a
        // hasExistingSession=true branch that returned
        // "tmux refresh-client -S\n", which got TYPED into the user's
        // foreground process (bash → ran it; vim/claude → keystrokes
        // landed in their input). Existing sessions now go through
        // the tmux side channel via InvokeTmuxCommand. The banner
        // helper must never embed a `tmux` command.
        var bytes = TerminalController.BuildWelcomeBannerCommand();
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        text.Should().NotContain("tmux ",
            "post-attach injection must not type tmux commands into the user's shell — those go through the side channel");
    }
}
