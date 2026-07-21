using System.Collections.Concurrent;

namespace Andy.Containers.Infrastructure.Providers.Local;

/// <summary>
/// Process-wide single-flight coordination for expensive local image builds.
/// Provider instances are intentionally short-lived, so the coordination
/// cannot live on <see cref="DockerInfrastructureProvider"/> itself: startup
/// warming and request-time provisioning resolve separate instances.
/// </summary>
internal static class LocalImageBuildCoordinator
{
    private static readonly ConcurrentDictionary<string, Flight> Flights = new(StringComparer.Ordinal);

    /// <summary>
    /// Joins or starts the build identified by <paramref name="key"/>. Caller
    /// cancellation stops only that wait while other waiters remain; when the
    /// last waiter leaves, the shared build is cancelled and a later caller
    /// may retry with a fresh flight.
    /// </summary>
    internal static async Task<LocalImageBuildResult> RunAsync(
        string key,
        Func<CancellationToken, Task> build,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(build);

        var candidate = new Flight(build);
        var flight = Flights.GetOrAdd(key, candidate);
        var startedBuild = ReferenceEquals(candidate, flight);
        if (!startedBuild)
        {
            candidate.Dispose();
        }

        var startedWaiting = System.Diagnostics.Stopwatch.GetTimestamp();
        flight.AddWaiter();
        var task = flight.Task;

        _ = task.ContinueWith(
            _ => RemoveIfCurrent(key, flight, cancel: false),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            await task.WaitAsync(ct).ConfigureAwait(false);
            return new LocalImageBuildResult(
                startedBuild,
                System.Diagnostics.Stopwatch.GetElapsedTime(startedWaiting));
        }
        finally
        {
            if (flight.RemoveWaiter() == 0 && !task.IsCompleted)
            {
                RemoveIfCurrent(key, flight, cancel: true);
            }
            else if (task.IsCompleted)
            {
                RemoveIfCurrent(key, flight, cancel: false);
            }
        }
    }

    private static void RemoveIfCurrent(string key, Flight flight, bool cancel)
    {
        if (!Flights.TryGetValue(key, out var current) || !ReferenceEquals(current, flight))
        {
            return;
        }

        if (!Flights.TryRemove(key, out var removed) || !ReferenceEquals(removed, flight))
        {
            return;
        }

        if (cancel)
        {
            flight.Cancel();
        }

        if (flight.Task.IsCompleted)
        {
            flight.Dispose();
        }
        else
        {
            _ = flight.Task.ContinueWith(
                _ => flight.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private sealed class Flight : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Lazy<Task> _task;
        private int _waiters;
        private int _disposed;

        internal Flight(Func<CancellationToken, Task> build)
        {
            _task = new Lazy<Task>(
                () =>
                {
                    try
                    {
                        return build(_cancellation.Token)
                            ?? Task.FromException(new InvalidOperationException("Image build delegate returned null."));
                    }
                    catch (Exception ex)
                    {
                        // Lazy<T>.Value rethrows synchronous factory failures
                        // before RunAsync reaches its finally block. Convert
                        // them to a faulted Task so waiter accounting, removal,
                        // and retry semantics remain identical to async faults.
                        return Task.FromException(ex);
                    }
                },
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        internal Task Task => _task.Value;

        internal void AddWaiter() => Interlocked.Increment(ref _waiters);

        internal int RemoveWaiter() => Interlocked.Decrement(ref _waiters);

        internal void Cancel()
        {
            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _cancellation.Dispose();
            }
        }
    }

    internal readonly record struct LocalImageBuildResult(bool StartedBuild, TimeSpan WaitDuration);
}
