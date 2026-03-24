using System.Net.WebSockets;
using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Renci.SshNet;

namespace Andy.Containers.Api.Controllers;

[ApiController]
[Route("api/containers/{id}/terminal")]
public class TerminalController : ControllerBase
{
    private readonly ContainersDbContext _db;
    private readonly ILogger<TerminalController> _logger;

    // Default SSH credentials set by the post-create script
    private const string SshUser = "root";
    private const string SshPassword = "container";
    private const int SshPort = 22;

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

        var hostIp = container.HostIp;
        if (string.IsNullOrEmpty(hostIp))
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsync("Container has no host IP assigned");
            return;
        }

        var ws = await HttpContext.WebSockets.AcceptWebSocketAsync();
        _logger.LogInformation("Terminal WebSocket connected for container {Name} ({Id}) at {HostIp}",
            container.Name, container.Id, hostIp);

        try
        {
            await RunSshSession(ws, hostIp, container.Name, HttpContext.RequestAborted);
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

    private async Task RunSshSession(WebSocket ws, string hostIp, string containerName, CancellationToken ct)
    {
        using var sshClient = new SshClient(hostIp, SshPort, SshUser, SshPassword);
        sshClient.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10);

        try
        {
            sshClient.Connect();
        }
        catch (Exception ex)
        {
            var msg = $"\r\n\x1b[31mSSH connection failed to {hostIp}: {ex.Message}\x1b[0m\r\n";
            await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Binary, true, ct);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "SSH failed", ct);
            return;
        }

        _logger.LogInformation("SSH connected to {HostIp} for container {Name}", hostIp, containerName);

        using var shell = sshClient.CreateShellStream("xterm-256color", 200, 50, 0, 0, 8192);

        // Relay: SSH -> WebSocket
        var sshToWs = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            while (!ct.IsCancellationRequested && sshClient.IsConnected && ws.State == WebSocketState.Open)
            {
                try
                {
                    var bytesRead = await shell.ReadAsync(buffer, ct);
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
                        await Task.Delay(10, ct);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "SSH read error");
                    break;
                }
            }
        }, ct);

        // Relay: WebSocket -> SSH
        var wsToSsh = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
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
                            // Resize is acknowledged but SSH.NET ShellStream doesn't expose
                            // window-change requests directly. The terminal was created with
                            // a generous default size (200x50). Skip for now.
                            continue;
                        }

                        shell.Write(data);
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

        await Task.WhenAny(sshToWs, wsToSsh);

        if (ws.State == WebSocketState.Open)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None); }
            catch { /* ignore close errors */ }
        }

        _logger.LogInformation("Terminal session ended for container {Name}", containerName);
    }
}
