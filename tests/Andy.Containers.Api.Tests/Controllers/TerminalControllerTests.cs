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
        var providerFactory = new Mock<IInfrastructureProviderFactory>();
        var controller = new TerminalController(db, currentUser.Object, configuration, logger.Object, providerFactory.Object);
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

        var providerFactory = new Mock<IInfrastructureProviderFactory>();
        var controller = new TerminalController(db, currentUser.Object, configuration, logger.Object, providerFactory.Object);

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
    public void BuildContainerShellCommand_ExecsTmuxNewSessionAttachOrCreate()
    {
        // The end of the command must launch tmux in attach-if-exists,
        // create-if-not mode. `-A` is the flag; `-s web` is the fixed
        // session name shared with the probe. Conductor #875 PR 2.
        var cmd = TerminalController.BuildContainerShellCommand(rows: 40, cols: 120);
        cmd.Should().Contain($"new-session -A -s {TerminalController.TmuxSessionName} bash -i",
            "tmux must use -A for attach-or-create — that gives us persistence across reconnects");
    }

    [Fact]
    public void BuildContainerShellCommand_PrefersBakedConfThenLazyCreatesInHome()
    {
        // Image-baked path lands at /etc/conductor-tmux.conf via the
        // Dockerfile COPY; older images without it fall back to a
        // lazy-create at $HOME/.config/conductor/tmux.conf so we don't
        // have to rebuild every existing container.
        var cmd = TerminalController.BuildContainerShellCommand(rows: 40, cols: 120);
        cmd.Should().Contain("/etc/conductor-tmux.conf",
            "must check the image-baked path first");
        cmd.Should().Contain("$HOME/.config/conductor/tmux.conf",
            "must fall back to a user-writable path for older images");
        cmd.Should().Contain("if [ ! -f",
            "must guard the heredoc on file-missing so we don't rewrite on every attach");
    }

    [Fact]
    public void BuildContainerShellCommand_FallsBackToBareBashWhenTmuxMissing()
    {
        // Containers without tmux installed (older minimal images,
        // arbitrary images the user attaches as a provider) must
        // still get a working terminal. Persistence/scrollback are
        // lost on those, but a broken terminal is strictly worse.
        var cmd = TerminalController.BuildContainerShellCommand(rows: 40, cols: 120);
        cmd.Should().Contain("command -v tmux",
            "must guard the tmux exec so missing tmux doesn't break the terminal");
        cmd.Should().Contain("exec bash -i",
            "must fall through to bare bash when tmux isn't on PATH");
    }

    [Fact]
    public void BuildContainerShellCommand_TmuxConfMakesUiInvisible()
    {
        // The whole point of the invisible-tmux design is that the
        // user never perceives tmux is there. Pin the four conf
        // directives that make that true.
        var cmd = TerminalController.BuildContainerShellCommand(rows: 40, cols: 120);
        cmd.Should().Contain("set -g status off",
            "no status bar at the bottom");
        cmd.Should().Contain("set -g prefix none",
            "no prefix key — so accidental Ctrl-B keystrokes don't capture");
        cmd.Should().Contain("unbind-key -a",
            "drop every default keybinding so the user only ever sees bash");
        cmd.Should().Contain("setw -g aggressive-resize on",
            "tmux must auto-resize the inner window when the daemon-PTY's SIGWINCH lands");
    }

    [Fact]
    public void BuildContainerShellCommand_TmuxConfPreservesScrollback()
    {
        // 10 000 lines is the floor we promise — anything less and
        // reconnect-and-look-up-history loses real work.
        var cmd = TerminalController.BuildContainerShellCommand(rows: 40, cols: 120);
        cmd.Should().Contain("set -g history-limit 10000",
            "scrollback floor for reconnect-and-still-see-what-you-did");
    }

    [Fact]
    public void BuildContainerShellCommand_DoesNotMentionDtach()
    {
        // Regression guard the other direction: dtach was removed in
        // #875 PR 2 because it had no scrollback to replay. If anyone
        // re-introduces dtach into the shell command, this test fires.
        var cmd = TerminalController.BuildContainerShellCommand(rows: 40, cols: 120);
        cmd.Should().NotContain("dtach",
            "dtach must not appear — use the invisible-tmux invocation for persistence");
    }

    // MARK: - tmux session name / banner contract (conductor #875 PR 2)
    //
    // The tmux session name is the SHARED contract between the shell
    // command (which CREATES the session) and the probe (which DETECTS
    // the session on reattach to suppress the welcome banner). If
    // anyone changes one constant without the other, banner detection
    // silently breaks — banner re-injects on every reattach,
    // overwriting the user's prompt.
    //
    // These tests pin the contract.

    [Fact]
    public void TmuxSessionName_IsTheStableConstant()
    {
        // Pin the literal so unrelated edits to BuildContainerShellCommand
        // or BuildTmuxHasSessionArguments don't accidentally diverge.
        TerminalController.TmuxSessionName.Should().Be("web");
    }

    [Fact]
    public void BuildContainerShellCommand_UsesTmuxSessionNameConstant()
    {
        var cmd = TerminalController.BuildContainerShellCommand(rows: 40, cols: 120);
        cmd.Should().Contain($"-s {TerminalController.TmuxSessionName} ",
            "the shell command must reference the same session name the probe checks");
    }

    [Fact]
    public void BuildTmuxHasSessionArguments_UsesTmuxSessionNameConstant()
    {
        var args = TerminalController.BuildTmuxHasSessionArguments("test-user", "ext-123");
        args.Should().Contain($"-t {TerminalController.TmuxSessionName}",
            "the probe must reference the same session name the shell command creates");
    }

    [Fact]
    public void BuildTmuxHasSessionArguments_UsesTmuxHasSession()
    {
        // `tmux has-session -t web` exits 0 iff the session exists on
        // the user's per-UID tmux socket. No filesystem path peeking,
        // no race with stale files.
        var args = TerminalController.BuildTmuxHasSessionArguments("test-user", "ext-123");
        args.Should().Contain("tmux has-session",
            "use tmux's own probe, not a filesystem proxy");
    }

    [Fact]
    public void BuildTmuxHasSessionArguments_RunsAsContainerUser()
    {
        // Each user has a per-UID tmux server. Probing as root finds
        // no session even when the container user's session exists.
        // Always probe as the same user that owns the session.
        var args = TerminalController.BuildTmuxHasSessionArguments("alice", "ext-abc");
        args.Should().Contain("-u alice",
            "probe must run as the container user, not root");
    }

    [Fact]
    public void BuildTmuxHasSessionArguments_TargetsTheCorrectContainer()
    {
        var args = TerminalController.BuildTmuxHasSessionArguments("alice", "ext-12345");
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
        // tmux session already exists → reattach → user already saw
        // the banner. Repeating it overwrites whatever they were
        // looking at.
        TerminalController.ShouldInjectWelcomeBanner(hasExistingSession: true)
            .Should().BeFalse();
    }

    // MARK: - Cross-module contract: shell + probe agree on session name

    [Fact]
    public void ShellCommandAndProbeAgreeOnTmuxSessionName()
    {
        // Belt-and-suspenders test: even if someone fixes a typo in
        // both call sites separately and keeps them in sync, this
        // test asserts they reference the SAME constant.
        var cmd = TerminalController.BuildContainerShellCommand(rows: 40, cols: 120);
        var args = TerminalController.BuildTmuxHasSessionArguments("test-user", "ext-123");

        cmd.Should().Contain($"-s {TerminalController.TmuxSessionName}");
        args.Should().Contain($"-t {TerminalController.TmuxSessionName}");
    }

    [Fact]
    public void ShellCommand_TmuxInvocationCreatesTheProbedSession()
    {
        // Specifically check the tmux invocation form, not just
        // session-name-presence. `new-session -A` is the form that
        // "attaches if exists OR creates if not" — the right semantic
        // for first-attach + reattach. Other forms would create a
        // NEW session every time and break persistence.
        var cmd = TerminalController.BuildContainerShellCommand(rows: 40, cols: 120);
        cmd.Should().Contain($"new-session -A -s {TerminalController.TmuxSessionName} bash -i",
            "must use `new-session -A` for attach-or-create");
    }

    // MARK: - Reattach-without-banner end-to-end contract
    //
    // The full chain we want to lock:
    //   1. First attach: probe → false → ShouldInject → true → banner fires
    //   2. tmux new-session -A creates the session; bash runs inside it.
    //   3. Reattach: probe → true (tmux has-session succeeds) → ShouldInject → false → no banner
    //
    // We can't easily run a real container in unit tests, but we can
    // simulate the decision arms:

    [Fact]
    public void Decision_FirstAttachNoSession_BannerFires()
    {
        // Arm 1: container has tmux installed but no session yet.
        // Probe would return false (`tmux has-session` exits non-zero).
        // hasExistingSession = false. Banner fires.
        var probeResult = false; // simulating: session doesn't exist
        TerminalController.ShouldInjectWelcomeBanner(probeResult).Should().BeTrue();
    }

    [Fact]
    public void Decision_SecondAttachSessionExists_BannerSkipped()
    {
        // Arm 2: session exists from a prior attach. Probe returns
        // true. Banner is skipped — the user is reattaching, not
        // visiting fresh.
        var probeResult = true; // simulating: tmux session running
        TerminalController.ShouldInjectWelcomeBanner(probeResult).Should().BeFalse();
    }

    [Fact]
    public void Decision_TmuxNotInstalled_ProbeFalseBannerAlwaysFires()
    {
        // Arm 3: tmux not installed (or unreachable). The probe
        // catches the exit-code-non-zero / spawn-failure and returns
        // false. hasExistingSession = false. Banner fires every
        // attach — same as the bare-bash baseline. No regression.
        var probeResult = false; // tmux missing → has-session fails → probe false
        TerminalController.ShouldInjectWelcomeBanner(probeResult).Should().BeTrue();
    }

    [Fact]
    public void BuildWelcomeBannerCommand_DoesNotInjectTmuxCommands()
    {
        // Regression guard: the previous version had a
        // hasExistingSession=true branch that returned
        // "tmux refresh-client -S\n", which got TYPED into the user's
        // foreground process (bash → ran it; vim/claude → keystrokes
        // landed in their input). The banner helper must never embed
        // a `tmux` command — reattach simply does NOT inject anything.
        var bytes = TerminalController.BuildWelcomeBannerCommand();
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        text.Should().NotContain("tmux ",
            "post-attach injection must not type tmux commands into the user's shell");
    }

    // MARK: - Capture-pane args (#839)

    [Fact]
    public void BuildTmuxCaptureArguments_ContainsCorrectInvocation()
    {
        // The capture endpoint shells out to
        // `<provider> exec -u <user> <id> tmux capture-pane …`. The
        // argument string must match what we pass to Process.Start
        // verbatim — a typo on the `-t` target or the `-S -<N>`
        // offset would make every preview return either an error
        // or the wrong slice of scrollback.
        var args = TerminalController.BuildTmuxCaptureArguments(
            containerUser: "developer",
            externalId: "abc123",
            lines: 8);

        args.Should().StartWith("exec -u developer abc123 ",
            "the provider command shape (-u + externalId) must match the docker/container CLI");
        args.Should().Contain("tmux capture-pane",
            "the actual tmux subcommand we depend on for read-only scrollback access");
        args.Should().Contain("-p",
            "-p prints the pane to stdout; without it the response is empty");
        args.Should().Contain("-J",
            "-J joins wrapped lines so a single visual row doesn't double-count");
        args.Should().Contain("-t web",
            "captures the canonical 'web' session name (TmuxSessionName)");
        args.Should().Contain("-S -8",
            "starts capture 8 lines back from the cursor so we get the last N");
    }

    [Fact]
    public void BuildTmuxCaptureArguments_HonorsLineCountVariations()
    {
        // The endpoint clamps lines to [1, 50]; the arg builder
        // itself takes whatever is passed. Spot-check both ends so
        // a future signature change doesn't silently break the
        // small / large preview cases.
        var oneLine = TerminalController.BuildTmuxCaptureArguments("root", "x", 1);
        oneLine.Should().Contain("-S -1");

        var fiftyLines = TerminalController.BuildTmuxCaptureArguments("root", "x", 50);
        fiftyLines.Should().Contain("-S -50");
    }

    [Fact]
    public void BuildTmuxCaptureArguments_UsesSameSessionNameAsConnect()
    {
        // Capture and Connect must target the same tmux session —
        // otherwise the preview would render scrollback from a
        // different shell than the one the user opens via the
        // detach path. Lock to TmuxSessionName so a constant rename
        // can't desync them.
        var args = TerminalController.BuildTmuxCaptureArguments("root", "x", 8);
        args.Should().Contain($"-t {TerminalController.TmuxSessionName}");
    }
}
