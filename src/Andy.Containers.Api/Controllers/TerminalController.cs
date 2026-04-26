using System.Diagnostics;
using System.Net.WebSockets;
using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using Andy.Rbac.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Controllers;

[ApiController]
[Route("api/containers/{id}/terminal")]
[Authorize]
public class TerminalController : ControllerBase
{
    private readonly ContainersDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TerminalController> _logger;

    public TerminalController(
        ContainersDbContext db,
        ICurrentUserService currentUser,
        IConfiguration configuration,
        ILogger<TerminalController> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet]
    [RequirePermission("container:execute")]
    public async Task Connect(Guid id)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsync("WebSocket connection required");
            return;
        }

        // CSWSH defense: browsers always send Origin on WebSocket upgrade requests.
        // Reject anything not on the configured Cors:Origins allowlist.
        var origin = HttpContext.Request.Headers.Origin.ToString();
        if (!IsOriginAllowed(origin))
        {
            _logger.LogWarning("Terminal WebSocket rejected — Origin '{Origin}' not in Cors:Origins allowlist", origin);
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsync("Origin not allowed");
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

        if (!CanAccess(container))
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsync("Forbidden");
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
        var containerUser = container.ContainerUser ?? "root";
        var homeDir = containerUser == "root" ? "/root" : $"/home/{containerUser}";

        // Wait for client to send terminal dimensions before creating PTY
        var (cols, rows) = await WaitForTerminalSize(ws, ct);
        _logger.LogInformation("Terminal size: {Cols}x{Rows} for container {Name}", cols, rows, container.Name);

        // Use tmux for session persistence with full screen redraw on reattach.
        // - Create with explicit -x/-y to match client terminal exactly
        // - On reconnect, resize-window BEFORE attach to fix stale dimensions
        // - attach -d detaches dead/stale clients so tmux uses current PTY size
        // - default-terminal xterm-256color prevents arrow key / escape issues
        var tmuxSession = "web";

        // Check if tmux session already exists (to decide whether to show banner).
        // Must run as the same user that owns the session — tmux uses a per-UID
        // socket (/tmp/tmux-<uid>/default), so a check as root cannot see a session
        // owned by `containerUser`. Without -u, hasExistingSession is always false
        // and the banner is injected on every reconnect.
        var checkExisting = providerType == ProviderType.AppleContainer ? "container" : "docker";
        var hasExistingSession = false;
        try
        {
            var checkProcess = System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = checkExisting == "docker" ? "docker" : "container",
                Arguments = $"exec -u {containerUser} {externalId} tmux has-session -t {tmuxSession}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (checkProcess is not null)
            {
                await checkProcess.WaitForExitAsync(ct);
                hasExistingSession = checkProcess.ExitCode == 0;
            }
        }
        catch { /* assume new session */ }

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
                         $"exec {execCommand} exec -it -u {containerUser} -w {homeDir} {externalId} bash -c '{innerShellQuoted}'";

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

        // Post-attach injection: on a NEW session, run the welcome
        // banner; on an EXISTING session (reattach), force a full
        // server-side redraw so the user sees a complete frame
        // immediately instead of a stale buffer until tmux's next
        // status-interval (~15 s).
        //
        // Why force a redraw at all: the bytes tmux already drew on
        // the server-side window are not replayed when a new client
        // attaches. The renderer paints a blinking cursor and nothing
        // else until tmux happens to repaint a region (status-interval
        // tick, vim cursor move, etc.). `tmux refresh-client -S`
        // serialises a fresh full-screen redraw through the tmux
        // server, which is what we actually need.
        //
        // Conductor #838.
        var stdinStream = process.StandardInput.BaseStream;
        _ = Task.Run(async () =>
        {
            try
            {
                // Wait for tmux to fully initialize and draw
                await Task.Delay(1500, ct);
                if (process.HasExited || ws.State != WebSocketState.Open)
                {
                    return;
                }

                if (hasExistingSession)
                {
                    // Reattach: force tmux to repaint via the side
                    // channel — `<provider> exec -u <user> tmux
                    // refresh-client -S` against the same per-UID
                    // tmux socket the session uses. Going through
                    // stdin here would type `tmux refresh-client -S`
                    // into the user's foreground process (vim,
                    // claude, even just bash), which is wrong: the
                    // command would land in the user's shell history
                    // and run as if they typed it. The side channel
                    // talks directly to the tmux server and is
                    // invisible to the user. Conductor #838.
                    InvokeTmuxCommand(
                        providerCommand: execCommand,
                        externalId: externalId,
                        containerUser: containerUser,
                        tmuxArguments: $"refresh-client -S -t {tmuxSession}",
                        ct: ct);
                }
                else
                {
                    // New session: the welcome banner runs INSIDE
                    // the user's bash session because that's exactly
                    // where it should print. Stdin injection is
                    // correct here.
                    var bannerCmd = BuildWelcomeBannerCommand();
                    await stdinStream.WriteAsync(bannerCmd, ct);
                    await stdinStream.FlushAsync(ct);
                }
            }
            catch { /* ignore */ }
        }, ct);

        // Relay: WebSocket -> Process stdin
        // Write raw bytes to BaseStream instead of using StreamWriter to avoid
        // any text encoding transformations that could corrupt escape sequences
        // (e.g. arrow keys: \x1b[A, \x1b[B, etc.)
        // Also intercepts resize messages from the client to update the PTY size
        // via ioctl, which sends SIGWINCH to tmux so it redraws at the new size.
        // Track the last (cols, rows) we forwarded to tmux so we can
        // dedupe identical resize messages (the renderer fires one on
        // every micro-layout change, not just on user resize). Without
        // dedupe, every redraw-from-tmux triggered by a previous
        // forwarder cascades into more forwards — terminal becomes
        // unusable. Conductor #836 follow-up.
        var lastForwardedCols = cols;
        var lastForwardedRows = rows;

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
                            // Dedupe: only spawn `tmux resize-window`
                            // when the size actually changes from the
                            // last value we sent. Otherwise we feed
                            // back into the renderer which fires more
                            // resize events, etc. Conductor #836.
                            if (newCols == lastForwardedCols && newRows == lastForwardedRows)
                            {
                                continue;
                            }
                            lastForwardedCols = newCols;
                            lastForwardedRows = newRows;

                            ForwardResizeToTmux(
                                providerCommand: execCommand,
                                externalId: externalId,
                                containerUser: containerUser,
                                tmuxSession: tmuxSession,
                                cols: newCols,
                                rows: newRows,
                                ct: ct);
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

    internal bool CanAccess(Container container)
    {
        if (_currentUser.IsAdmin()) return true;
        return container.OwnerId == _currentUser.GetUserId();
    }

    /// <summary>
    /// Tells the tmux server to resize the named window to the new
    /// columns/rows. Runs as <paramref name="containerUser"/> so the
    /// command lands on the same per-UID tmux socket that owns the
    /// session.
    /// </summary>
    private void ForwardResizeToTmux(
        string providerCommand,
        string externalId,
        string containerUser,
        string tmuxSession,
        int cols,
        int rows,
        CancellationToken ct)
    {
        if (!IsValidTerminalSize(cols, rows))
        {
            _logger.LogWarning(
                "Refusing tmux resize-window — out of range: {Cols}x{Rows}",
                cols, rows);
            return;
        }

        _logger.LogDebug(
            "[CONTAINERS-TMUX] resize-window {Cols}x{Rows} on {Provider} {ExternalId} as {User}",
            cols, rows, providerCommand, externalId, containerUser);

        InvokeTmuxCommand(
            providerCommand: providerCommand,
            externalId: externalId,
            containerUser: containerUser,
            tmuxArguments: $"resize-window -t {tmuxSession} -x {cols} -y {rows}",
            ct: ct);
    }

    /// <summary>
    /// Spawns a <c>&lt;providerCommand&gt; exec -u &lt;user&gt; &lt;externalId&gt; tmux &lt;args&gt;</c>
    /// against the container's per-UID tmux socket. Used for any
    /// out-of-band tmux server command (resize-window, refresh-client,
    /// kill-session, …) where stdin injection into the user's shell
    /// would be wrong (they'd see the command typed in their session).
    ///
    /// Fire-and-forget: a slow <c>docker exec</c> spawn never stalls
    /// the main WebSocket relay loop. Failures are logged at Debug
    /// level so a transient error isn't surfaced as a banner.
    /// </summary>
    /// <remarks>
    /// Conductor #838 — replaces the previous stdin-injection path
    /// for the post-attach redraw, which typed
    /// <c>tmux refresh-client -S</c> into the user's foreground
    /// process.
    /// </remarks>
    private void InvokeTmuxCommand(
        string providerCommand,
        string externalId,
        string containerUser,
        string tmuxArguments,
        CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var args = $"exec -u {containerUser} {externalId} tmux {tmuxArguments}";
                using var p = System.Diagnostics.Process.Start(new ProcessStartInfo
                {
                    FileName = providerCommand,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (p is null) return;
                await p.WaitForExitAsync(ct);
                if (p.ExitCode != 0)
                {
                    var stderr = await p.StandardError.ReadToEndAsync(ct);
                    _logger.LogDebug(
                        "tmux {Args} exited {Code}: {Stderr}",
                        tmuxArguments, p.ExitCode, stderr.Trim());
                }
            }
            catch (OperationCanceledException) { /* WS closed */ }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Failed to invoke tmux command: {Args}",
                    tmuxArguments);
            }
        }, ct);
    }

    internal bool IsOriginAllowed(string origin)
    {
        if (string.IsNullOrEmpty(origin))
        {
            // Native WebSocket clients (macOS NSURLSession's
            // URLSessionWebSocketTask, in particular) do not send an
            // Origin header for ws:// schemes — Origin is a
            // browser-only CSWSH defence, and natively-launched
            // requests aren't subject to that attack vector.
            //
            // When the upgrade comes from loopback we treat empty
            // Origin as a same-origin local request and allow it.
            // Cross-host requests that arrive without Origin are still
            // rejected, so the CSWSH guarantee for browser traffic
            // (which always sends Origin) is preserved.
            //
            // Conductor regression: terminal WebSocket from the
            // bundled Conductor app over the local proxy never
            // populates Origin, so every WS upgrade was failing 403.
            return IsRemoteLoopback();
        }
        var allowed = _configuration.GetSection("Cors:Origins").Get<string[]>();
        if (allowed is null || allowed.Length == 0)
            return false;
        return Array.Exists(allowed, a => string.Equals(a, origin, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when the upgrade request arrived from a loopback address
    /// (127.0.0.0/8, ::1). HttpContext is null in unit-test contexts
    /// that don't drive the controller through ASP.NET; treat that as
    /// non-loopback so tests cover the strict path.
    /// </summary>
    internal virtual bool IsRemoteLoopback()
    {
        var remote = HttpContext?.Connection?.RemoteIpAddress;
        return remote is not null && System.Net.IPAddress.IsLoopback(remote);
    }

    /// <summary>
    /// Builds the bytes injected into the inner shell's stdin on first
    /// attach to display the welcome banner. Only invoked on new
    /// sessions — reattach uses the tmux side channel
    /// (<see cref="InvokeTmuxCommand"/>) so commands don't get typed
    /// into the user's foreground process.
    /// </summary>
    /// <remarks>
    /// Internal for unit tests. Space prefix keeps the command out
    /// of bash history; trailing <c>; true</c> swallows the exit code
    /// so a missing andy-banner binary doesn't surface as a `1` exit
    /// on the prompt.
    /// </remarks>
    internal static byte[] BuildWelcomeBannerCommand()
    {
        return System.Text.Encoding.UTF8.GetBytes(
            " clear && /usr/local/bin/andy-banner 2>/dev/null; true\n");
    }

    /// <summary>
    /// Validates a (columns, rows) pair against the bounds tmux accepts.
    ///
    /// xterm hardware limits are 1..999 for both axes; tmux silently
    /// clamps to a stricter floor of 2, so a 1-column resize crashes
    /// with "create window failed: size too small". Refuse pathological
    /// input rather than passing it along — a malformed resize message
    /// from a stale client should not break the live session.
    /// </summary>
    /// <remarks>
    /// Public for unit tests. The full forwarding helper itself spawns
    /// a child process and is harder to isolate; pinning the bounds
    /// rule as a pure function lets tests cover the most error-prone
    /// branch directly.
    /// </remarks>
    internal static bool IsValidTerminalSize(int cols, int rows)
    {
        return cols >= 2 && cols <= 1000 && rows >= 2 && rows <= 1000;
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
