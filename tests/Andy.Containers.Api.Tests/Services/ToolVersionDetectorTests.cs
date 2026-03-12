using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class ToolVersionDetectorTests
{
    private readonly ToolVersionDetector _detector = new();

    [Fact]
    public void ParseDpkgOutput_ValidOutput_ReturnsPackages()
    {
        var output = "libssl3\t3.0.13-0ubuntu3.4\tamd64\ncurl\t8.5.0-2ubuntu10.4\tamd64\ngit\t1:2.43.0-1ubuntu7.1\tamd64";

        var packages = _detector.ParseDpkgOutput(output);

        packages.Should().HaveCount(3);
        packages[0].Name.Should().Be("libssl3");
        packages[0].Version.Should().Be("3.0.13-0ubuntu3.4");
        packages[0].Architecture.Should().Be("amd64");
        packages[1].Name.Should().Be("curl");
        packages[2].Name.Should().Be("git");
    }

    [Fact]
    public void ParseDpkgOutput_EmptyOutput_ReturnsEmpty()
    {
        var packages = _detector.ParseDpkgOutput("");

        packages.Should().BeEmpty();
    }

    [Fact]
    public void ParseDpkgOutput_TwoColumnOutput_ReturnsPackagesWithoutArch()
    {
        var output = "nano\t7.2-1\nwget\t1.21.4-1ubuntu4.1";

        var packages = _detector.ParseDpkgOutput(output);

        packages.Should().HaveCount(2);
        packages[0].Name.Should().Be("nano");
        packages[0].Architecture.Should().BeNull();
    }

    [Fact]
    public void ParseOsInfo_EmptyInput_ReturnsDefaults()
    {
        var info = _detector.ParseOsInfo("", "x86_64", "6.5.0");

        info.Name.Should().Be("Unknown");
        info.Version.Should().Be("Unknown");
        info.KernelVersion.Should().Be("6.5.0");
    }

    [Fact]
    public void ParseOsInfo_QuotedValues_StripsQuotes()
    {
        var osRelease = "NAME=\"Alpine Linux\"\nVERSION_ID=\"3.19\"";

        var info = _detector.ParseOsInfo(osRelease, "aarch64", "6.1.0");

        info.Name.Should().Be("Alpine Linux");
        info.Version.Should().Be("3.19");
    }

    [Fact]
    public void ParseIntrospectionOutput_WithToolType_ParsesCorrectly()
    {
        var json = """{"tools":[{"name":"rustc","version":"1.82.0","type":"Compiler","path":"/usr/bin/rustc"}]}""";

        var tools = _detector.ParseIntrospectionOutput(json);

        tools.Should().HaveCount(1);
        tools[0].Type.Should().Be(DependencyType.Compiler);
        tools[0].BinaryPath.Should().Be("/usr/bin/rustc");
    }

    [Fact]
    public void ParseIntrospectionOutput_MissingToolsKey_ReturnsEmpty()
    {
        var json = """{"os":{"name":"Ubuntu"}}""";

        var tools = _detector.ParseIntrospectionOutput(json);

        tools.Should().BeEmpty();
    }

    [Fact]
    public void ParseIntrospectionOutput_DeclaredDeps_UnmatchedConstraint_SetsFalse()
    {
        var json = """{"tools":[{"name":"node","version":"18.0.0","path":"/usr/bin/node"}]}""";
        var declared = new List<DependencySpec>
        {
            new() { Id = Guid.NewGuid(), TemplateId = Guid.NewGuid(), Name = "node", VersionConstraint = "20.*", Type = DependencyType.Runtime }
        };

        var tools = _detector.ParseIntrospectionOutput(json, declared);

        tools[0].DeclaredVersion.Should().Be("20.*");
        tools[0].MatchesDeclared.Should().BeFalse();
    }

    [Fact]
    public void GenerateIntrospectionScript_ContainsOsDetection()
    {
        var script = _detector.GenerateIntrospectionScript();

        script.Should().Contain("/etc/os-release");
        script.Should().Contain("uname");
        script.Should().Contain("\"tools\"");
    }

    [Fact]
    public void GenerateIntrospectionScript_ContainsAllToolDetection()
    {
        var script = _detector.GenerateIntrospectionScript();

        script.Should().Contain("dotnet");
        script.Should().Contain("python3");
        script.Should().Contain("node");
        script.Should().Contain("npm");
        script.Should().Contain("go");
        script.Should().Contain("rustc");
        script.Should().Contain("java");
        script.Should().Contain("git");
        script.Should().Contain("curl");
        script.Should().Contain("code-server");
    }
}
