// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
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

        if (!opts.ProvisionStream)
        {
            // Embedded/shared-broker daemon: a peer (andy-tasks) owns the
            // stream that covers our subjects. Don't provision — just stay
            // connected and publish; the OutboxDispatcher retries any rows
            // that race ahead of the peer's stream creation. Avoids the
            // same-startup-wave race where this service would otherwise
            // create a broad `andy.>` stream that blocks the peer's
            // narrower ANDY_PROGRESS / ANDY_DOMAIN provisioning.
            logger.LogInformation(
                "NATS JetStream provisioning skipped (ProvisionStream=false); " +
                "publishing to peer-owned streams.");
            return;
        }

        var config = new StreamConfig(opts.StreamName, opts.StreamSubjects)
        {
            MaxAge = opts.MaxAge
        };

        try
        {
            await bus.JetStream.CreateOrUpdateStreamAsync(config, ct);

            logger.LogInformation(
                "NATS JetStream stream {Stream} provisioned with subjects [{Subjects}]",
                opts.StreamName, string.Join(", ", opts.StreamSubjects));
        }
        catch (NatsJSApiException ex) when (
            ex.Error.Code == 400 &&
            (ex.Error.Description?.Contains("overlap", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            // A PEER service (e.g. andy-tasks, which owns ANDY_PROGRESS +
            // ANDY_DOMAIN) has already provisioned a stream whose subjects
            // cover ours. JetStream forbids two streams with overlapping
            // subjects, so our CreateOrUpdate is rejected — but the existing
            // stream already captures everything we publish
            // (`andy.containers.events.>`), so publishing still works. In a
            // shared-broker (embedded daemon) deployment this is the EXPECTED
            // steady state, not an error: swallow it so andy-containers does
            // not crash-loop on startup. (Standalone deployments where this
            // service owns its stream still create it on first boot.)
            logger.LogInformation(
                "NATS JetStream stream {Stream} not provisioned: its subjects [{Subjects}] " +
                "overlap a peer-owned stream that already covers them — publishing to the " +
                "existing stream. ({Detail})",
                opts.StreamName, string.Join(", ", opts.StreamSubjects), ex.Error.Description);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
