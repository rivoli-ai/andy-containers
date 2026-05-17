// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

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
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ActivitySources.Provisioning.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => captured.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);

        using (var activity = ActivitySources.Provisioning.StartActivity("ProvisionContainer"))
        {
            activity.Should().NotBeNull("an active listener must materialise the activity");
            activity!.SetTag("container.id", "test-container");
        }

        captured.Should().ContainSingle();
        captured[0].OperationName.Should().Be("ProvisionContainer");
        captured[0].GetTagItem("container.id").Should().Be("test-container");
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
