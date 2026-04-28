using System.Text;
using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Providers.Local;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Containers.Integration.Tests;

/// <summary>
/// Regression tests for the daemon-side PTY exec path. Conductor
/// #875 PR 1.
///
/// These tests guard against a class of bug we hit early in PR 1
/// where keystrokes typed into the terminal silently never reached
/// the inner shell — Docker.DotNet 3.125.x's
/// <see cref="Docker.DotNet.MultiplexedStream"/>'s write side
/// targets a one-way <c>ChunkedReadStream</c>, so writes
/// disappeared even though they returned successfully. The fix
/// hand-rolls the HTTP/1.1 hijack via
/// <see cref="DockerInfrastructureProvider.OpenInteractiveExecAsync"/>;
/// these tests verify it actually delivers bytes both ways.
///
/// Requires: Docker daemon running (Docker Desktop or colima),
/// <c>alpine:latest</c> image pullable.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Docker")]
public class InteractiveExecPtyTests : IAsyncLifetime
{
    private readonly DockerInfrastructureProvider _provider;
    private string? _externalId;

    public InteractiveExecPtyTests()
    {
        _provider = new DockerInfrastructureProvider(
            null,
            NullLoggerFactory.Instance.CreateLogger<DockerInfrastructureProvider>());
    }

    public async Task InitializeAsync()
    {
        // Spin up a tiny alpine container — `sleep infinity` keeps
        // PID 1 alive while we exec interactive shells against it.
        var spec = new ContainerSpec
        {
            Name = $"pty-test-{Guid.NewGuid().ToString()[..8]}",
            ImageReference = "alpine:latest",
            Resources = new ResourceSpec { CpuCores = 1, MemoryMb = 128 }
        };
        var created = await _provider.CreateContainerAsync(spec, CancellationToken.None);
        _externalId = created.ExternalId;
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
    public async Task SingleByteWrite_TriggersKernelEcho()
    {
        await using var session = await OpenSessionAsync();

        // Drain the initial prompt so it doesn't bleed into the
        // assertion. ~700 ms is enough for `/bin/sh -i` to print
        // its first prompt + cursor-position-report sequence.
        var initial = await DrainAsync(session, TimeSpan.FromMilliseconds(700));
        initial.Should().NotBeEmpty(
            "the shell should at least emit its initial prompt — if this fails, " +
            "the read direction is broken too");

        // The actual regression: writing a single byte must reach
        // the inner shell, which the kernel echoes back unchanged
        // (PTY default termios has ECHO on). Before PR 1's hijack
        // fix, this byte never arrived and the read pump sat idle
        // forever.
        var key = new byte[] { (byte)'g' };
        await session.WriteAsync(key, CancellationToken.None);

        var echo = await DrainAsync(session, TimeSpan.FromSeconds(2));
        echo.Should().NotBeEmpty(
            "kernel echo of a single keystroke should arrive within 2 s — " +
            "if this fails, the daemon-PTY write path is broken again " +
            "(check OpenHijackedExecAttachAsync)");
    }

    [Fact]
    public async Task MultiByteWrite_FlushesBeforeNewline()
    {
        await using var session = await OpenSessionAsync();
        await DrainAsync(session, TimeSpan.FromMilliseconds(700));

        // Multi-byte payloads should also flow — guards against
        // a regression where someone might add buffering keyed on
        // payload size or threshold.
        var data = Encoding.UTF8.GetBytes("echo hi");
        await session.WriteAsync(data, CancellationToken.None);

        var echo = await DrainAsync(session, TimeSpan.FromSeconds(2));
        echo.Should().NotBeEmpty(
            "the kernel should echo the typed characters back");
    }

    [Fact]
    public async Task TenSequentialKeystrokes_AllEcho()
    {
        await using var session = await OpenSessionAsync();
        await DrainAsync(session, TimeSpan.FromMilliseconds(700));

        // Mimic the real Conductor flow: each WS frame from the
        // client is a single byte. Earlier we saw 10+ such writes
        // produce zero echoes; now we expect at least *some* echo
        // per byte (kernel sees each one).
        for (int i = 0; i < 10; i++)
        {
            await session.WriteAsync(new[] { (byte)('a' + i) }, CancellationToken.None);
            await Task.Delay(20);
        }

        var echo = await DrainAsync(session, TimeSpan.FromSeconds(2));
        echo.Length.Should().BeGreaterThanOrEqualTo(
            10,
            "10 typed characters should produce at least 10 echo bytes");
    }

    /// <summary>
    /// Opens a daemon-PTY exec session against the test container's
    /// <c>/bin/sh -i</c>. Centralised so each test can focus on
    /// the specific WriteAsync / ReadAsync behaviour being pinned.
    /// </summary>
    private async Task<IInteractiveExecSession> OpenSessionAsync()
    {
        var session = await _provider.OpenInteractiveExecAsync(
            externalId: _externalId!,
            command: new[] { "/bin/sh", "-i" },
            user: "root",
            workingDirectory: "/",
            cols: 120,
            rows: 40,
            ct: CancellationToken.None);
        session.Should().NotBeNull("Docker provider should support daemon-PTY exec");
        return session!;
    }

    /// <summary>
    /// Polls <see cref="IInteractiveExecSession.ReadAsync"/> with
    /// short individual timeouts until <paramref name="window"/>
    /// elapses. Returns whatever bytes accumulated. Used in lieu
    /// of a single long-blocking read so the test can move on
    /// when the shell is idle (no data) without hanging.
    /// </summary>
    private static async Task<byte[]> DrainAsync(IInteractiveExecSession session, TimeSpan window)
    {
        using var ms = new MemoryStream();
        var buf = new byte[4096];
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
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
