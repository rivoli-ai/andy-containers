using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Models;

public class ContainerTests
{
    [Fact]
    public void NewContainer_ShouldHaveDefaultStatus_Pending()
    {
        var container = new Container { Name = "test", OwnerId = "user1" };

        container.Status.Should().Be(ContainerStatus.Pending);
    }

    [Fact]
    public void NewContainer_ShouldHaveCreatedAtSet()
    {
        var before = DateTime.UtcNow;
        var container = new Container { Name = "test", OwnerId = "user1" };
        var after = DateTime.UtcNow;

        container.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void NewContainer_ShouldHaveEmptySessionsCollection()
    {
        var container = new Container { Name = "test", OwnerId = "user1" };

        container.Sessions.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void NewContainer_ShouldHaveEmptyEventsCollection()
    {
        var container = new Container { Name = "test", OwnerId = "user1" };

        container.Events.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void NewContainer_NullableProperties_ShouldBeNull()
    {
        var container = new Container { Name = "test", OwnerId = "user1" };

        container.ExternalId.Should().BeNull();
        container.Template.Should().BeNull();
        container.Provider.Should().BeNull();
        container.AllocatedResources.Should().BeNull();
        container.NetworkConfig.Should().BeNull();
        container.IdeEndpoint.Should().BeNull();
        container.VncEndpoint.Should().BeNull();
        container.GitRepository.Should().BeNull();
        container.EnvironmentVariables.Should().BeNull();
        container.StartedAt.Should().BeNull();
        container.StoppedAt.Should().BeNull();
        container.ExpiresAt.Should().BeNull();
        container.LastActivityAt.Should().BeNull();
        container.Metadata.Should().BeNull();
    }

    [Theory]
    [InlineData(ContainerStatus.Pending)]
    [InlineData(ContainerStatus.Creating)]
    [InlineData(ContainerStatus.Running)]
    [InlineData(ContainerStatus.Stopping)]
    [InlineData(ContainerStatus.Stopped)]
    [InlineData(ContainerStatus.Failed)]
    [InlineData(ContainerStatus.Destroying)]
    [InlineData(ContainerStatus.Destroyed)]
    public void ContainerStatus_ShouldAcceptAllValues(ContainerStatus status)
    {
        var container = new Container { Name = "test", OwnerId = "user1" };
        container.Status = status;

        container.Status.Should().Be(status);
    }

    [Fact]
    public void Container_ShouldStoreAllProperties()
    {
        var templateId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();

        var container = new Container
        {
            Name = "my-container",
            OwnerId = "user-42",
            TemplateId = templateId,
            ProviderId = providerId,
            OrganizationId = orgId,
            ExternalId = "ext-123",
            IdeEndpoint = "https://ide.example.com",
            VncEndpoint = "https://vnc.example.com",
            GitRepository = "{\"url\":\"https://github.com/test/repo\"}",
        };

        container.Name.Should().Be("my-container");
        container.OwnerId.Should().Be("user-42");
        container.TemplateId.Should().Be(templateId);
        container.ProviderId.Should().Be(providerId);
        container.OrganizationId.Should().Be(orgId);
        container.ExternalId.Should().Be("ext-123");
        container.IdeEndpoint.Should().Be("https://ide.example.com");
        container.VncEndpoint.Should().Be("https://vnc.example.com");
        container.GitRepository.Should().Contain("github.com");
    }
}
