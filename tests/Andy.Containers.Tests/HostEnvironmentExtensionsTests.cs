using Andy.Containers.Api.Services;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Andy.Containers.Tests;

public class HostEnvironmentExtensionsTests
{
    [Theory]
    [InlineData("Embedded", true)]
    [InlineData("Development", false)]
    [InlineData("Docker", false)]
    [InlineData("Production", false)]
    public void IsEmbedded_ReturnsExpected(string envName, bool expected)
    {
        Assert.Equal(expected, new FakeEnv(envName).IsEmbedded());
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Docker", true)]
    [InlineData("Embedded", true)]
    [InlineData("Production", false)]
    public void IsLocalOrEmbedded_TrueForNonProduction(string envName, bool expected)
    {
        Assert.Equal(expected, new FakeEnv(envName).IsLocalOrEmbedded());
    }

    [Fact]
    public void EmbeddedEnvironmentName_MatchesConductorContract()
    {
        // Kept in lock-step with ServiceEnvironment.embeddedEnvironmentName
        // on the Swift side. Drift here = silent Embedded-branch no-op.
        Assert.Equal("Embedded", HostEnvironmentExtensions.EmbeddedEnvironmentName);
    }

    private sealed class FakeEnv : IHostEnvironment
    {
        public FakeEnv(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
