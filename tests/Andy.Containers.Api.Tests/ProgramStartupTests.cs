using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Andy.Containers.Api.Tests;

/// <summary>
/// Pins the fail-loud contract for andy-settings configuration.
///
/// andy-containers must refuse to start when <c>AndySettings:ApiBaseUrl</c>
/// is missing. Without this guard, the service would happily boot and then
/// silently fail the moment a request needs a provider credential or LLM
/// key — exactly the "works locally but fails on a fresh clone" class of
/// bug that epic rivoli-ai/conductor#771 deletes.
/// </summary>
public class ProgramStartupTests
{
    [Fact]
    public void Host_refuses_to_start_when_AndySettings_ApiBaseUrl_is_empty()
    {
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Rbac:ApiBaseUrl"] = "https://localhost:7003",
                        ["AndySettings:ApiBaseUrl"] = "",
                    });
                });
            });

        Action act = () => _ = factory.Services;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AndySettings:ApiBaseUrl must be configured*");
    }

    [Fact]
    public void Host_refuses_to_start_when_AndySettings_ApiBaseUrl_is_whitespace()
    {
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Rbac:ApiBaseUrl"] = "https://localhost:7003",
                        ["AndySettings:ApiBaseUrl"] = "   ",
                    });
                });
            });

        Action act = () => _ = factory.Services;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AndySettings:ApiBaseUrl must be configured*");
    }
}
