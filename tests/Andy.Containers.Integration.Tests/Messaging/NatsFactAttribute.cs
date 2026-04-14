// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Xunit;

namespace Andy.Containers.Integration.Tests.Messaging;

// Fact attribute that skips when NATS is not available. Set
// ANDY_CONTAINERS_TEST_NATS=true and point Messaging__Nats__Url (or
// NATS_URL) at a running JetStream server to run these tests — same
// gating pattern andy-tasks uses for its own NATS integration tests.
public sealed class NatsFactAttribute : FactAttribute
{
    private const string EnvVar = "ANDY_CONTAINERS_TEST_NATS";

    public NatsFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnvVar),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip = $"NATS integration tests require {EnvVar}=true and a running NATS server with JetStream";
        }
    }
}
