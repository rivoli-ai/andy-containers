namespace Andy.Containers.Api.Services;

/// <summary>
/// Collects terminal-resize messages and emits at most one
/// <c>tmux resize-window</c> per quiet-period after the size has
/// stabilised. Conductor #863.
///
/// The Ghostty renderer fires <c>onSurfaceResize</c> on every
/// SwiftUI frame change (typically a dozen events during a 200 ms
/// animation). Forwarding each one to <c>tmux resize-window</c> via
/// the side channel (a) hammers the tmux server with redundant work
/// and (b) raced with TUI apps running inside tmux, producing
/// visible drawing artifacts. This debouncer absorbs the mid-frames
/// and only forwards the final, stable value.
///
/// Behaviour:
/// <list type="bullet">
/// <item>First <c>Observe</c> after a quiet period restarts the timer.</item>
/// <item>Subsequent <c>Observe</c> calls within the quiet period
/// reset the timer — the forwarder fires once after the LAST
/// observed value sits unchanged for <c>quietPeriod</c>.</item>
/// <item>If the final stable size matches the last forwarded value
/// (no actual change), the forward is skipped.</item>
/// <item>Cancellation via the constructor's token cleanly stops any
/// pending fire.</item>
/// </list>
/// </summary>
public sealed class ResizeDebouncer
{
    private readonly TimeSpan _quietPeriod;
    private readonly Action<int, int> _forward;
    private readonly CancellationToken _ct;
    private readonly object _gate = new();

    private int _pendingCols;
    private int _pendingRows;
    private int _lastForwardedCols;
    private int _lastForwardedRows;
    private CancellationTokenSource? _pendingCts;

    /// <summary>
    /// Number of times <see cref="_forward"/> has fired. Visible
    /// to tests for verifying debounce semantics.
    /// </summary>
    public int ForwardCount { get; private set; }

    public ResizeDebouncer(
        string providerCommand,
        string externalId,
        string containerUser,
        string tmuxSession,
        int initialCols,
        int initialRows,
        TimeSpan quietPeriod,
        Action<int, int> forward,
        CancellationToken ct)
    {
        _quietPeriod = quietPeriod;
        _forward = forward;
        _ct = ct;
        _lastForwardedCols = initialCols;
        _lastForwardedRows = initialRows;
        _pendingCols = initialCols;
        _pendingRows = initialRows;
        // Provider/externalId/containerUser/tmuxSession are bound
        // into the `forward` closure by the caller; we don't store
        // them here.
        _ = providerCommand;
        _ = externalId;
        _ = containerUser;
        _ = tmuxSession;
    }

    /// <summary>
    /// Records an incoming resize event. The actual forward fires
    /// after <see cref="_quietPeriod"/> elapses with no further
    /// <c>Observe</c> calls.
    /// </summary>
    public void Observe(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0) return;

        CancellationTokenSource newCts;
        lock (_gate)
        {
            _pendingCols = cols;
            _pendingRows = rows;
            _pendingCts?.Cancel();
            _pendingCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            newCts = _pendingCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_quietPeriod, newCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            int colsToForward, rowsToForward;
            lock (_gate)
            {
                if (newCts != _pendingCts) return; // superseded
                if (_pendingCols == _lastForwardedCols && _pendingRows == _lastForwardedRows)
                {
                    return;
                }
                colsToForward = _pendingCols;
                rowsToForward = _pendingRows;
                _lastForwardedCols = colsToForward;
                _lastForwardedRows = rowsToForward;
                ForwardCount++;
            }

            try
            {
                _forward(colsToForward, rowsToForward);
            }
            catch
            {
                // The forward closure handles its own logging.
                // Swallow here so a single failure doesn't kill the
                // debouncer.
            }
        }, _ct);
    }

    /// <summary>
    /// Test-only: synchronously fires a single forward with the
    /// most recently observed values, regardless of quiet-period
    /// state. Useful for asserting the dedupe / no-change branches
    /// without sleeping in tests.
    /// </summary>
    internal void FlushForTesting()
    {
        int colsToForward, rowsToForward;
        lock (_gate)
        {
            if (_pendingCols == _lastForwardedCols && _pendingRows == _lastForwardedRows)
            {
                return;
            }
            colsToForward = _pendingCols;
            rowsToForward = _pendingRows;
            _lastForwardedCols = colsToForward;
            _lastForwardedRows = rowsToForward;
            ForwardCount++;
        }
        _forward(colsToForward, rowsToForward);
    }
}
