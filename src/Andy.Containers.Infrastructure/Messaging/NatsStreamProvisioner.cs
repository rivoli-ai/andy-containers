// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream.Models;

namespace Andy.Containers.Infrastructure.Messaging;

// Ensures the JetStream stream exists before any BackgroundService
// (OutboxDispatcher, future consumers) starts publishing or
// subscribing. IHostedService.StartAsync runs before BackgroundService
// .ExecuteAsync, so the ordering guarantee is built into the host.
// CreateOrUpdateStreamAsync is idempotent — safe on every boot.
public sealed class NatsStreamProvisioner(
    NatsMessageBus bus,
    IOptions<NatsOptions> options,
    ILogger<NatsStreamProvisioner> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await bus.ConnectAsync(ct);

        var opts = options.Value;
        var config = new StreamConfig(opts.StreamName, opts.StreamSubjects)
        {
            MaxAge = opts.MaxAge
        };

        await bus.JetStream.CreateOrUpdateStreamAsync(config, ct);

        logger.LogInformation(
            "NATS JetStream stream {Stream} provisioned with subjects [{Subjects}]",
            opts.StreamName, string.Join(", ", opts.StreamSubjects));
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
