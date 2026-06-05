// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Containers.Api.Services;
using Andy.Containers.Messaging.Events;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

/// <summary>
/// SM.2.6 (rivoli-ai/conductor#2008). Unit tests for
/// <see cref="ContainerProvisioningWorker.ClassifyAbortReason"/> — verifies
/// the exception-to-reason taxonomy covering all branches documented in the
/// AC.
/// </summary>
public class ProvisioningAbortClassifierTests
{
    [Fact]
    public void ClassifyAbortReason_QuotaExceededException_ReturnsQuotaDenied()
    {
        var ex = new QuotaExceededException(limit: 5, current: 5, ownerId: "user");

        var reason = ContainerProvisioningWorker.ClassifyAbortReason(ex);

        reason.Should().Be(ProvisioningAbortReason.QuotaDenied);
    }

    [Theory]
    [InlineData("image not found in registry")]
    [InlineData("manifest unknown")]
    [InlineData("Error: image ghcr.io/foo/bar:latest not found")]
    public void ClassifyAbortReason_ImageNotFoundMessage_ReturnsImageNotFound(string message)
    {
        var ex = new InvalidOperationException(message);

        var reason = ContainerProvisioningWorker.ClassifyAbortReason(ex);

        reason.Should().Be(ProvisioningAbortReason.ImageNotFound);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TimeoutException))]
    public void ClassifyAbortReason_NetworkOrTimeoutException_ReturnsEngineUnavailable(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType, "network error")!;

        var reason = ContainerProvisioningWorker.ClassifyAbortReason(ex);

        reason.Should().Be(ProvisioningAbortReason.EngineUnavailable);
    }

    [Fact]
    public void ClassifyAbortReason_ConnectionRefusedMessage_ReturnsEngineUnavailable()
    {
        var ex = new Exception("connect: connection refused");

        var reason = ContainerProvisioningWorker.ClassifyAbortReason(ex);

        reason.Should().Be(ProvisioningAbortReason.EngineUnavailable);
    }

    [Fact]
    public void ClassifyAbortReason_ArbitraryException_ReturnsUnknown()
    {
        var ex = new InvalidOperationException("some unrelated error");

        var reason = ContainerProvisioningWorker.ClassifyAbortReason(ex);

        reason.Should().Be(ProvisioningAbortReason.Unknown);
    }

    [Theory]
    [InlineData(ProvisioningAbortReason.QuotaDenied,       "quota_denied")]
    [InlineData(ProvisioningAbortReason.ImageNotFound,     "image_not_found")]
    [InlineData(ProvisioningAbortReason.EngineUnavailable, "engine_unavailable")]
    [InlineData(ProvisioningAbortReason.Cancelled,         "cancelled")]
    [InlineData(ProvisioningAbortReason.Timeout,           "timeout")]
    [InlineData(ProvisioningAbortReason.Unknown,           "unknown")]
    public void ToWireString_AllValues_ProduceStableStrings(
        ProvisioningAbortReason reason, string expected)
    {
        reason.ToWireString().Should().Be(expected);
    }

    [Theory]
    [InlineData("quota_denied",       ProvisioningAbortReason.QuotaDenied)]
    [InlineData("image_not_found",    ProvisioningAbortReason.ImageNotFound)]
    [InlineData("engine_unavailable", ProvisioningAbortReason.EngineUnavailable)]
    [InlineData("cancelled",          ProvisioningAbortReason.Cancelled)]
    [InlineData("timeout",            ProvisioningAbortReason.Timeout)]
    [InlineData("unknown",            ProvisioningAbortReason.Unknown)]
    [InlineData("some_future_reason", ProvisioningAbortReason.Unknown)]
    [InlineData(null,                 ProvisioningAbortReason.Unknown)]
    public void FromWireString_AllValues_RoundTrip(string? wire, ProvisioningAbortReason expected)
    {
        ProvisioningAbortReasonExtensions.FromWireString(wire).Should().Be(expected);
    }
}
