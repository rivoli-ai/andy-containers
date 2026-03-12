using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests;

public class VersionParserTests
{
    private readonly ToolVersionDetector _detector = new();

    [Fact]
    public void ParseIntrospectionOutput_ValidJson_ReturnTools()
    {
        var json = """
        {"tools":[
            {"name":"dotnet-sdk","version":"8.0.404","path":"/usr/share/dotnet"},
            {"name":"python","version":"3.12.8","path":"/usr/bin/python3"},
            {"name":"node","version":"20.18.1","path":"/usr/bin/node"}
        ]}
        """;

        var tools = _detector.ParseIntrospectionOutput(json);
        tools.Should().HaveCount(3);
        tools[0].Name.Should().Be("dotnet-sdk");
        tools[0].Version.Should().Be("8.0.404");
        tools[1].Name.Should().Be("python");
        tools[1].Version.Should().Be("3.12.8");
        tools[2].Name.Should().Be("node");
        tools[2].Version.Should().Be("20.18.1");
    }

    [Fact]
    public void ParseIntrospectionOutput_EmptyJson_ReturnsEmpty()
    {
        var tools = _detector.ParseIntrospectionOutput("""{"tools":[]}""");
        tools.Should().BeEmpty();
    }

    [Fact]
    public void ParseIntrospectionOutput_InvalidJson_ReturnsEmpty()
    {
        var tools = _detector.ParseIntrospectionOutput("not json");
        tools.Should().BeEmpty();
    }

    [Fact]
    public void ParseIntrospectionOutput_WithDeclaredDeps_MatchesDeclared()
    {
        var json = """{"tools":[{"name":"dotnet-sdk","version":"8.0.404","path":"/usr/share/dotnet"}]}""";
        var declared = new List<DependencySpec>
        {
            new() { Id = Guid.NewGuid(), TemplateId = Guid.NewGuid(), Name = "dotnet-sdk", VersionConstraint = "8.0.*", Type = DependencyType.Sdk }
        };

        var tools = _detector.ParseIntrospectionOutput(json, declared);
        tools.Should().HaveCount(1);
        tools[0].DeclaredVersion.Should().Be("8.0.*");
    }

    [Fact]
    public void ParseOsInfo_ValidInput_ReturnsOsInfo()
    {
        var osRelease = "NAME=\"Ubuntu\"\nVERSION_ID=\"24.04\"\nVERSION_CODENAME=noble";
        var osInfo = _detector.ParseOsInfo(osRelease, "x86_64", "6.5.0-generic");

        osInfo.Name.Should().Be("Ubuntu");
        osInfo.Version.Should().Be("24.04");
        osInfo.Codename.Should().Be("noble");
        osInfo.KernelVersion.Should().Be("6.5.0-generic");
    }

    [Fact]
    public void GenerateIntrospectionScript_ReturnsNonEmptyScript()
    {
        var script = _detector.GenerateIntrospectionScript();
        script.Should().NotBeNullOrEmpty();
        script.Should().Contain("#!/bin/bash");
        script.Should().Contain("dotnet");
        script.Should().Contain("python");
        script.Should().Contain("node");
    }
}

public class ContentHashCalculatorTests
{
    [Fact]
    public void ComputeHash_SameInput_SameHash()
    {
        var tools = new List<(string Name, string Version)>
        {
            ("dotnet-sdk", "8.0.404"),
            ("python", "3.12.8")
        };

        var hash1 = ContentHashCalculator.ComputeHash(tools, "sha256:abc", "amd64");
        var hash2 = ContentHashCalculator.ComputeHash(tools, "sha256:abc", "amd64");

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void ComputeHash_DifferentInput_DifferentHash()
    {
        var tools1 = new List<(string Name, string Version)> { ("dotnet-sdk", "8.0.404") };
        var tools2 = new List<(string Name, string Version)> { ("dotnet-sdk", "8.0.405") };

        var hash1 = ContentHashCalculator.ComputeHash(tools1, "sha256:abc", "amd64");
        var hash2 = ContentHashCalculator.ComputeHash(tools2, "sha256:abc", "amd64");

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void ComputeHash_OrderDoesNotMatter()
    {
        var tools1 = new List<(string Name, string Version)>
        {
            ("python", "3.12.8"),
            ("dotnet-sdk", "8.0.404")
        };
        var tools2 = new List<(string Name, string Version)>
        {
            ("dotnet-sdk", "8.0.404"),
            ("python", "3.12.8")
        };

        var hash1 = ContentHashCalculator.ComputeHash(tools1, "sha256:abc", "amd64");
        var hash2 = ContentHashCalculator.ComputeHash(tools2, "sha256:abc", "amd64");

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void ComputeHash_StartsWithSha256Prefix()
    {
        var tools = new List<(string Name, string Version)> { ("node", "20.0.0") };
        var hash = ContentHashCalculator.ComputeHash(tools, "sha256:abc", "amd64");
        hash.Should().StartWith("sha256:");
    }
}

public class VersionComparerTests
{
    [Theory]
    [InlineData("1.0.0", "2.0.0", "Major")]
    [InlineData("1.0.0", "1.1.0", "Minor")]
    [InlineData("1.0.0", "1.0.1", "Patch")]
    public void ClassifySeverity_ReturnsCorrectResult(string from, string to, string expected)
    {
        var result = VersionComparer.ClassifySeverity(from, to);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("3.12.8", "3.12.0", 1)]
    [InlineData("3.12.8", "4.0.0", -1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    public void Compare_ReturnsCorrectOrder(string a, string b, int expectedSign)
    {
        var result = VersionComparer.Compare(a, b);
        Math.Sign(result).Should().Be(expectedSign);
    }

    [Theory]
    [InlineData("8.0.*", "8.0.404", true)]
    [InlineData("8.0.*", "9.0.100", false)]
    [InlineData(">=3.12,<4.0", "3.12.8", true)]
    [InlineData(">=3.12,<4.0", "3.11.9", false)]
    [InlineData("latest", "99.99.99", true)]
    [InlineData("3.12.8", "3.12.8", true)]
    [InlineData("3.12.8", "3.12.9", false)]
    public void SatisfiesConstraint_ReturnsCorrectResult(string constraint, string version, bool expected)
    {
        var result = VersionComparer.SatisfiesConstraint(constraint, version);
        result.Should().Be(expected);
    }
}
