// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Andy.Containers.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Andy.Containers.Infrastructure.Messaging;

// IMessageBus backed by NATS JetStream per ADR 0001. The connection is
// created once at construction and disposed when the DI container shuts
// down. Stream provisioning (CreateOrUpdateStream) is handled by the
// separate NatsStreamProvisioner hosted service which runs before any
// BackgroundService, guaranteeing the stream exists before the
// OutboxDispatcher starts publishing.
public sealed class NatsMessageBus : IMessageBus, IAsyncDisposable
{
    private readonly NatsOptions _options;
    private readonly ILogger<NatsMessageBus> _logger;
    private readonly NatsConnection _connection;
    private readonly INatsJSContext _jsContext;

    public NatsMessageBus(IOptions<NatsOptions> options, ILogger<NatsMessageBus> logger)
    {
        _options = options.Value;
        _logger = logger;
        _connection = new NatsConnection(new NatsOpts { Url = _options.Url });
        _jsContext = new NatsJSContext(_connection);
    }

    internal INatsJSContext JetStream => _jsContext;
    internal NatsConnection Connection => _connection;

    // Eagerly connect the underlying TCP socket. Called by
    // NatsStreamProvisioner.StartAsync before any publish/subscribe so
    // we don't pay the lazy-connect cost on the first hot-path
    // operation.
    internal async Task ConnectAsync(CancellationToken ct = default)
    {
        await _connection.ConnectAsync();
    }

    public async Task PublishAsync(
        string subject,
        object payload,
        MessageHeaders headers,
        CancellationToken ct = default)
    {
        if (headers.ExceedsGenerationLimit)
        {
            _logger.LogError(
                "Dropping message {MsgId} on {Subject} — generation {Gen} exceeds limit {Max}. " +
                "Correlation: {CorrId} Causation: {CausedBy}",
                headers.MsgId, subject, headers.Generation, MessageHeaders.MaxGeneration,
                headers.CorrelationId, headers.CausationId);
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, EventJson.Options);
        var natsHeaders = ToNatsHeaders(headers);

        var ack = await _jsContext.PublishAsync(subject, bytes, headers: natsHeaders, cancellationToken: ct);

        if (ack.Error is not null)
        {
            throw new InvalidOperationException(
                $"NATS JetStream publish rejected on {subject}: {ack.Error.Code} {ack.Error.Description}");
        }
    }

    public async IAsyncEnumerable<IncomingMessage> SubscribeAsync(
        string subjectFilter,
        SubscriptionOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // NATS 2.10+ embeds the consumer name in the subject for the
        // CREATE API ($JS.API.CONSUMER.CREATE.<stream>.<name>). Dots in
        // the name break subject parsing. Sanitize to dashes.
        var safeDurableName = options.DurableName.Replace('.', '-');

        var consumerConfig = new ConsumerConfig(safeDurableName)
        {
            FilterSubject = options.SubjectFilter ?? subjectFilter,
            AckPolicy = options.ManualAck
                ? ConsumerConfigAckPolicy.Explicit
                : ConsumerConfigAckPolicy.None,
            MaxDeliver = options.MaxDeliver
        };

        var consumer = await _jsContext.CreateOrUpdateConsumerAsync(
            _options.StreamName, consumerConfig, ct);

        _logger.LogDebug(
            "Subscription opened on {Filter} durable {Durable}",
            subjectFilter, options.DurableName);

        await foreach (var jsMsg in consumer.ConsumeAsync<byte[]>(cancellationToken: ct))
        {
            var parsed = TryParseHeaders(jsMsg);
            if (parsed is null)
            {
                _logger.LogWarning(
                    "Dropping message on {Subject} — missing or malformed required headers. " +
                    "Acking to prevent redelivery loop",
                    jsMsg.Subject);
                await PublishToDlqAsync(jsMsg.Subject, jsMsg.Data, jsMsg.Headers, ct);
                await jsMsg.AckAsync(cancellationToken: ct);
                continue;
            }

            if (parsed.ExceedsGenerationLimit)
            {
                _logger.LogError(
                    "Dropping message {MsgId} on {Subject} — generation {Gen} exceeds limit {Max}. " +
                    "Correlation: {CorrId} Causation: {CausedBy}",
                    parsed.MsgId, jsMsg.Subject, parsed.Generation, MessageHeaders.MaxGeneration,
                    parsed.CorrelationId, parsed.CausationId);
                await PublishToDlqAsync(jsMsg.Subject, jsMsg.Data, jsMsg.Headers, ct);
                await jsMsg.AckAsync(cancellationToken: ct);
                continue;
            }

            yield return new NatsIncomingMessage(jsMsg)
            {
                Headers = parsed,
                Subject = jsMsg.Subject,
                Payload = jsMsg.Data ?? ReadOnlyMemory<byte>.Empty,
                ReceivedAt = DateTimeOffset.UtcNow
            };
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    private static NatsHeaders ToNatsHeaders(MessageHeaders headers)
    {
        return new NatsHeaders
        {
            { "Nats-Msg-Id", headers.MsgId.ToString() },
            { "Andy-Correlation-Id", headers.CorrelationId.ToString() },
            { "Andy-Causation-Id", headers.CausationId?.ToString() ?? "" },
            { "Andy-Generation", headers.Generation.ToString() }
        };
    }

    private static MessageHeaders? TryParseHeaders(INatsJSMsg<byte[]> jsMsg)
    {
        if (jsMsg.Headers is null)
            return null;

        var h = jsMsg.Headers;

        if (!h.TryGetValue("Nats-Msg-Id", out var msgIdValues)
            || !Guid.TryParse(msgIdValues.ToString(), out var msgId))
            return null;

        if (!h.TryGetValue("Andy-Correlation-Id", out var corrValues)
            || !Guid.TryParse(corrValues.ToString(), out var correlationId))
            return null;

        if (!h.TryGetValue("Andy-Generation", out var genValues)
            || !int.TryParse(genValues.ToString(), out var generation))
            return null;

        Guid? causationId = null;
        if (h.TryGetValue("Andy-Causation-Id", out var causValues))
        {
            var raw = causValues.ToString();
            if (!string.IsNullOrEmpty(raw) && Guid.TryParse(raw, out var parsed))
                causationId = parsed;
        }

        return new MessageHeaders(msgId, correlationId, causationId, generation);
    }

    private async Task PublishToDlqAsync(
        string originalSubject,
        byte[]? payload,
        NatsHeaders? originalHeaders,
        CancellationToken ct)
    {
        try
        {
            var dlqSubject = $"{_options.DlqPrefix}.{originalSubject}";
            await _jsContext.PublishAsync(
                dlqSubject,
                payload ?? [],
                headers: originalHeaders,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to publish to DLQ for {OriginalSubject} — message is lost",
                originalSubject);
        }
    }
}

// Wraps an INatsJSMsg<byte[]> so consumers call Ack/Nack through the
// IncomingMessage abstraction without knowing about the NATS client.
internal sealed class NatsIncomingMessage : IncomingMessage
{
    private readonly INatsJSMsg<byte[]> _jsMsg;

    internal NatsIncomingMessage(INatsJSMsg<byte[]> jsMsg)
    {
        _jsMsg = jsMsg;
    }

    public override async Task AckAsync(CancellationToken ct = default)
    {
        await _jsMsg.AckAsync(cancellationToken: ct);
    }

    public override async Task NackAsync(CancellationToken ct = default)
    {
        await _jsMsg.NakAsync(cancellationToken: ct);
    }
}
