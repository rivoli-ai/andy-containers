using System.Diagnostics;
using System.Net.WebSockets;
using Andy.Containers.Abstractions;
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
    private readonly IInfrastructureProviderFactory _providerFactory;

    public TerminalController(
        ContainersDbContext db,
        ICurrentUserService currentUser,
        IConfiguration configuration,
        ILogger<TerminalController> logger,
        IInfrastructureProviderFactory providerFactory)
    {
        _db = db;
        _currentUser = currentUser;
        _configuration = configuration;
        _logger = logger;
        _providerFactory = providerFactory;
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

        // Try the daemon-side PTY path first (#875 PR 1). Provider
        // returns null when it doesn't support daemon-managed PTY
        // (Apple Containers today; cloud providers permanently). On
        // null we fall through to the legacy script-based path
        // below.
        //
        // Verified end-to-end via a hand-rolled HTTP/1.1 hijack
        // (Docker.DotNet's MultiplexedStream-based attach silently
        // drops writes — see DockerInfrastructureProvider for the
        // detail). On any exception we still fall through to the
        // legacy script-based path so a regression here doesn't
        // leave the user with no terminal.
        if (container.Provider is not null)
        {
            try
            {
                var infra = _providerFactory.GetProvider(container.Provider);
                var ptyShellCmd = BuildContainerShellCommand(rows: rows, cols: cols);
                var ptySession = await infra.OpenInteractiveExecAsync(
                    externalId: externalId,
                    command: ["bash", "-c", ptyShellCmd],
                    user: containerUser,
                    workingDirectory: homeDir,
                    cols: cols,
                    rows: rows,
                    ct: ct);
                if (ptySession is not null)
                {
                    var execCommandForProbe = providerType == ProviderType.AppleContainer ? "container" : "docker";
                    var ptyHasExistingSession = await ProbeTmuxSessionExistsAsync(
                        providerCommand: execCommandForProbe,
                        externalId: externalId,
                        containerUser: containerUser,
                        ct: ct);
                    await using (ptySession)
                    {
                        await RunInteractiveExecSession(
                            ws: ws,
                            session: ptySession,
                            hasExistingSession: ptyHasExistingSession,
                            container: container,
                            ct: ct);
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[PTY-EXEC] OpenInteractiveExecAsync threw — falling back to script-based path");
            }
        }

        // Legacy fallback path (script + docker exec / container exec).
        // Used when the provider doesn't support daemon-managed PTY,
        // or when OpenInteractiveExecAsync threw above.

        // ⚠️ TEMPORARILY BYPASSING TMUX (#842 preview).
        //
        // tmux + script + docker-exec PTY chain has been the source
        // of every recent rendering regression (#836, #838, #863):
        // resize-window doesn't propagate the size to the script
        // PTY's inner client, claude code / vim drawn at the wrong
        // width, stale cells past the apparent right edge,
        // refresh-client races with TUI app writes, etc.
        //
        // Skip tmux entirely and run plain interactive bash. Costs:
        //   - No session persistence: closing the terminal ends
        //     the bash session (ssh-style, not screen-style).
        //   - No status bar: free real estate at the bottom of the
        //     terminal.
        // Wins:
        //   - claude code / vim / less render at the actual terminal
        //     width (the script PTY's stty was set above to match
        //     the client).
        //   - No tmux refresh-client races, no resize-window
        //     side-channel, no per-UID-socket gotchas.
        //
        // Reverting: restore the previous tmux block from git history.
        // Conductor #842 will land a proper multiplexer-mode picker
        // so users can choose tmux / screen / none / custom.
        var tmuxSession = "web"; // unused; kept for diff size only
        _ = tmuxSession;

        // Detect whether we're reattaching by probing for the dtach
        // socket. When dtach is installed, the first attach creates
        // /tmp/conductor.sock (see BuildContainerShellCommand);
        // subsequent attaches join that same socket. The welcome
        // banner should only fire on the FIRST attach — repeating it
        // on every reconnect overwrites the user's prompt with the
        // banner header (the bug the user reported when verifying
        // dtach persistence).
        //
        // For containers without dtach, the socket never exists and
        // every attach gets the banner — same as before this change,
        // since each attach is a fresh bash anyway.
        var execCommand = providerType == ProviderType.AppleContainer ? "container" : "docker";
        var hasExistingSession = await ProbeTmuxSessionExistsAsync(
            providerCommand: execCommand,
            externalId: externalId,
            containerUser: containerUser,
            ct: ct);

        var shellCmd = BuildContainerShellCommand(rows: rows, cols: cols);

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

                // Existing sessions: do nothing. The previous
                // attempt to force a redraw via `tmux refresh-client
                // -S` (#838) caused visible artifacts in TUI apps
                // (claude code, vim) running inside the session —
                // the redraw raced with the app's own writes and
                // produced duplicated / partial frames. Until we
                // have a clean mechanism (#842 multiplexer-aware
                // mode, or replacing the script chain), reattach
                // simply lets the user's first keystroke prompt
                // tmux to repaint. The original "stale frame on
                // reattach" annoyance returns, but it's strictly
                // less broken than the redraw-cascade.
                //
                // New sessions still get the welcome banner — that
                // injection runs inside the user's fresh bash
                // session and doesn't race with anything.
                if (ShouldInjectWelcomeBanner(hasExistingSession))
                {
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
        // Drop client resize messages on the floor.
        //
        // We've now tried three approaches to forward resizes to
        // tmux via `tmux resize-window`: forward every event (#145),
        // dedupe by exact size (#151), debounce after a quiet period
        // (#153). All three either flood tmux with redundant work
        // or ship a resize that tmux's CLIENT-SIZE constraint
        // silently rejects — the script PTY's stty was set at
        // attach time and tmux clamps the window to the smallest
        // attached client. The net result is the same: TUI apps
        // (claude, vim) keep drawing at the OLD width, the
        // renderer's grid is wider, cells past the OLD edge are
        // never overwritten, and the user sees stale content as
        // visible artifacts.
        //
        // The proper fix needs either (a) ownership of the script
        // PTY's master FD so `ioctl(TIOCSWINSZ)` actually changes
        // the inner client size, or (b) the no-multiplexer mode
        // from #842 so heavy TUIs don't go through tmux at all.
        // Until one of those lands, we drop resize messages and
        // accept the pre-existing "no reflow on window resize"
        // limitation. It's strictly less broken than the
        // mismatch-and-stale-cells current state.
        //
        // Conductor #836 / #863 reopened.
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
                        if (result.Count > 10 && buffer[0] == '{' && IsResizeMessage(buffer, result.Count, out _, out _))
                        {
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

    /// <summary>
    /// PTY-backed terminal session loop (#875 PR 1). Identical
    /// pump shape to the legacy script-based path but uses
    /// <see cref="IInteractiveExecSession"/> for I/O so resize
    /// propagates through the daemon's PTY API instead of a
    /// side-channel <c>tmux resize-window</c> hack.
    ///
    /// Three concurrent tasks: session→WS read, WS→session write
    /// (with resize-message detection), and a one-shot post-attach
    /// banner injection identical to the legacy path.
    /// </summary>
    private async Task RunInteractiveExecSession(
        WebSocket ws,
        IInteractiveExecSession session,
        bool hasExistingSession,
        Container container,
        CancellationToken ct)
    {
        // Relay: session → WS
        var sessionToWs = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                try
                {
                    var bytesRead = await session.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (bytesRead <= 0)
                    {
                        break;
                    }
                    await ws.SendAsync(
                        new ArraySegment<byte>(buffer, 0, bytesRead),
                        WebSocketMessageType.Binary,
                        true,
                        ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[PTY-EXEC] read error");
                    break;
                }
            }
        }, ct);

        // Post-attach welcome-banner injection (#838 / #157). Same
        // logic as the legacy path: only fire on first attach,
        // detected via dtach-socket probe upstream.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500, ct);
                if (ws.State != WebSocketState.Open) return;
                if (ShouldInjectWelcomeBanner(hasExistingSession))
                {
                    var bannerCmd = BuildWelcomeBannerCommand();
                    await session.WriteAsync(bannerCmd, ct);
                }
            }
            catch { /* ignore */ }
        }, ct);

        // Relay: WS → session, with resize-message routing
        var wsToSession = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                try
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.Count <= 0) continue;

                    // Resize messages route to the daemon's PTY-size
                    // API (provider-specific). The kernel propagates
                    // SIGWINCH down to the inner shell.
                    if (result.Count > 10
                        && buffer[0] == '{'
                        && IsResizeMessage(buffer, result.Count, out var newCols, out var newRows))
                    {
                        if (IsValidTerminalSize(newCols, newRows))
                        {
                            await session.ResizeAsync(newCols, newRows, ct);
                        }
                        continue;
                    }

                    await session.WriteAsync(buffer.AsMemory(0, result.Count), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (WebSocketException) { break; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[PTY-EXEC] WS read error");
                    break;
                }
            }
        }, ct);

        await Task.WhenAny(sessionToWs, wsToSession);

        if (ws.State == WebSocketState.Open)
        {
            try
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
            }
            catch { /* ignore */ }
        }
        _logger.LogInformation("[PTY-EXEC] terminal session ended for container {Name}", container.Name);
    }

    internal bool CanAccess(Container container)
    {
        if (_currentUser.IsAdmin()) return true;
        return container.OwnerId == _currentUser.GetUserId();
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
    /// sessions — reattach must NOT inject because the bytes would be
    /// typed into whatever the user has running in the foreground
    /// (vim, claude code, etc.).
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

    /// <summary>
    /// Checks whether the tmux session already exists in the
    /// container — the signal that we're reattaching to an existing
    /// session and shouldn't re-emit the welcome banner.
    /// </summary>
    /// <remarks>
    /// Best-effort: a failure to probe is interpreted as
    /// "no session", which means the worst case is a redundant
    /// banner injection on a borderline-broken container. The
    /// session name matches <see cref="TmuxSessionName"/>.
    /// </remarks>
    private async Task<bool> ProbeTmuxSessionExistsAsync(
        string providerCommand,
        string externalId,
        string containerUser,
        CancellationToken ct)
    {
        var args = BuildTmuxHasSessionArguments(containerUser, externalId);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = providerCommand,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                _logger.LogInformation(
                    "[TMUX-PROBE] Process.Start returned null — provider={Provider} args={Args}",
                    providerCommand, args);
                return false;
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            string stderr;
            try
            {
                await process.WaitForExitAsync(cts.Token);
                stderr = (await process.StandardError.ReadToEndAsync(ct)).Trim();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "[TMUX-PROBE] timed out after 3s — provider={Provider} args={Args} elapsed={ElapsedMs}ms",
                    providerCommand, args, sw.ElapsedMilliseconds);
                try { process.Kill(true); } catch { /* ignore */ }
                return false;
            }

            var hasSession = process.ExitCode == 0;
            _logger.LogInformation(
                "[TMUX-PROBE] provider={Provider} args={Args} exit={ExitCode} elapsed={ElapsedMs}ms hasSession={HasSession} stderr={Stderr}",
                providerCommand, args, process.ExitCode, sw.ElapsedMilliseconds, hasSession,
                stderr.Length > 200 ? stderr[..200] : stderr);
            return hasSession;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(
                "[TMUX-PROBE] threw — provider={Provider} args={Args} elapsed={ElapsedMs}ms exception={Exception}",
                providerCommand, args, sw.ElapsedMilliseconds, ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Builds the shell command piped into <c>docker exec -it … bash
    /// -c '…'</c> to start a terminal session inside the container.
    ///
    /// The session is wrapped in an invisible tmux: tmux holds the
    /// scroll buffer + screen state across WebSocket reconnects, but
    /// is configured to render exactly like a bare bash — no status
    /// bar, no prefix key, no keybindings, native ANSI passthrough.
    /// The user perceives a plain bash; reconnecting from another
    /// machine attaches the same session and replays the last
    /// 10 000 lines automatically. Conductor #875 PR 2.
    ///
    /// dtach (used by the previous incarnation of this command) also
    /// preserved the bash session but had no buffer to replay, so a
    /// reconnecting client saw a blank screen until the user typed.
    /// tmux's <c>aggressive-resize</c> + the daemon-PTY's SIGWINCH
    /// chain make the rendering bugs that drove the earlier dtach
    /// switch (#154) moot — those came from <c>tmux resize-window</c>
    /// being injected as a side-channel against a <c>script</c>-owned
    /// PTY. With the daemon owning the PTY, SIGWINCH propagates
    /// cleanly and tmux resizes correctly.
    ///
    /// The conductor-tmux.conf is lazy-created in the user's home on
    /// first attach so we don't have to rebuild every container image
    /// (and so the file lands somewhere the container user can write).
    /// </summary>
    /// <remarks>
    /// Internal for unit tests. Pure function — no side effects, no
    /// DI dependencies. Tests pin the tmux-attach shape so future
    /// changes don't silently lose session persistence.
    /// </remarks>
    internal static string BuildContainerShellCommand(int rows, int cols)
    {
        // Outer setup (stty, env, rcfiles) matches the legacy command
        // so existing infrastructure that depends on this shape keeps
        // working. The final exec wraps `bash -i` in invisible tmux:
        // tmux holds the screen state across WS reconnects and renders
        // identically to bare bash thanks to a minimal
        // conductor-tmux.conf (no status bar, no prefix, no bindings).
        //
        // Conf resolution: prefer the image-baked /etc/conductor-tmux.conf
        // (every desktop Dockerfile copies the canonical
        // scripts/container/conductor-tmux.conf there), fall back to
        // a lazy-create at $HOME/.config/conductor/tmux.conf so older
        // images that pre-date the bake still get invisible tmux
        // without us having to rebuild every container.
        //
        // Fallback when tmux isn't installed: exec a bare `bash -i`.
        // Without this guard, `exec tmux …` against a missing binary
        // would silently fail and the user would see a broken
        // terminal — so the dtach-fallback semantic survives the
        // multiplexer swap. Persistence/scrollback are lost on
        // tmux-less containers, but the terminal still works.
        return $"stty rows {rows} cols {cols} 2>/dev/null; " +
               $"export TERM=xterm-256color LANG=C.UTF-8 LC_ALL=C.UTF-8; " +
               $"[ -f /etc/profile ] && . /etc/profile 2>/dev/null; " +
               $"[ -f /etc/bash.bashrc ] && . /etc/bash.bashrc 2>/dev/null; " +
               $"[ -f ~/.profile ] && . ~/.profile 2>/dev/null; " +
               $"[ -f ~/.bashrc ] && . ~/.bashrc 2>/dev/null; " +
               $"if [ -f /etc/conductor-tmux.conf ]; then " +
                    $"TMUX_CONF=/etc/conductor-tmux.conf; " +
               $"else " +
                    $"TMUX_CONF=\"$HOME/.config/conductor/tmux.conf\"; " +
                    $"mkdir -p \"$(dirname \"$TMUX_CONF\")\" 2>/dev/null; " +
                    $"if [ ! -f \"$TMUX_CONF\" ]; then cat > \"$TMUX_CONF\" <<'CONDUCTOR_TMUX_EOF'\n" +
                    "set -g status off\n" +
                    "set -g prefix none\n" +
                    "unbind-key -a\n" +
                    "setw -g aggressive-resize on\n" +
                    "set -g default-terminal \"xterm-256color\"\n" +
                    "set -g default-shell \"/bin/bash\"\n" +
                    "set -g history-limit 10000\n" +
                    "CONDUCTOR_TMUX_EOF\n" +
                    $"fi; " +
               $"fi; " +
               $"if command -v tmux >/dev/null 2>&1; then " +
                    $"exec tmux -f \"$TMUX_CONF\" new-session -A -s {TmuxSessionName} bash -i; " +
               $"else " +
                    $"exec bash -i; " +
               $"fi";
    }

    /// <summary>
    /// Fixed tmux session name. The session is created on first
    /// attach via <c>tmux new-session -A -s web</c> and reused for
    /// every subsequent attach to the same container — the user
    /// perceives "close terminal, reopen, you're back."
    ///
    /// Internal so unit tests can pin both the shell command and
    /// the probe argv against the same constant — if anyone changes
    /// one without the other, banner detection breaks silently.
    /// </summary>
    internal const string TmuxSessionName = "web";

    /// <summary>
    /// Pure decision: does the post-attach injection block need to
    /// inject the welcome banner? Banner fires only on FIRST attach
    /// (no existing session). Reattaches must NOT inject — repeating
    /// the banner overwrites whatever the user was looking at.
    /// </summary>
    internal static bool ShouldInjectWelcomeBanner(bool hasExistingSession) => !hasExistingSession;

    /// <summary>
    /// Builds the argv for probing whether the tmux session already
    /// exists via
    /// <c>&lt;providerCommand&gt; exec -u &lt;user&gt; &lt;externalId&gt; tmux has-session -t web</c>.
    /// <c>tmux has-session</c> exits 0 if the session is alive on
    /// the user's per-UID tmux socket, non-zero otherwise — the
    /// precise signal we need to decide "first attach" vs. "reattach"
    /// without poking at filesystem socket paths.
    /// </summary>
    internal static string BuildTmuxHasSessionArguments(string containerUser, string externalId)
        => $"exec -u {containerUser} {externalId} tmux has-session -t {TmuxSessionName}";

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
