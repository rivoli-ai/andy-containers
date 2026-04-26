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
/// network namespace; we own a single bidirectional byte stream
/// (the multiplexed stream returned by the exec attach call) and
/// route resize through the daemon's
/// <c>ResizeContainerExecTtyAsync</c> API. SIGWINCH propagates
/// down to the inner shell automatically because the daemon
/// manages the PTY end-to-end.
///
/// Replaces the legacy chain
/// <c>script + docker exec -it + bash</c> for Docker containers,
/// which couldn't propagate resize because <c>script</c> owned
/// its own master FD that we had no handle on.
/// </summary>
internal sealed class DockerInteractiveExecSession : IInteractiveExecSession
{
    private readonly DockerClient _client;
    private readonly string _execId;
    private readonly MultiplexedStream _stream;
    private readonly ILogger _logger;
    private bool _disposed;

    public DockerInteractiveExecSession(
        DockerClient client,
        string execId,
        MultiplexedStream stream,
        ILogger logger)
    {
        _client = client;
        _execId = execId;
        _stream = stream;
        _logger = logger;
    }

    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        // With Tty=true the multiplexed stream collapses to a raw
        // byte stream — stdout / stderr merge in the PTY. Each
        // ReadAsync returns whatever bytes the inner shell has
        // emitted since the last call.
        var result = await _stream.ReadOutputAsync(buffer, offset, count, ct).ConfigureAwait(false);
        return result.Count;
    }

    public Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        return _stream.WriteAsync(buffer.ToArray(), 0, buffer.Length, ct);
    }

    public async Task ResizeAsync(int cols, int rows, CancellationToken ct)
    {
        // Daemon-side resize. Docker propagates SIGWINCH to the
        // exec process, which propagates to its child (bash, then
        // tmux/dtach/etc.). This is what makes the resize chain
        // work without our own PTY fork.
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
            _stream.Dispose();
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
