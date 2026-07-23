using System.Collections.Concurrent;
using System.Threading.Channels;
using Andy.Containers.Storage;

namespace Andy.Containers.Infrastructure.Runs.Events;

/// <summary>
/// In-process run-output bus with per-run buffering and fan-out to
/// multiple subscribers. F4.1 (rivoli-ai/conductor#1934).
/// </summary>
/// <remarks>
/// A direct sibling of <c>InMemoryBuildEventBus</c> (IM9 / #263): the bus
/// keeps a ring buffer of recent lines per run (capacity from
/// <see cref="RunOutputBusOptions.BufferSize"/>) so a subscriber that
/// attaches mid-run catches up on what it missed. Subscribers each get
/// their own bounded <see cref="Channel{T}"/>; a subscriber that falls
/// behind drops lines rather than backpressuring the runner — the SSE
/// client can re-fetch the run status snapshot to reconcile. A run's
/// output is marked terminal via <see cref="Complete"/>; subscribers
/// drain the buffer then close.
/// </remarks>
public sealed class InMemoryRunOutputBus : IRunOutputBus, IDisposable
{
    private readonly RunOutputBusOptions _options;
    private readonly ConcurrentDictionary<Guid, RunChannel> _channels = new();

    public InMemoryRunOutputBus(RunOutputBusOptions? options = null)
    {
        _options = options ?? new RunOutputBusOptions();
    }

    public void Publish(Guid runId, RunOutputLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var channel = _channels.GetOrAdd(runId, _ => new RunChannel(_options.BufferSize));
        channel.Publish(line);
    }

    public void Complete(Guid runId)
    {
        var channel = _channels.GetOrAdd(runId, _ => new RunChannel(_options.BufferSize));
        channel.MarkTerminal();
    }

    public async IAsyncEnumerable<RunOutputEnvelope> SubscribeAsync(
        Guid runId,
        long? lastEventId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct,
        int? tail = null,
        DateTimeOffset? since = null,
        bool follow = true)
    {
        var channel = _channels.GetOrAdd(runId, _ => new RunChannel(_options.BufferSize));
        var subscription = channel.Subscribe(
            _options.SubscriberQueueSize,
            lastEventId,
            tail,
            since,
            follow);
        try
        {
            await foreach (var envelope in subscription.ReadAllAsync(ct))
            {
                yield return envelope;
            }
        }
        finally
        {
            channel.Unsubscribe(subscription);
        }
    }

    public void Forget(Guid runId)
    {
        if (_channels.TryRemove(runId, out var channel))
        {
            channel.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var channel in _channels.Values)
        {
            channel.Dispose();
        }
        _channels.Clear();
    }

    /// <summary>
    /// One run's queue. Holds a ring buffer of recent envelopes for
    /// replay + a list of active subscribers each with their own bounded
    /// channel. A terminal marker closes every subscriber after the
    /// buffer is drained.
    /// </summary>
    private sealed class RunChannel : IDisposable
    {
        private readonly object _lock = new();
        private readonly int _bufferSize;
        private readonly Queue<RunOutputEnvelope> _buffer;
        private readonly List<Subscription> _subscribers = [];
        private long _nextSequence = 1;
        private bool _terminalSeen;

        public RunChannel(int bufferSize)
        {
            _bufferSize = bufferSize;
            _buffer = new Queue<RunOutputEnvelope>(bufferSize);
        }

        public void Publish(RunOutputLine line)
        {
            RunOutputEnvelope envelope;
            List<Subscription> snapshot;
            lock (_lock)
            {
                // A line published after the terminal marker is dropped —
                // the stream is closed and the buffer is frozen. This
                // matches the build bus, which stops emitting after the
                // BuildCompletedEvent.
                if (_terminalSeen)
                {
                    return;
                }

                envelope = new RunOutputEnvelope(_nextSequence++, line);
                if (_buffer.Count >= _bufferSize)
                {
                    _buffer.Dequeue();
                }
                _buffer.Enqueue(envelope);
                snapshot = [.. _subscribers];
            }

            foreach (var sub in snapshot)
            {
                sub.TryWrite(envelope);
            }
        }

