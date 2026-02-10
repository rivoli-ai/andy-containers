using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Models;

public class ContainerTemplateTests
{
    [Fact]
    public void NewTemplate_ShouldHaveDefaultCatalogScope_Global()
    {
        var template = new ContainerTemplate
        {
            Code = "test",
            Name = "Test Template",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };

        template.CatalogScope.Should().Be(CatalogScope.Global);
    }

    [Fact]
    public void NewTemplate_ShouldHaveDefaultIdeType_CodeServer()
    {
        var template = new ContainerTemplate
        {
            Code = "test",
            Name = "Test Template",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };

        template.IdeType.Should().Be(IdeType.CodeServer);
    }

    [Fact]
    public void NewTemplate_ShouldNotBePublished()
    {
        var template = new ContainerTemplate
        {
            Code = "test",
            Name = "Test Template",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };

        template.IsPublished.Should().BeFalse();
    }

    [Fact]
    public void NewTemplate_GpuFlags_ShouldBeFalse()
    {
        var template = new ContainerTemplate
        {
            Code = "test",
            Name = "Test Template",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };

        template.GpuRequired.Should().BeFalse();
        template.GpuPreferred.Should().BeFalse();
    }

    [Fact]
    public void NewTemplate_ShouldHaveCreatedAtSet()
    {
        var before = DateTime.UtcNow;
        var template = new ContainerTemplate
        {
            Code = "test",
            Name = "Test Template",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };
        var after = DateTime.UtcNow;

        template.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Theory]
    [InlineData(CatalogScope.Global)]
    [InlineData(CatalogScope.Organization)]
    [InlineData(CatalogScope.Team)]
    [InlineData(CatalogScope.User)]
    public void CatalogScope_ShouldAcceptAllValues(CatalogScope scope)
    {
        var template = new ContainerTemplate
        {
            Code = "test",
            Name = "Test",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };
        template.CatalogScope = scope;

        template.CatalogScope.Should().Be(scope);
    }

    [Theory]
    [InlineData(IdeType.None)]
    [InlineData(IdeType.CodeServer)]
    [InlineData(IdeType.Zed)]
    [InlineData(IdeType.Both)]
    public void IdeType_ShouldAcceptAllValues(IdeType ideType)
    {
        var template = new ContainerTemplate
        {
            Code = "test",
            Name = "Test",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04"
        };
        template.IdeType = ideType;

        template.IdeType.Should().Be(ideType);
    }

    [Fact]
    public void Template_Tags_ShouldBeSettable()
    {
        var template = new ContainerTemplate
        {
            Code = "test",
            Name = "Test",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
            Tags = new[] { "dotnet", "fullstack", "gpu" }
        };

        template.Tags.Should().HaveCount(3).And.Contain("dotnet");
    }
}
