// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Xunit;

namespace Andy.Containers.Api.Tests.Telemetry;

/// <summary>
/// OT5 (rivoli-ai/conductor#1263). The local
/// <c>OpenTelemetryExtensions.AddAndyTelemetry</c> shim was deleted —
/// Program.cs now calls <see cref="AndyTelemetryExtensions.AddAndyTelemetry"/>
/// from the shared library directly. These tests assert the shared
/// library's surface stays usable from this repo and that supplying
/// the canonical configure delegate does not throw at registration
/// time.
/// </summary>
public class OpenTelemetryExtensionsTests
{
    [Fact]
    public void SharedAndyTelemetry_registers_services_without_throwing()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AndyTelemetry:ServiceName"] = "andy-containers"
            })
            .Build();

        var act = () => services.AddAndyTelemetry(configuration);

        act.Should().NotThrow();
    }

    [Fact]
    public void SharedAndyTelemetry_with_otlp_endpoint_does_not_throw()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AndyTelemetry:ServiceName"] = "andy-containers",
                ["AndyTelemetry:OtlpEndpoint"] = "http://localhost:4318",
                ["AndyTelemetry:Protocol"] = "http/protobuf",
            })
            .Build();

        var act = () => services.AddAndyTelemetry(configuration);

        act.Should().NotThrow();
    }

    [Fact]
    public void SharedAndyTelemetry_with_no_otlp_endpoint_does_not_throw()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AndyTelemetry:ServiceName"] = "andy-containers",
                ["AndyTelemetry:OtlpEndpoint"] = ""
            })
            .Build();

        var act = () => services.AddAndyTelemetry(configuration);

        act.Should().NotThrow();
    }

    [Fact]
    public void SharedAndyTelemetry_resolves_options_with_aspnet_instrumentation_disabled()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AndyTelemetry:ServiceName"] = "andy-containers",
            })
            .Build();

        services.AddAndyTelemetry(configuration, o =>
        {
            o.EnableAspNetCoreInstrumentation = false;
            o.EnableHttpClientInstrumentation = true;
            o.ActivitySources.Add("Andy.Containers.Provisioning");
            o.Meters.Add("Andy.Containers");
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AndyTelemetryOptions>();

        options.ServiceName.Should().Be("andy-containers");
        options.EnableAspNetCoreInstrumentation.Should().BeFalse();
        options.EnableHttpClientInstrumentation.Should().BeTrue();
        options.ActivitySources.Should().Contain("Andy.Containers.Provisioning");
        options.Meters.Should().Contain("Andy.Containers");
    }
}
