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

        // Build the exec arguments based on provider type
        // 1. Set PTY size via stty so tmux picks up the correct dimensions
        // 2. Export locale for proper character rendering
        // 3. unset TMUX prevents nesting when reattaching to an existing session
        // 4. After tmux attach, force-resize the window to match the client PTY
        //    (tmux sessions remember their creation size; -A reattach keeps the old size)
        var tmuxCmd = $"unset TMUX; tmux set-option -g default-size {cols}x{rows} 2>/dev/null; " +
                      $"tmux new-session -A -s web \\; resize-window -x {cols} -y {rows}";
        const string fallbackCmd = "bash -l";
        var shellCmd = $"stty rows {rows} cols {cols} 2>/dev/null; " +
                       $"export LANG=C.UTF-8 LC_ALL=C.UTF-8; " +
                       $"command -v tmux >/dev/null 2>&1 && {{ {tmuxCmd}; }} || {fallbackCmd}";

        var execArgs = providerType switch
        {
            ProviderType.AppleContainer => new[] { "exec", "-it", "-w", "/root", externalId, "bash", "-c", shellCmd },
            ProviderType.Docker => new[] { "exec", "-it", "-w", "/root", externalId, "bash", "-c", shellCmd },
            _ => new[] { "exec", "-it", "-w", "/root", externalId, "bash", "-c", shellCmd }
        };
        var execCommand = providerType == ProviderType.AppleContainer ? "container" : "docker";

        // Use 'script' to allocate a PTY with the client's actual terminal size
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/script",
            ArgumentList = { "-q", "/dev/null", execCommand },
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

        foreach (var arg in execArgs)
            psi.ArgumentList.Add(arg);

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

                    if (result.MessageType == WebSocketMessageType.Text || result.MessageType == WebSocketMessageType.Binary)
                    {
                        var data = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);

                        // Ignore resize messages — PTY size is fixed at creation
                        if (data.StartsWith("\x1b[R") && data.Contains(';'))
                            continue;

                        await process.StandardInput.WriteAsync(data);
                        await process.StandardInput.FlushAsync();
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
}
