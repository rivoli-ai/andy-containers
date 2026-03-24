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

    private async Task RunExecSession(WebSocket ws, Container container, CancellationToken ct)
    {
        var externalId = container.ExternalId!;
        var providerType = container.Provider?.Type ?? ProviderType.Docker;

        // Build the exec arguments based on provider type
        // Use -w /root to start in the home directory
        // Use tmux for session persistence — reconnects to existing session if one exists
        // Falls back to bash if tmux is not available
        const string tmuxCmd = "tmux new-session -A -s web";
        const string fallbackCmd = "bash -l";
        var shellCmd = $"command -v tmux >/dev/null 2>&1 && {tmuxCmd} || {fallbackCmd}";

        var execArgs = providerType switch
        {
            ProviderType.AppleContainer => new[] { "exec", "-it", "-w", "/root", externalId, "bash", "-c", shellCmd },
            ProviderType.Docker => new[] { "exec", "-it", "-w", "/root", externalId, "bash", "-c", shellCmd },
            _ => new[] { "exec", "-it", "-w", "/root", externalId, "bash", "-c", shellCmd }
        };
        var execCommand = providerType == ProviderType.AppleContainer ? "container" : "docker";

        // Use 'script' to allocate a PTY for the subprocess
        // script -q /dev/null wraps the command in a pseudo-terminal
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
                ["COLUMNS"] = "200",
                ["LINES"] = "50"
            }
        };

        // Add exec arguments individually to preserve quoting
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

        _logger.LogInformation("Terminal process started (PID {Pid}) for container {Name} via {Command}",
            process.Id, container.Name, execCommand);

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
                        break; // EOF
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
                        break; // EOF
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

                        // Handle terminal resize messages (format: \x1b[R<cols>;<rows>)
                        if (data.StartsWith("\x1b[R") && data.Contains(';'))
                        {
                            // PTY resize would require ioctl — skip for now
                            continue;
                        }

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
