using System.Collections.Concurrent;

namespace Andy.Containers.Api.Services;

/// <summary>
/// AP7 (rivoli-ai/andy-containers#109). Default in-process implementation
/// of <see cref="IRunCancellationRegistry"/>. See the interface for the
/// scaling caveat — this is single-node only.
/// </summary>
public sealed class RunCancellationRegistry : IRunCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public IRunRegistration Register(Guid runId, CancellationToken outerToken)
    {
        var entry = new Entry(runId, outerToken, this);
        if (!_entries.TryAdd(runId, entry))
        {
            entry.Dispose();
            throw new InvalidOperationException(
                $"Run {runId} is already registered with the cancellation registry.");
        }

        return entry;
    }

    public bool TryCancel(Guid runId)
    {
        if (!_entries.TryGetValue(runId, out var entry))
        {
            return false;
        }

        entry.SignalCancel();
        return true;
    }

    public Task<bool> WaitForTerminalAsync(Guid runId, TimeSpan timeout, CancellationToken ct)
    {
        if (!_entries.TryGetValue(runId, out var entry))
        {
            // No registration → either already terminal (the runner
            // disposed before we got here) or never spawned. Either
            // way, the caller should refetch the row to learn the
            // current state — no waiting required.
            return Task.FromResult(true);
        }

        return entry.WaitForTerminalAsync(timeout, ct);
    }

    private void Remove(Guid runId, Entry entry)
    {
        // ConcurrentDictionary.TryRemove with the value compare-exchange
        // pattern guards against removing a stale entry if the runner
        // somehow re-registered in between (which Register itself
        // forbids, but be defensive).
        var kvp = new KeyValuePair<Guid, Entry>(runId, entry);
        ((ICollection<KeyValuePair<Guid, Entry>>)_entries).Remove(kvp);
    }

    private sealed class Entry : IRunRegistration
    {
        private readonly Guid _runId;
        private readonly RunCancellationRegistry _owner;
        private readonly CancellationTokenSource _cts;
        private readonly TaskCompletionSource<bool> _terminal;
        private int _disposed;

        public Entry(Guid runId, CancellationToken outerToken, RunCancellationRegistry owner)
        {
            _runId = runId;
            _owner = owner;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
            _terminal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public CancellationToken Token => _cts.Token;

        public void SignalCancel()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Disposal raced with cancel. The runner has already
                // observed terminal — nothing left to signal.
            }
        }

        public async Task<bool> WaitForTerminalAsync(TimeSpan timeout, CancellationToken ct)
        {
            if (_terminal.Task.IsCompleted)
            {
                return true;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            var completed = await Task.WhenAny(
                _terminal.Task,
                Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token)).ConfigureAwait(false);

            if (completed == _terminal.Task)
            {
                return true;
            }

            // The delay fired — distinguish caller-cancel from timeout.
            ct.ThrowIfCancellationRequested();
            return false;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _owner.Remove(_runId, this);
            _terminal.TrySetResult(true);
            _cts.Dispose();
        }
    }
}
