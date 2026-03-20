using Andy.Containers.Api.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Containers.Api.Tests.Telemetry;

public class OpenTelemetryExtensionsTests
{
    [Fact]
    public void AddAndyTelemetry_registers_services_without_throwing()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenTelemetry:ServiceName"] = "test-service"
            })
            .Build();

        var act = () => services.AddAndyTelemetry(configuration);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddAndyTelemetry_with_otlp_endpoint_does_not_throw()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenTelemetry:ServiceName"] = "test-service",
                ["OpenTelemetry:OtlpEndpoint"] = "http://localhost:4317"
            })
            .Build();

        var act = () => services.AddAndyTelemetry(configuration);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddAndyTelemetry_with_console_exporter_does_not_throw()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenTelemetry:ServiceName"] = "test-service",
                ["OpenTelemetry:OtlpEndpoint"] = ""
            })
            .Build();

        var act = () => services.AddAndyTelemetry(configuration);

        act.Should().NotThrow();
    }
}
