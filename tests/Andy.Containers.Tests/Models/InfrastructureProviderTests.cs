using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Models;

public class InfrastructureProviderTests
{
    [Fact]
    public void NewProvider_ShouldBeEnabled()
    {
        var provider = new InfrastructureProvider { Code = "docker-local", Name = "Local Docker" };

        provider.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void NewProvider_ShouldHaveUnknownHealthStatus()
    {
        var provider = new InfrastructureProvider { Code = "docker-local", Name = "Local Docker" };

        provider.HealthStatus.Should().Be(ProviderHealth.Unknown);
    }

    [Fact]
    public void NewProvider_ShouldHaveCreatedAtSet()
    {
        var before = DateTime.UtcNow;
        var provider = new InfrastructureProvider { Code = "docker-local", Name = "Local Docker" };
        var after = DateTime.UtcNow;

        provider.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void NewProvider_NullableProperties_ShouldBeNull()
    {
        var provider = new InfrastructureProvider { Code = "test", Name = "Test" };

        provider.ConnectionConfig.Should().BeNull();
        provider.Capabilities.Should().BeNull();
        provider.Region.Should().BeNull();
        provider.OrganizationId.Should().BeNull();
        provider.LastHealthCheck.Should().BeNull();
        provider.Metadata.Should().BeNull();
    }

    [Theory]
    [InlineData(ProviderType.Docker)]
    [InlineData(ProviderType.AppleContainer)]
    [InlineData(ProviderType.Rivoli)]
    [InlineData(ProviderType.Ssh)]
    [InlineData(ProviderType.AzureAci)]
    [InlineData(ProviderType.AzureAca)]
    [InlineData(ProviderType.AzureAcp)]
    public void ProviderType_ShouldAcceptAllValues(ProviderType type)
    {
        var provider = new InfrastructureProvider { Code = "test", Name = "Test" };
        provider.Type = type;

        provider.Type.Should().Be(type);
    }

    [Theory]
    [InlineData(ProviderHealth.Unknown)]
    [InlineData(ProviderHealth.Healthy)]
    [InlineData(ProviderHealth.Degraded)]
    [InlineData(ProviderHealth.Unreachable)]
    public void ProviderHealth_ShouldAcceptAllValues(ProviderHealth health)
    {
        var provider = new InfrastructureProvider { Code = "test", Name = "Test" };
        provider.HealthStatus = health;

        provider.HealthStatus.Should().Be(health);
    }

    [Fact]
    public void Provider_ShouldStoreAllProperties()
    {
        var orgId = Guid.NewGuid();
        var provider = new InfrastructureProvider
        {
            Code = "azure-eastus",
            Name = "Azure East US",
            Type = ProviderType.AzureAci,
            Region = "eastus",
            OrganizationId = orgId,
            ConnectionConfig = "{\"subscriptionId\":\"sub-123\"}",
            Capabilities = "{\"supportsGpu\":true}",
            IsEnabled = false,
            HealthStatus = ProviderHealth.Healthy,
            LastHealthCheck = DateTime.UtcNow
        };

        provider.Code.Should().Be("azure-eastus");
        provider.Name.Should().Be("Azure East US");
        provider.Type.Should().Be(ProviderType.AzureAci);
        provider.Region.Should().Be("eastus");
        provider.OrganizationId.Should().Be(orgId);
        provider.IsEnabled.Should().BeFalse();
        provider.HealthStatus.Should().Be(ProviderHealth.Healthy);
        provider.LastHealthCheck.Should().NotBeNull();
    }
}
