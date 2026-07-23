// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Runs.Events;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public sealed class RunOutputSseHeartbeatTests
{
    [Fact]
    public async Task Stream_IdleFollowingConnection_EmitsHeartbeatComments()
    {
        using var bus = new InMemoryRunOutputBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        var context = new DefaultHttpContext();
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        await RunOutputSse.StreamAsync(
            context.Response,
            context.Request,
            bus,
            Guid.NewGuid(),
            cts.Token,
            heartbeatInterval: TimeSpan.FromMilliseconds(10));

        responseBody.Position = 0;
        var body = Encoding.UTF8.GetString(responseBody.ToArray());
        body.Should().Contain(": heartbeat\n\n");
        context.Response.Headers.ContentType.ToString().Should().Be("text/event-stream");
    }
}
