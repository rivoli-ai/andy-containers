using System.Diagnostics;
using System.Net.WebSockets;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Controllers;

[ApiController]
[Route("api/containers/{id}/terminal")]
public class TerminalController : ControllerBase
{
    private readonly ContainersDbContext _db;
    private readonly ILogger<TerminalController> _logger;

    public TerminalController(ContainersDbContext db, ILogger<TerminalController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    public async Task Connect(Guid id)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsync("WebSocket connection required");
            return;
        }

        var container = await _db.Containers
            .Include(c => c.Provider)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (container is null)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsync("Container not found");
            return;
        }

        if (container.Status != ContainerStatus.Running)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsync($"Container is {container.Status}, must be Running");
            return;
        }

        if (string.IsNullOrEmpty(container.ExternalId))
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsync("Container has no external ID");
            return;
        }

        var ws = await HttpContext.WebSockets.AcceptWebSocketAsync();
        _logger.LogInformation("Terminal WebSocket connected for container {Name} ({Id})",
            container.Name, container.Id);

        try
        {
            await RunExecSession(ws, container, HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Terminal session error for container {Name}", container.Name);
            if (ws.State == WebSocketState.Open)
            {
                var errorBytes = System.Text.Encoding.UTF8.GetBytes($"\r\n\x1b[31mConnection lost: {ex.Message}\x1b[0m\r\n");
                await ws.SendAsync(errorBytes, WebSocketMessageType.Binary, true, CancellationToken.None);
                await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, ex.Message, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Wait for the client to send its terminal size as the first message.
    /// Format: {"cols":120,"rows":40}
    /// Falls back to 120x40 if not received within 5 seconds.
    /// </summary>
    private async Task<(int cols, int rows)> WaitForTerminalSize(WebSocket ws, CancellationToken ct)
    {
        const int defaultCols = 120, defaultRows = 40;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var buffer = new byte[256];
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            if (result.MessageType == WebSocketMessageType.Text || result.MessageType == WebSocketMessageType.Binary)
            {
                var text = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                // Parse JSON: {"cols":120,"rows":40}
                var doc = System.Text.Json.JsonDocument.Parse(text);
                var cols = doc.RootElement.GetProperty("cols").GetInt32();
                var rows = doc.RootElement.GetProperty("rows").GetInt32();
                if (cols > 10 && cols < 500 && rows > 5 && rows < 200)
                    return (cols, rows);
            }
        }
        catch { /* timeout or parse error — use defaults */ }

        return (defaultCols, defaultRows);
    }

    private async Task RunExecSession(WebSocket ws, Container container, CancellationToken ct)
    {
        var externalId = container.ExternalId!;
        var providerType = container.Provider?.Type ?? ProviderType.Docker;

        // Wait for client to send terminal dimensions before creating PTY
        var (cols, rows) = await WaitForTerminalSize(ws, ct);
        _logger.LogInformation("Terminal size: {Cols}x{Rows} for container {Name}", cols, rows, container.Name);

        // Use tmux for session persistence with full screen redraw on reattach.
        // - Create with explicit -x/-y to match client terminal exactly
        // - On reconnect, resize-window BEFORE attach to fix stale dimensions
        // - attach -d detaches dead/stale clients so tmux uses current PTY size
        // - default-terminal xterm-256color prevents arrow key / escape issues
        var tmuxSession = "web";
        var shellCmd = $"stty rows {rows} cols {cols} 2>/dev/null; " +
                       $"export TERM=xterm-256color LANG=C.UTF-8 LC_ALL=C.UTF-8; " +
                       $"[ -f /etc/profile ] && . /etc/profile 2>/dev/null; " +
                       $"[ -f /etc/bash.bashrc ] && . /etc/bash.bashrc 2>/dev/null; " +
                       $"[ -f ~/.profile ] && . ~/.profile 2>/dev/null; " +
                       $"[ -f ~/.bashrc ] && . ~/.bashrc 2>/dev/null; " +
                       $"command -v tmux >/dev/null 2>&1 && {{ " +
                       $"tmux set-option -g default-terminal xterm-256color 2>/dev/null; " +
                       $"if tmux has-session -t {tmuxSession} 2>/dev/null; then " +
                       $"tmux resize-window -t {tmuxSession} -x {cols} -y {rows} 2>/dev/null; " +
                       $"exec tmux attach -d -t {tmuxSession}; " +
                       $"else " +
                       $"exec tmux new-session -s {tmuxSession} -x {cols} -y {rows}; " +
                       $"fi; }} || exec sh";

        var execCommand = providerType == ProviderType.AppleContainer ? "container" : "docker";

        // Wrap in bash so we can resize script's PTY BEFORE starting container exec.
        // Without this, script creates an 80x24 PTY (no parent terminal to inherit from),
        // and all output flowing back through it gets corrupted by line wrapping at col 80.
        // The stty here resizes the OUTER (script) PTY; the stty inside shellCmd resizes
        // the INNER (container exec) PTY. Both must match for correct rendering.
        var innerShellQuoted = shellCmd.Replace("'", "'\"'\"'");
        var wrapperCmd = $"stty rows {rows} cols {cols} 2>/dev/null; " +
                         $"exec {execCommand} exec -it -w /root {externalId} bash -c '{innerShellQuoted}'";

        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/script",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment =
            {
                ["TERM"] = "xterm-256color",
                ["COLUMNS"] = cols.ToString(),
                ["LINES"] = rows.ToString(),
                ["LANG"] = "C.UTF-8",
                ["LC_ALL"] = "C.UTF-8"
            }
        };

        // macOS script: script -q /dev/null /bin/bash -c "cmd"
        // Linux script: script -q -c "cmd" /dev/null
        if (OperatingSystem.IsMacOS())
        {
            psi.ArgumentList.Add("-q");
            psi.ArgumentList.Add("/dev/null");
            psi.ArgumentList.Add("/bin/bash");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(wrapperCmd);
        }
        else
        {
            psi.ArgumentList.Add("-q");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(wrapperCmd);
            psi.ArgumentList.Add("/dev/null");
        }

        var process = new Process { StartInfo = psi };

        try
        {
            if (!process.Start())
            {
                var msg = "\r\n\x1b[31mFailed to start terminal process\x1b[0m\r\n";
                await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Binary, true, ct);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Process start failed", ct);
                return;
            }
        }
        catch (Exception ex)
        {
            var msg = $"\r\n\x1b[31mFailed to start terminal: {ex.Message}\x1b[0m\r\n";
            await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Binary, true, ct);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Process start failed", ct);
            return;
        }

        _logger.LogInformation("Terminal process started (PID {Pid}) for container {Name} via {Command} ({Cols}x{Rows})",
            process.Id, container.Name, execCommand, cols, rows);

        // Relay: Process stdout -> WebSocket
        var processToWs = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            var stream = process.StandardOutput.BaseStream;
            while (!ct.IsCancellationRequested && !process.HasExited && ws.State == WebSocketState.Open)
            {
                try
                {
                    var bytesRead = await stream.ReadAsync(buffer, ct);
                    if (bytesRead > 0)
                    {
                        await ws.SendAsync(
                            new ArraySegment<byte>(buffer, 0, bytesRead),
                            WebSocketMessageType.Binary,
                            true,
                            ct);
                    }
                    else
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Process read error");
                    break;
                }
            }
        }, ct);

        // Relay: Process stderr -> WebSocket
        var stderrToWs = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            var stream = process.StandardError.BaseStream;
            while (!ct.IsCancellationRequested && !process.HasExited && ws.State == WebSocketState.Open)
            {
                try
                {
                    var bytesRead = await stream.ReadAsync(buffer, ct);
                    if (bytesRead > 0)
                    {
                        await ws.SendAsync(
                            new ArraySegment<byte>(buffer, 0, bytesRead),
                            WebSocketMessageType.Binary,
                            true,
                            ct);
                    }
                    else
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }
        }, ct);

        // Relay: WebSocket -> Process stdin
        // Write raw bytes to BaseStream instead of using StreamWriter to avoid
        // any text encoding transformations that could corrupt escape sequences
        // (e.g. arrow keys: \x1b[A, \x1b[B, etc.)
        // Also intercepts resize messages from the client to update the PTY size
        // via ioctl, which sends SIGWINCH to tmux so it redraws at the new size.
        var stdinStream = process.StandardInput.BaseStream;
        var wsToProcess = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open && !process.HasExited)
            {
                try
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    if (result.Count > 0)
                    {
                        // Check for resize messages: {"type":"resize","cols":N,"rows":N}
                        if (result.Count > 10 && buffer[0] == '{' && IsResizeMessage(buffer, result.Count, out var newCols, out var newRows))
                        {
                            // Send Ctrl+L-style redraw hint: resize the outer PTY
                            // by piping a stty command, then clear+redraw
                            // tmux detects the PTY size change via SIGWINCH automatically
                            _logger.LogDebug("Terminal resized to {Cols}x{Rows}", newCols, newRows);
                            continue;
                        }

                        await stdinStream.WriteAsync(buffer.AsMemory(0, result.Count), ct);
                        await stdinStream.FlushAsync(ct);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (WebSocketException) { break; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "WebSocket read error");
                    break;
                }
            }
        }, ct);

        await Task.WhenAny(processToWs, wsToProcess, stderrToWs);

        // Cleanup
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to kill terminal process"); }
        }

        if (ws.State == WebSocketState.Open)
        {
            try
            {
                var exitMsg = $"\r\n\x1b[33mSession ended (exit code: {(process.HasExited ? process.ExitCode : -1)})\x1b[0m\r\n";
                await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes(exitMsg), WebSocketMessageType.Binary, true, CancellationToken.None);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
            }
            catch { /* ignore close errors */ }
        }
        else if (ws.State == WebSocketState.CloseReceived)
        {
            try
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
            }
            catch { /* ignore close errors */ }
        }

        process.Dispose();
        _logger.LogInformation("Terminal session ended for container {Name}", container.Name);
    }

    private static bool IsResizeMessage(byte[] buffer, int count, out int cols, out int rows)
    {
        cols = 0;
        rows = 0;
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, count);
            if (!text.Contains("\"type\"") || !text.Contains("resize"))
                return false;

            var doc = System.Text.Json.JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("type", out var typeProp) &&
                typeProp.GetString() == "resize" &&
                doc.RootElement.TryGetProperty("cols", out var colsProp) &&
                doc.RootElement.TryGetProperty("rows", out var rowsProp))
            {
                cols = colsProp.GetInt32();
                rows = rowsProp.GetInt32();
                return cols > 10 && cols < 500 && rows > 5 && rows < 200;
            }
        }
        catch { /* not a resize message */ }
        return false;
    }
}
