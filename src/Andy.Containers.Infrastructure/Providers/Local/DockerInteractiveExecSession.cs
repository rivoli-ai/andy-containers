using Andy.Containers.Abstractions;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Infrastructure.Providers.Local;

/// <summary>
/// PTY-backed exec session using Docker's daemon-side exec API
/// (<c>POST /containers/{id}/exec</c> with <c>Tty=true</c>).
/// Conductor #875 PR 1.
///
/// The Docker daemon allocates the PTY inside the container's
/// network namespace; we own the wire (the bidirectional hijacked
/// HTTP stream returned by <see cref="DockerInfrastructureProvider"/>)
/// and route resize through the daemon's
/// <c>ResizeContainerExecTtyAsync</c> API. SIGWINCH propagates
/// down to the inner shell automatically because the daemon
/// manages the PTY end-to-end.
///
/// Replaces the legacy chain
/// <c>script + docker exec -it + bash</c> for Docker containers,
/// which couldn't propagate resize because <c>script</c> owned
/// its own master FD that we had no handle on.
///
/// Why a raw <see cref="Stream"/> instead of
/// <see cref="MultiplexedStream"/>? Docker.DotNet 3.125.x's
/// <c>MultiplexedStream</c> wraps a one-way <c>ChunkedReadStream</c>;
/// writes through it never reach the daemon, so keystrokes are
/// silently dropped and the kernel never echoes. The provider
/// hand-rolls the HTTP/1.1 hijack and hands us the raw bidirectional
/// socket; we own reads and writes without any wrapper.
/// </summary>
internal sealed class DockerInteractiveExecSession : IInteractiveExecSession
{
    private readonly DockerClient _client;
    private readonly string _execId;
    private readonly Stream _socket;
    private readonly ILogger _logger;
    private bool _disposed;

    public DockerInteractiveExecSession(
        DockerClient client,
        string execId,
        Stream socket,
        ILogger logger)
    {
        _client = client;
        _execId = execId;
        _socket = socket;
        _logger = logger;
    }

    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        // TTY mode: daemon emits raw bytes (no 8-byte multiplex
        // framing), so we read directly from the upgraded socket.
        return await _socket.ReadAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        await _socket.WriteAsync(buffer, ct).ConfigureAwait(false);
        await _socket.FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task ResizeAsync(int cols, int rows, CancellationToken ct)
    {
        // Daemon-side resize via a separate HTTP request. Docker
        // propagates SIGWINCH to the exec process, which propagates
        // to its child (bash, then tmux/dtach/etc.). This is what
        // makes the resize chain work without our own PTY fork.
        try
        {
            await _client.Exec.ResizeContainerExecTtyAsync(_execId, new ContainerResizeParameters
            {
                Height = (long)rows,
                Width = (long)cols,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "[PTY-EXEC] resize {Cols}x{Rows} failed for exec {ExecId}",
                cols, rows, _execId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _socket.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "[PTY-EXEC] dispose threw for exec {ExecId}",
                _execId);
        }
        await Task.CompletedTask;
    }
}