        public void MarkTerminal()
        {
            List<Subscription> snapshot;
            lock (_lock)
            {
                if (_terminalSeen)
                {
                    return;
                }
                _terminalSeen = true;
                snapshot = [.. _subscribers];
            }

            // Complete each subscriber so SubscribeAsync's foreach exits
            // cleanly after the in-flight lines they already received.
            // Subscribers attached after the terminal marker still replay
            // the buffer during Subscribe() then close immediately.
            foreach (var sub in snapshot)
            {
                sub.Complete();
            }
        }

        public Subscription Subscribe(
            int queueSize,
            long? lastEventId,
            int? tail,
            DateTimeOffset? since,
            bool follow)
        {
            // BoundedChannelFullMode.DropOldest matches the buffer's
            // ring-replace semantics — slow subscribers drop history
            // rather than block the publisher.
            var channel = Channel.CreateBounded<RunOutputEnvelope>(
                new BoundedChannelOptions(queueSize)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });
            var subscription = new Subscription(channel);

            RunOutputEnvelope[] replay;
            bool terminalAlreadySeen;
            lock (_lock)
            {
                if (follow)
                {
                    _subscribers.Add(subscription);
                }

                IEnumerable<RunOutputEnvelope> replayQuery = _buffer.Where(e =>
                    (lastEventId is null || e.SequenceNumber > lastEventId.Value)
                    && (since is null || e.Line.Timestamp >= since.Value));
                if (tail is { } tailCount)
                {
                    replayQuery = replayQuery.TakeLast(Math.Max(0, tailCount));
                }
                replay = [.. replayQuery];
                terminalAlreadySeen = _terminalSeen;
            }

            // Replay buffered lines to the new subscriber. If the run's
            // output already completed, replay then close.
            foreach (var envelope in replay)
            {
                subscription.TryWrite(envelope);
            }
            if (terminalAlreadySeen || !follow)
            {
                subscription.Complete();
            }
            return subscription;
        }

        public void Unsubscribe(Subscription subscription)
        {
            lock (_lock)
            {
                _subscribers.Remove(subscription);
            }
            subscription.Complete();
        }

        public void Dispose()
        {
            List<Subscription> snapshot;
            lock (_lock)
            {
                snapshot = [.. _subscribers];
                _subscribers.Clear();
                _buffer.Clear();
            }
            foreach (var sub in snapshot)
            {
                sub.Complete();
            }
        }
    }

    public sealed class Subscription
    {
        private readonly Channel<RunOutputEnvelope> _channel;
        private int _completed;

        internal Subscription(Channel<RunOutputEnvelope> channel)
        {
            _channel = channel;
        }

        public IAsyncEnumerable<RunOutputEnvelope> ReadAllAsync(CancellationToken ct)
            => _channel.Reader.ReadAllAsync(ct);

        public void TryWrite(RunOutputEnvelope envelope)
        {
            // BoundedChannel.TryWrite returns false when the channel is
            // full — DropOldest mode handles that internally, so a false
            // return means we're past completion. Ignore.
            _ = _channel.Writer.TryWrite(envelope);
        }

        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _channel.Writer.TryComplete();
            }
        }
    }
}

/// <summary>
/// Configuration for <see cref="InMemoryRunOutputBus"/>.
/// </summary>
public sealed class RunOutputBusOptions
{
    /// <summary>
    /// Per-run ring-buffer capacity. A subscriber that attaches mid-run
    /// sees up to this many recent lines. Beyond that, the older lines
    /// are dropped from the buffer (in-flight subscribers are unaffected;
    /// they're streamed live).
    /// </summary>
    public int BufferSize { get; init; } = 2000;

    /// <summary>
    /// Per-subscriber bounded channel capacity. Subscribers that fall
    /// behind by more than this drop the oldest lines; the SSE endpoint
    /// can re-fetch the run status snapshot to reconcile.
    /// </summary>
    public int SubscriberQueueSize { get; init; } = 2000;
}
