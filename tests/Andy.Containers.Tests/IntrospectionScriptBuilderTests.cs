using Andy.Containers.Api.Services;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests;

public class IntrospectionScriptBuilderTests
{
    [Fact]
    public void BuildScript_ShouldStartWithShebang()
    {
        var script = IntrospectionScriptBuilder.BuildScript();
        script.Should().StartWith("#!/bin/sh");
    }

    [Fact]
    public void BuildScript_ShouldDetectArchitecture()
    {
        var script = IntrospectionScriptBuilder.BuildScript();
        script.Should().Contain("uname -m");
        script.Should().Contain("ARCH");
    }

    [Fact]
    public void BuildScript_ShouldDetectKernel()
    {
        var script = IntrospectionScriptBuilder.BuildScript();
        script.Should().Contain("uname -r");
        script.Should().Contain("KERNEL");
    }

    [Fact]
    public void BuildScript_ShouldReadOsRelease()
    {
        var script = IntrospectionScriptBuilder.BuildScript();
        script.Should().Contain("/etc/os-release");
        script.Should().Contain("OS_RELEASE");
    }

    [Fact]
    public void BuildScript_ShouldCheckForEachKnownTool()
    {
        var script = IntrospectionScriptBuilder.BuildScript();

        foreach (var tool in ToolRegistry.KnownTools)
        {
            script.Should().Contain(tool.DetectionCommand, $"script should check for {tool.Name}");
            script.Should().Contain($"TOOL\\t{tool.Name}", $"script should output TOOL line for {tool.Name}");
        }
    }

    [Fact]
    public void BuildScript_ShouldUseCommandVForToolDetection()
    {
        var script = IntrospectionScriptBuilder.BuildScript();

        foreach (var tool in ToolRegistry.KnownTools)
        {
            script.Should().Contain($"command -v {tool.WhichCommand}", $"script should check if {tool.WhichCommand} exists");
        }
    }

    [Fact]
    public void BuildScript_ShouldCaptureBinaryPath()
    {
        var script = IntrospectionScriptBuilder.BuildScript();
        script.Should().Contain("_path=$(command -v");
    }

    [Fact]
    public void BuildScript_StderrTools_ShouldRedirectStderr()
    {
        var script = IntrospectionScriptBuilder.BuildScript();
        var stderrTools = ToolRegistry.KnownTools.Where(t => t.UsesStdErr).ToList();
        stderrTools.Should().NotBeEmpty("there should be tools that use stderr");

        foreach (var tool in stderrTools)
        {
            script.Should().Contain($"{tool.DetectionCommand} 2>&1", $"tool {tool.Name} should redirect stderr to stdout");
        }
    }

    [Fact]
    public void BuildScript_NonStderrTools_ShouldSuppressStderr()
    {
        var script = IntrospectionScriptBuilder.BuildScript();
        var normalTools = ToolRegistry.KnownTools.Where(t => !t.UsesStdErr).ToList();

        foreach (var tool in normalTools)
        {
            script.Should().Contain($"{tool.DetectionCommand} 2>/dev/null", $"tool {tool.Name} should suppress stderr");
        }
    }

    [Fact]
    public void BuildScript_ShouldDetectDpkgOrApk()
    {
        var script = IntrospectionScriptBuilder.BuildScript();
        script.Should().Contain("dpkg-query");
        script.Should().Contain("apk list --installed");
        script.Should().Contain("PACKAGES");
    }

    [Fact]
    public void BuildScript_ShouldEndWithNewline()
    {
        var script = IntrospectionScriptBuilder.BuildScript();
        script.Should().EndWith("\n");
    }
}
