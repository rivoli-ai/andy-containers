using Andy.Containers.Api.Services;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests;

public class ToolRegistryTests
{
    [Fact]
    public void KnownTools_ShouldNotBeEmpty()
    {
        ToolRegistry.KnownTools.Should().NotBeEmpty();
    }

    [Fact]
    public void KnownTools_AllShouldHaveNonEmptyName()
    {
        foreach (var tool in ToolRegistry.KnownTools)
        {
            tool.Name.Should().NotBeNullOrWhiteSpace($"tool '{tool.Name}' has empty name");
        }
    }

    [Fact]
    public void KnownTools_AllShouldHaveNonEmptyDetectionCommand()
    {
        foreach (var tool in ToolRegistry.KnownTools)
        {
            tool.DetectionCommand.Should().NotBeNullOrWhiteSpace($"tool '{tool.Name}' has empty detection command");
        }
    }

    [Fact]
    public void KnownTools_ShouldHaveNoDuplicateNames()
    {
        var names = ToolRegistry.KnownTools.Select(t => t.Name).ToList();
        names.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void KnownTools_AllShouldHaveWhichCommand()
    {
        foreach (var tool in ToolRegistry.KnownTools)
        {
            tool.WhichCommand.Should().NotBeNullOrWhiteSpace($"tool '{tool.Name}' has no which command");
        }
    }

    [Fact]
    public void KnownTools_ShouldContainExpectedTools()
    {
        var names = ToolRegistry.KnownTools.Select(t => t.Name).ToHashSet();
        names.Should().Contain("dotnet-sdk");
        names.Should().Contain("python");
        names.Should().Contain("node");
        names.Should().Contain("git");
        names.Should().Contain("curl");
    }

    [Theory]
    [InlineData("ssh", true)]
    [InlineData("java", true)]
    [InlineData("git", false)]
    [InlineData("python", false)]
    public void KnownTools_StderrFlag_ShouldBeCorrect(string name, bool expectedUsesStdErr)
    {
        var tool = ToolRegistry.KnownTools.Single(t => t.Name == name);
        tool.UsesStdErr.Should().Be(expectedUsesStdErr);
    }
}
