using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Models;

public class DependencySpecTests
{
    [Fact]
    public void NewDependencySpec_ShouldDefaultAutoUpdate_True()
    {
        var spec = new DependencySpec { Name = "dotnet-sdk", VersionConstraint = "8.0.*" };

        spec.AutoUpdate.Should().BeTrue();
    }

    [Fact]
    public void NewDependencySpec_ShouldDefaultUpdatePolicy_Minor()
    {
        var spec = new DependencySpec { Name = "dotnet-sdk", VersionConstraint = "8.0.*" };

        spec.UpdatePolicy.Should().Be(UpdatePolicy.Minor);
    }

    [Fact]
    public void NewDependencySpec_OptionalFields_ShouldBeNull()
    {
        var spec = new DependencySpec { Name = "node", VersionConstraint = "18.x" };

        spec.Ecosystem.Should().BeNull();
        spec.Metadata.Should().BeNull();
        spec.Template.Should().BeNull();
    }

    [Theory]
    [InlineData(DependencyType.Compiler)]
    [InlineData(DependencyType.Runtime)]
    [InlineData(DependencyType.Sdk)]
    [InlineData(DependencyType.Tool)]
    [InlineData(DependencyType.OsPackage)]
    [InlineData(DependencyType.Library)]
    [InlineData(DependencyType.Extension)]
    [InlineData(DependencyType.Image)]
    public void DependencyType_ShouldAcceptAllValues(DependencyType type)
    {
        var spec = new DependencySpec { Name = "test", VersionConstraint = "1.0" };
        spec.Type = type;

        spec.Type.Should().Be(type);
    }

    [Theory]
    [InlineData(UpdatePolicy.SecurityOnly)]
    [InlineData(UpdatePolicy.Patch)]
    [InlineData(UpdatePolicy.Minor)]
    [InlineData(UpdatePolicy.Major)]
    [InlineData(UpdatePolicy.Manual)]
    public void UpdatePolicy_ShouldAcceptAllValues(UpdatePolicy policy)
    {
        var spec = new DependencySpec { Name = "test", VersionConstraint = "1.0" };
        spec.UpdatePolicy = policy;

        spec.UpdatePolicy.Should().Be(policy);
    }

    [Fact]
    public void DependencySpec_ShouldStoreAllProperties()
    {
        var templateId = Guid.NewGuid();
        var spec = new DependencySpec
        {
            Name = "numpy",
            VersionConstraint = ">=1.24,<2.0",
            Type = DependencyType.Library,
            Ecosystem = "pip",
            TemplateId = templateId,
            AutoUpdate = false,
            UpdatePolicy = UpdatePolicy.SecurityOnly,
            SortOrder = 5,
            Metadata = "{\"notes\":\"for ml\"}"
        };

        spec.Name.Should().Be("numpy");
        spec.VersionConstraint.Should().Be(">=1.24,<2.0");
        spec.Type.Should().Be(DependencyType.Library);
        spec.Ecosystem.Should().Be("pip");
        spec.TemplateId.Should().Be(templateId);
        spec.AutoUpdate.Should().BeFalse();
        spec.UpdatePolicy.Should().Be(UpdatePolicy.SecurityOnly);
        spec.SortOrder.Should().Be(5);
    }

    [Fact]
    public void ResolvedDependency_ShouldDefaultFromOfflineCache_False()
    {
        var resolved = new ResolvedDependency { ResolvedVersion = "8.0.404" };

        resolved.FromOfflineCache.Should().BeFalse();
    }

    [Fact]
    public void ResolvedDependency_ShouldHaveResolvedAtSet()
    {
        var before = DateTime.UtcNow;
        var resolved = new ResolvedDependency { ResolvedVersion = "3.12.8" };
        var after = DateTime.UtcNow;

        resolved.ResolvedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }
}
