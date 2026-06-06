// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Andy.Containers.Storage;

namespace Andy.Containers.Infrastructure.Runs.Events;

/// <summary>
/// SM.2.6 (rivoli-ai/conductor#2008). In-process broadcast implementation
/// of <see cref="IContainerLifecycleBus"/>. Maintains a rolling ring buffer
/// of recent lifecycle events so SSE subscribers that attach mid-stream
/// catch up before tailing live.
/// </summary>
/// <remarks>
/// Pattern is identical to <c>InMemoryRunOutputBus</c> (F4.1 / #1934)
/// and <c>InMemoryBuildEventBus</c> (IM9 / #263). Key differences:
/// events are fleet-wide (not per-run) and the stream is never
/// "terminal" — a subscriber stays open until the HTTP request is
/// cancelled; the bus itself is closed only when the process shuts down.
/// </remarks>
public sealed class InMemoryContainerLifecycleBus : IContainerLifecycleBus, IDisposable
{
    private readonly ContainerLifecycleBusOptions _options;
    private readonly object _lock = new();
    private readonly Queue<ContainerLifecycleEnvelope> _buffer;
    private readonly List<Subscription> _subscribers = [];
    private long _nextSequence = 1;

    public InMemoryContainerLifecycleBus(ContainerLifecycleBusOptions? options = null)
    {
        _options = options ?? new ContainerLifecycleBusOptions();
        _buffer = new Queue<ContainerLifecycleEnvelope>(_options.BufferSize);
    }

    /// <inheritdoc/>
    public void Publish(ContainerLifecycleEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        ContainerLifecycleEnvelope envelope;
        List<Subscription> snapshot;
        lock (_lock)
        {
            envelope = new ContainerLifecycleEnvelope(_nextSequence++, @event);
            if (_buffer.Count >= _options.BufferSize)
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

    /// <inheritdoc/>
    public async IAsyncEnumerable<ContainerLifecycleEnvelope> SubscribeAsync(
        long? lastEventId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var subscription = CreateSubscription(lastEventId);
        try
        {
            await foreach (var envelope in subscription.ReadAllAsync(ct))
            {
                yield return envelope;
            }
        }
        finally
        {
            RemoveSubscription(subscription);
        }
    }

    private Subscription CreateSubscription(long? lastEventId)
    {
        var channel = Channel.CreateBounded<ContainerLifecycleEnvelope>(
            new BoundedChannelOptions(_options.SubscriberQueueSize)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        var subscription = new Subscription(channel);

        ContainerLifecycleEnvelope[] replay;
        lock (_lock)
        {
            _subscribers.Add(subscription);
            replay = [.. _buffer.Where(e =>
                lastEventId is null || e.SequenceNumber > lastEventId.Value)];
        }

        foreach (var envelope in replay)
        {
            subscription.TryWrite(envelope);
        }

        return subscription;
    }

    private void RemoveSubscription(Subscription subscription)
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

    /// <summary>One active SSE subscriber channel.</summary>
    private sealed class Subscription
    {
        private readonly Channel<ContainerLifecycleEnvelope> _channel;
        private int _completed;

        internal Subscription(Channel<ContainerLifecycleEnvelope> channel)
        {
            _channel = channel;
        }

        public IAsyncEnumerable<ContainerLifecycleEnvelope> ReadAllAsync(CancellationToken ct)
            => _channel.Reader.ReadAllAsync(ct);

        public void TryWrite(ContainerLifecycleEnvelope envelope)
        {
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
/// Configuration for <see cref="InMemoryContainerLifecycleBus"/>.
/// </summary>
public sealed class ContainerLifecycleBusOptions
{
    /// <summary>
    /// Fleet-wide ring-buffer capacity. A subscriber that attaches will
    /// be replayed up to this many recent events before tailing live.
    /// Default: 256 (a few minutes of steady-state fleet activity for a
    /// medium-sized dev team).
    /// </summary>
    public int BufferSize { get; init; } = 256;

    /// <summary>
    /// Per-subscriber bounded channel capacity. A slow SSE connection
    /// drops events past this limit rather than backpressuring producers.
    /// Default: 128.
    /// </summary>
    public int SubscriberQueueSize { get; init; } = 128;
}
