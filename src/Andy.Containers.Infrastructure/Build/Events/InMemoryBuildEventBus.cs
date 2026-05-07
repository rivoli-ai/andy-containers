using System.Collections.Concurrent;
using System.Threading.Channels;
using Andy.Containers.Abstractions.Images;
using Andy.Containers.Storage;

namespace Andy.Containers.Infrastructure.Build.Events;

/// <summary>
/// In-process build event bus with per-build buffering and
/// fan-out to multiple subscribers.
/// </summary>
/// <remarks>
/// IM9 (rivoli-ai/andy-containers#263). The bus keeps a ring buffer
/// of recent events per build (capacity from
/// <see cref="BuildEventBusOptions.BufferSize"/>) so a subscriber
/// that attaches mid-build catches up on what it missed. Subscribers
/// each get their own bounded <see cref="Channel{T}"/>; a subscriber
/// that falls behind drops events rather than backpressuring the
/// publisher — the SSE client can re-fetch the build status snapshot
/// to reconcile.
/// </remarks>
public sealed class InMemoryBuildEventBus : IBuildEventBus, IDisposable
{
    private readonly BuildEventBusOptions _options;
    private readonly ConcurrentDictionary<Guid, BuildChannel> _channels = new();

    public InMemoryBuildEventBus(BuildEventBusOptions? options = null)
    {
        _options = options ?? new BuildEventBusOptions();
    }

    public void Publish(Guid buildId, BuildProgressEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var channel = _channels.GetOrAdd(buildId, _ => new BuildChannel(_options.BufferSize));
        channel.Publish(@event);
    }

    public async IAsyncEnumerable<BuildEventEnvelope> SubscribeAsync(
        Guid buildId,
        long? lastEventId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = _channels.GetOrAdd(buildId, _ => new BuildChannel(_options.BufferSize));
        var subscription = channel.Subscribe(_options.SubscriberQueueSize, lastEventId);
        try
        {
            await foreach (var envelope in subscription.ReadAllAsync(ct))
            {
                yield return envelope;
                if (envelope.Event is BuildCompletedEvent)
                {
                    // Terminal event observed — close the stream
                    // cleanly so the SSE endpoint disconnects rather
                    // than waiting for additional traffic that won't
                    // arrive.
                    yield break;
                }
            }
        }
        finally
        {
            channel.Unsubscribe(subscription);
        }
    }

    public void Forget(Guid buildId)
    {
        if (_channels.TryRemove(buildId, out var channel))
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
    /// One build's queue. Holds a ring buffer of recent envelopes
    /// for replay + a list of active subscribers each with their
    /// own bounded channel.
    /// </summary>
    private sealed class BuildChannel : IDisposable
    {
        private readonly object _lock = new();
        private readonly int _bufferSize;
        private readonly Queue<BuildEventEnvelope> _buffer;
        private readonly List<Subscription> _subscribers = [];
        private long _nextSequence = 1;
        private bool _terminalSeen;

        public BuildChannel(int bufferSize)
        {
            _bufferSize = bufferSize;
            _buffer = new Queue<BuildEventEnvelope>(bufferSize);
        }

        public void Publish(BuildProgressEvent @event)
        {
            BuildEventEnvelope envelope;
            List<Subscription> snapshot;
            lock (_lock)
            {
                envelope = new BuildEventEnvelope(_nextSequence++, @event);
                if (_buffer.Count >= _bufferSize)
                {
                    _buffer.Dequeue();
                }
                _buffer.Enqueue(envelope);
                if (@event is BuildCompletedEvent)
                {
                    _terminalSeen = true;
                }
                snapshot = [.. _subscribers];
            }

            foreach (var sub in snapshot)
            {
                sub.TryWrite(envelope);
            }

            if (envelope.Event is BuildCompletedEvent)
            {
                // Complete each subscriber so SubscribeAsync's
                // foreach exits cleanly. Subscribers attached after
                // the terminal event will still get the buffered
                // terminal event during their initial replay below.
                foreach (var sub in snapshot)
                {
                    sub.Complete();
                }
            }
        }

        public Subscription Subscribe(int queueSize, long? lastEventId)
        {
            // BoundedChannelFullMode.DropOldest matches the buffer's
            // ring-replace semantics — slow subscribers drop history
            // rather than block the publisher.
            var channel = Channel.CreateBounded<BuildEventEnvelope>(
                new BoundedChannelOptions(queueSize)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });
            var subscription = new Subscription(channel);

            BuildEventEnvelope[] replay;
            bool terminalAlreadySeen;
            lock (_lock)
            {
                _subscribers.Add(subscription);
                replay = [.. _buffer.Where(e => lastEventId is null || e.SequenceNumber > lastEventId.Value)];
                terminalAlreadySeen = _terminalSeen;
            }

            // Replay buffered events to the new subscriber. If the
            // build has already completed, replay then close.
            foreach (var envelope in replay)
            {
                subscription.TryWrite(envelope);
            }
            if (terminalAlreadySeen)
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
        private readonly Channel<BuildEventEnvelope> _channel;
        private int _completed;

        internal Subscription(Channel<BuildEventEnvelope> channel)
        {
            _channel = channel;
        }

        public IAsyncEnumerable<BuildEventEnvelope> ReadAllAsync(CancellationToken ct)
            => _channel.Reader.ReadAllAsync(ct);

        public void TryWrite(BuildEventEnvelope envelope)
        {
            // BoundedChannel.TryWrite returns false when the channel
            // is full — DropOldest mode handles that internally, so a
            // false return means we're past completion. Ignore.
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
/// Configuration for <see cref="InMemoryBuildEventBus"/>.
/// </summary>
public sealed class BuildEventBusOptions
{
    /// <summary>
    /// Per-build ring-buffer capacity. A subscriber that attaches
    /// mid-build sees up to this many recent events. Beyond that,
    /// the older events are dropped from the buffer (in-flight
    /// subscribers are unaffected; they're streamed live).
    /// </summary>
    public int BufferSize { get; init; } = 200;

    /// <summary>
    /// Per-subscriber bounded channel capacity. Subscribers that
    /// fall behind by more than this drop the oldest events; the
    /// SSE endpoint can re-fetch the build status snapshot to
    /// reconcile.
    /// </summary>
    public int SubscriberQueueSize { get; init; } = 256;
}
