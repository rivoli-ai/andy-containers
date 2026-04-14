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
}
