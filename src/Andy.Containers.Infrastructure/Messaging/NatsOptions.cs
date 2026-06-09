// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Containers.Infrastructure.Messaging;

public sealed class NatsOptions
{
    public const string SectionName = "Messaging:Nats";

    public string Url { get; set; } = "nats://localhost:4222";
    public string StreamName { get; set; } = "ANDY";
    public string[] StreamSubjects { get; set; } = ["andy.>"];
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(7);
    public string DlqPrefix { get; set; } = "andy.containers.dlq";

    /// When false, this service does NOT provision its JetStream stream on
    /// boot and relies on a peer that already owns a stream covering its
    /// subjects — the embedded/shared-broker daemon, where andy-tasks owns
    /// ANDY_PROGRESS (andy.containers.events.run/container.>) + ANDY_DOMAIN.
    /// Default true preserves standalone behaviour where this service owns
    /// its stream. A scalar bool so it binds reliably from
    /// `Messaging__Nats__ProvisionStream` (env array binding for
    /// StreamSubjects is unreliable, so the embedded daemon flips this flag
    /// rather than trying to override the subjects to match the peer).
    public bool ProvisionStream { get; set; } = true;
}
