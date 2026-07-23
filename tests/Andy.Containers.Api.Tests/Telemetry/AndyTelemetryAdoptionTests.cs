// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Diagnostics;
using Andy.Containers.Api.Telemetry;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Telemetry;

/// <summary>
/// OT5 (rivoli-ai/conductor#1263). Verifies that the domain
/// <see cref="ActivitySources"/> + <see cref="Meters"/> registered with
/// <c>AddAndyTelemetry</c> in <c>Program.cs</c> are actually observable —
/// i.e. spans started on the domain sources surface to an
/// <see cref="ActivityListener"/> and counters live on the canonical meter.
///
/// We don't boot the full pipeline here. What we DO assert is that the
/// well-known source / meter names are wired correctly so the SDK doesn't
/// silently drop emissions. If a rename ever breaks the registration in
/// <c>Program.cs</c> without updating the constants below, this test fails.
/// </summary>
public class AndyTelemetryAdoptionTests
{
    [Fact]
    public void Provisioning_ActivitySource_IsListenedTo()
    {
        var operationName = $"ProvisionContainer-{Guid.NewGuid():N}";
        var captured = new ConcurrentQueue<Activity>();
        // Materialize the static source before installing the listener.
        // Referencing ActivitySources.Provisioning from ShouldListenTo while
        // the ActivitySources type initializer is constructing that same
        // source recursively observes a null field in standalone test runs.
        var provisioningSource = ActivitySources.Provisioning;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == provisioningSource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = captured.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        using (var activity = provisioningSource.StartActivity(operationName))
        {
            activity.Should().NotBeNull("an active listener must materialise the activity");
            activity!.SetTag("container.id", "test-container");
        }

        // Activity listeners are process-global and other parallel tests may
        // emit on this source. Snapshot the thread-safe queue and select only
        // the uniquely named activity created above.
        var matching = captured.Where(a => a.OperationName == operationName).ToArray();
        matching.Should().ContainSingle();
        matching[0].GetTagItem("container.id").Should().Be("test-container");
    }

    [Fact]
    public void AllActivitySources_AreEnumerated()
    {
        ActivitySources.All.Should().Contain(new[]
        {
            "Andy.Containers.Provisioning",
            "Andy.Containers.Introspection",
            "Andy.Containers.Git",
            "Andy.Containers.Infrastructure",
            "Andy.Containers.ApiKeys",
        });
    }

    [Fact]
    public void ContainersCreatedCounter_IsOnTheCanonicalMeter()
    {
        Meters.ContainersCreated.Meter.Name.Should().Be("Andy.Containers");
        Meters.ContainersCreated.Name.Should().Be("andy.containers.created");
    }

    [Fact]
    public void AllMeters_ContainTheContainersMeter()
    {
        Meters.All.Should().Contain("Andy.Containers");
    }
}
