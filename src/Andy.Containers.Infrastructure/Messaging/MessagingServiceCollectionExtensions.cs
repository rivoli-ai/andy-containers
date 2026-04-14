// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Containers.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Andy.Containers.Infrastructure.Messaging;

public static class MessagingServiceCollectionExtensions
{
    // Registers the IMessageBus implementation selected by
    // Messaging:Provider configuration (InMemory, default; Nats in a
    // follow-up PR) and wires the OutboxDispatcher background worker.
    //
    // Callers must register ContainersDbContext separately — the
    // dispatcher resolves it from the scope and drains OutboxEntries
    // from whatever provider (Postgres / SQLite) the app is using.
    public static IServiceCollection AddContainersMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var provider = configuration["Messaging:Provider"] ?? "InMemory";

        if (string.Equals(provider, "Nats", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "Messaging:Provider=Nats is not wired yet. Follow-up PR to " +
                "this repo's #78 adds NatsMessageBus + NatsStreamProvisioner. " +
                "For now use Messaging:Provider=InMemory (the default).");
        }

        services.TryAddSingleton<IMessageBus, InMemoryMessageBus>();

        services.Configure<OutboxDispatcherOptions>(
            configuration.GetSection(OutboxDispatcherOptions.SectionName));
        services.AddHostedService<OutboxDispatcher>();

        return services;
    }
}
