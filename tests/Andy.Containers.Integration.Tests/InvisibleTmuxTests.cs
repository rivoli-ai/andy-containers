using System.Text;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Controllers;
using Andy.Containers.Infrastructure.Providers.Local;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Containers.Integration.Tests;

/// <summary>
/// End-to-end behaviour tests for the invisible-tmux session
/// architecture. Conductor #875 PR 2 shipped the multiplexer swap;
/// these tests pin the two user-visible properties that justified
/// the swap:
///
/// <list type="number">
///   <item>
///     <description>
///       <b>Invisibility</b> — tmux is configured to render exactly
///       like a bare bash. No status bar at the bottom of the
///       screen, no `[web]` session label, no tmux UI characters
///       leaking into the WS stream.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Scrollback replay</b> — closing and reopening a session
///       lands the user back at the exact same view, with the
///       previous output still visible. dtach (the previous
///       persistence layer) had no buffer to replay, so reattach
///       showed a blank screen until the user typed; tmux replays
///       up to <c>history-limit</c> lines automatically.
///     </description>
///   </item>
/// </list>
///
/// Requires: Docker daemon running, <c>alpine:latest</c> pullable,
/// <c>apk add</c> network reachable for the in-test tmux install.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Docker")]
public class InvisibleTmuxTests : IAsyncLifetime
{
    private readonly DockerInfrastructureProvider _provider;
    private string? _externalId;

    public InvisibleTmuxTests()
    {
        _provider = new DockerInfrastructureProvider(
            null,
            NullLoggerFactory.Instance.CreateLogger<DockerInfrastructureProvider>());
    }

    public async Task InitializeAsync()
    {
        // Spin up a minimal alpine container, then install tmux +
        // bash inline. The desktop images bake tmux into /etc but
        // those are heavyweight to spin up for a unit-scoped test —
        // installing here keeps the test self-contained.
        var spec = new ContainerSpec
        {
            Name = $"tmux-test-{Guid.NewGuid().ToString()[..8]}",
            ImageReference = "alpine:latest",
            Resources = new ResourceSpec { CpuCores = 1, MemoryMb = 256 }
        };
        var created = await _provider.CreateContainerAsync(spec, CancellationToken.None);
        _externalId = created.ExternalId;

        var install = await _provider.ExecAsync(
            _externalId!,
            "apk add --no-cache tmux bash >/dev/null 2>&1",
            CancellationToken.None);
        install.ExitCode.Should().Be(0,
            "test container needs tmux + bash for the invisible-tmux session to start");
    }

    public async Task DisposeAsync()
    {
        if (_externalId is not null)
        {
            try { await _provider.DestroyContainerAsync(_externalId, CancellationToken.None); }
            catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task TmuxStatusBarIsInvisible()
    {
        // Open a real session via the production shell command —
        // exercises the full chain (BuildContainerShellCommand →
        // tmux conf lazy-create → tmux new-session -A) end-to-end.
        await using var session = await OpenProductionSessionAsync();

        // Let the prompt + first paint settle. With tmux's status
        // bar OFF, nothing further should appear on its own — no
        // status-interval redraw cycle, no `[web]` window label.
        var initial = await DrainAsync(session, TimeSpan.FromMilliseconds(1000));
        initial.Should().NotBeEmpty(
            "the shell should at least emit its initial prompt before we can assert invisibility");

        var rendered = Encoding.UTF8.GetString(initial);
        rendered.Should().NotContain("[web]",
            "tmux's default status bar shows the session name in brackets — " +
            "if that string appears, `set -g status off` regressed");
        rendered.Should().NotContain("0:bash",
            "tmux's default window list format `0:bash*` must not appear — " +
            "again that's a status-bar leak");
    }

    [Fact]
    public async Task ScrollbackSurvivesReconnect()
    {
        // Mark a unique string so we can search for it on reattach
        // without false positives from the prompt or banner.
        var marker = $"MARK-{Guid.NewGuid().ToString()[..8].ToUpperInvariant()}";

        // First attach: print the marker, then close the session.
        await using (var first = await OpenProductionSessionAsync())
        {
            await DrainAsync(first, TimeSpan.FromMilliseconds(700)); // settle prompt
            var cmd = Encoding.UTF8.GetBytes($"echo {marker}\r");
            await first.WriteAsync(cmd, CancellationToken.None);
            // Wait long enough for `echo` to run and the output to
            // land in tmux's scrollback buffer.
            await DrainAsync(first, TimeSpan.FromMilliseconds(700));
        }

        // Second attach to the same container — tmux has-session -t
        // web should succeed, new-session -A should attach to the
        // existing session, and the marker should appear in the
        // replayed scrollback.
        await using var second = await OpenProductionSessionAsync();
        var replay = await DrainAsync(second, TimeSpan.FromSeconds(2));
        var rendered = Encoding.UTF8.GetString(replay);
        rendered.Should().Contain(
            marker,
            "tmux must replay scrollback from the previous attach so the " +
            "user lands back at the exact same view — that's the whole " +
            "point of the invisible-tmux design");
    }

    /// <summary>
    /// Opens an exec session running the production shell command —
    /// the same chain Conductor's WS handler would run for a real
    /// terminal attach.
    /// </summary>
    private async Task<IInteractiveExecSession> OpenProductionSessionAsync()
    {
        var shellCmd = TerminalController.BuildContainerShellCommand(rows: 40, cols: 120);
        var session = await _provider.OpenInteractiveExecAsync(
            externalId: _externalId!,
            command: new[] { "/bin/bash", "-c", shellCmd },
            user: "root",
            workingDirectory: "/root",
            cols: 120,
            rows: 40,
            ct: CancellationToken.None);
        session.Should().NotBeNull("Docker provider should support daemon-PTY exec");
        return session!;
    }

    /// <summary>
    /// Polls <see cref="IInteractiveExecSession.ReadAsync"/> with
    /// short individual timeouts until <paramref name="window"/>
    /// elapses. Returns whatever bytes accumulated.
    /// </summary>
    private static async Task<byte[]> DrainAsync(IInteractiveExecSession session, TimeSpan window)
    {
        using var ms = new MemoryStream();
        var buf = new byte[4096];
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            try
            {
                var n = await session.ReadAsync(buf, 0, buf.Length, cts.Token);
                if (n <= 0) break;
                ms.Write(buf, 0, n);
            }
            catch (OperationCanceledException) { /* poll window expired, loop */ }
        }
        return ms.ToArray();
    }
}
