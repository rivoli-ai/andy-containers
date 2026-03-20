using Bunit;
using Andy.Containers.Web.Shared;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Web.Tests;

public class StatusBadgeTests : TestContext
{
    [Fact]
    public void Renders_WithRunningStatus()
    {
        var cut = RenderComponent<StatusBadge>(p => p.Add(s => s.Status, "Running"));

        var span = cut.Find("span.status-badge");
        span.ClassList.Should().Contain("running");
        span.TextContent.Should().Be("Running");
    }

    [Fact]
    public void Renders_WithStoppedStatus()
    {
        var cut = RenderComponent<StatusBadge>(p => p.Add(s => s.Status, "Stopped"));

        var span = cut.Find("span.status-badge");
        span.ClassList.Should().Contain("stopped");
        span.TextContent.Should().Be("Stopped");
    }

    [Fact]
    public void Renders_WithFailedStatus()
    {
        var cut = RenderComponent<StatusBadge>(p => p.Add(s => s.Status, "Failed"));

        var span = cut.Find("span.status-badge");
        span.ClassList.Should().Contain("failed");
        span.TextContent.Should().Be("Failed");
    }

    [Fact]
    public void Renders_WithPendingStatus()
    {
        var cut = RenderComponent<StatusBadge>(p => p.Add(s => s.Status, "Pending"));

        var span = cut.Find("span.status-badge");
        span.ClassList.Should().Contain("pending");
        span.TextContent.Should().Be("Pending");
    }

    [Fact]
    public void Renders_WithCreatingStatus()
    {
        var cut = RenderComponent<StatusBadge>(p => p.Add(s => s.Status, "Creating"));

        var span = cut.Find("span.status-badge");
        span.ClassList.Should().Contain("creating");
        span.TextContent.Should().Be("Creating");
    }

    [Fact]
    public void Renders_WithEmptyString()
    {
        var cut = RenderComponent<StatusBadge>(p => p.Add(s => s.Status, ""));

        var span = cut.Find("span.status-badge");
        span.TextContent.Should().BeEmpty();
    }
}
