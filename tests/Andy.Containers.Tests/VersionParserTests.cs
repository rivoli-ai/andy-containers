using Andy.Containers.Api.Services;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests;

public class VersionParserTests
{
    // --- .NET SDK ---

    [Fact]
    public void ParseDotnetSdk_StandardOutput_ShouldReturnVersion()
    {
        var output = "8.0.404 [/usr/share/dotnet/sdk]";
        VersionParser.ParseDotnetSdk(output).Should().Be("8.0.404");
    }

    [Fact]
    public void ParseDotnetSdk_MultipleSdks_ShouldReturnFirst()
    {
        var output = "8.0.404 [/usr/share/dotnet/sdk]\n9.0.100 [/usr/share/dotnet/sdk]";
        VersionParser.ParseDotnetSdk(output).Should().Be("8.0.404");
    }

    [Fact]
    public void ParseDotnetSdk_Null_ShouldReturnNull()
    {
        VersionParser.ParseDotnetSdk(null).Should().BeNull();
    }

    [Fact]
    public void ParseDotnetSdk_Empty_ShouldReturnNull()
    {
        VersionParser.ParseDotnetSdk("").Should().BeNull();
        VersionParser.ParseDotnetSdk("   ").Should().BeNull();
    }

    // --- .NET Runtime ---

    [Fact]
    public void ParseDotnetRuntime_StandardOutput_ShouldReturnVersion()
    {
        var output = "Microsoft.NETCore.App 8.0.11 [/usr/share/dotnet/shared/Microsoft.NETCore.App]";
        VersionParser.ParseDotnetRuntime(output).Should().Be("8.0.11");
    }

    [Fact]
    public void ParseDotnetRuntime_MultipleRuntimes_ShouldReturnNETCoreApp()
    {
        var output = "Microsoft.AspNetCore.App 8.0.11 [/usr/share/dotnet/shared/...]\nMicrosoft.NETCore.App 8.0.11 [/usr/share/dotnet/shared/...]";
        VersionParser.ParseDotnetRuntime(output).Should().Be("8.0.11");
    }

    [Fact]
    public void ParseDotnetRuntime_NoNETCoreApp_ShouldReturnNull()
    {
        var output = "Some.Other.Runtime 1.0.0 [/path]";
        VersionParser.ParseDotnetRuntime(output).Should().BeNull();
    }

    // --- Python ---

    [Fact]
    public void ParsePython_StandardOutput_ShouldReturnVersion()
    {
        VersionParser.ParsePython("Python 3.12.8").Should().Be("3.12.8");
    }

    [Fact]
    public void ParsePython_WithTrailingNewline_ShouldReturnVersion()
    {
        VersionParser.ParsePython("Python 3.12.8\n").Should().Be("3.12.8");
    }

    [Fact]
    public void ParsePython_UnexpectedFormat_ShouldReturnNull()
    {
        VersionParser.ParsePython("not python output").Should().BeNull();
    }

    // --- Node.js ---

    [Fact]
    public void ParseNode_WithLeadingV_ShouldStripAndReturn()
    {
        VersionParser.ParseNode("v20.18.1").Should().Be("20.18.1");
    }

    [Fact]
    public void ParseNode_WithTrailingWhitespace_ShouldTrim()
    {
        VersionParser.ParseNode("v20.18.1\n").Should().Be("20.18.1");
    }

    [Fact]
    public void ParseNode_Null_ShouldReturnNull()
    {
        VersionParser.ParseNode(null).Should().BeNull();
    }

    // --- npm ---

    [Fact]
    public void ParseNpm_StandardOutput_ShouldReturnVersion()
    {
        VersionParser.ParseNpm("10.8.2").Should().Be("10.8.2");
    }

    [Fact]
    public void ParseNpm_WithTrailingNewline_ShouldTrim()
    {
        VersionParser.ParseNpm("10.8.2\n").Should().Be("10.8.2");
    }

    // --- Go ---

    [Fact]
    public void ParseGo_StandardOutput_ShouldReturnVersion()
    {
        VersionParser.ParseGo("go version go1.22.5 linux/amd64").Should().Be("1.22.5");
    }

    [Fact]
    public void ParseGo_Null_ShouldReturnNull()
    {
        VersionParser.ParseGo(null).Should().BeNull();
    }

    // --- Rust ---

    [Fact]
    public void ParseRust_StandardOutput_ShouldReturnVersion()
    {
        VersionParser.ParseRust("rustc 1.82.0 (f6e511eec 2024-10-15)").Should().Be("1.82.0");
    }

    [Fact]
    public void ParseRust_UnexpectedFormat_ShouldReturnNull()
    {
        VersionParser.ParseRust("not rustc output").Should().BeNull();
    }

    // --- Java ---

    [Fact]
    public void ParseJava_OpenJdk_ShouldReturnVersion()
    {
        var output = "openjdk 21.0.4 2024-07-16 LTS\nOpenJDK Runtime Environment...";
        VersionParser.ParseJava(output).Should().Be("21.0.4");
    }

    [Fact]
    public void ParseJava_Null_ShouldReturnNull()
    {
        VersionParser.ParseJava(null).Should().BeNull();
    }

    // --- Git ---

    [Fact]
    public void ParseGit_StandardOutput_ShouldReturnVersion()
    {
        VersionParser.ParseGit("git version 2.43.0").Should().Be("2.43.0");
    }

    [Fact]
    public void ParseGit_WithSuffix_ShouldReturnVersion()
    {
        VersionParser.ParseGit("git version 2.43.0.windows.1").Should().Be("2.43.0");
    }

    // --- Docker ---

    [Fact]
    public void ParseDocker_StandardOutput_ShouldReturnVersion()
    {
        VersionParser.ParseDocker("Docker version 27.3.1, build ce12230").Should().Be("27.3.1");
    }

    // --- kubectl ---

    [Fact]
    public void ParseKubectl_JsonOutput_ShouldReturnVersion()
    {
        var json = """{"clientVersion":{"gitVersion":"v1.29.2","major":"1","minor":"29"}}""";
        VersionParser.ParseKubectl(json).Should().Be("1.29.2");
    }

    [Fact]
    public void ParseKubectl_InvalidJson_ShouldReturnNull()
    {
        VersionParser.ParseKubectl("not json").Should().BeNull();
    }

    [Fact]
    public void ParseKubectl_MissingField_ShouldReturnNull()
    {
        VersionParser.ParseKubectl("""{"serverVersion":{}}""").Should().BeNull();
    }

    // --- code-server ---

    [Fact]
    public void ParseCodeServer_MultiLine_ShouldReturnFirstLine()
    {
        VersionParser.ParseCodeServer("4.96.2\nf1b07c287bc7b8e15c0f04f6db1f2022dea5e2e2\nwith Code 1.96.2").Should().Be("4.96.2");
    }

    // --- OpenSSH ---

    [Fact]
    public void ParseOpenSsh_StderrOutput_ShouldReturnVersion()
    {
        VersionParser.ParseOpenSsh("OpenSSH_9.6p1 Ubuntu-3ubuntu13.5, OpenSSL 3.0.13").Should().Be("9.6p1");
    }

    // --- curl ---

    [Fact]
    public void ParseCurl_StandardOutput_ShouldReturnVersion()
    {
        VersionParser.ParseCurl("curl 8.5.0 (x86_64-pc-linux-gnu) libcurl/8.5.0").Should().Be("8.5.0");
    }

    // --- Angular CLI ---

    [Fact]
    public void ParseAngularCli_InVersionOutput_ShouldReturnVersion()
    {
        var output = """
            Angular CLI: 18.2.12
            Node: 20.18.1
            Package Manager: npm 10.8.2
            """;
        VersionParser.ParseAngularCli(output).Should().Be("18.2.12");
    }

    [Fact]
    public void ParseAngularCli_NoMatch_ShouldReturnNull()
    {
        VersionParser.ParseAngularCli("no angular here").Should().BeNull();
    }

    // --- dpkg-query ---

    [Fact]
    public void ParseDpkgQuery_StandardOutput_ShouldReturnPackages()
    {
        var output = "libssl3\t3.0.13-0ubuntu3.5\tamd64\ncurl\t8.5.0-2ubuntu10.6\tamd64";
        var packages = VersionParser.ParseDpkgQuery(output);

        packages.Should().HaveCount(2);
        packages[0].Name.Should().Be("libssl3");
        packages[0].Version.Should().Be("3.0.13-0ubuntu3.5");
        packages[0].Architecture.Should().Be("amd64");
        packages[1].Name.Should().Be("curl");
    }

    [Fact]
    public void ParseDpkgQuery_TwoColumns_ShouldReturnWithoutArch()
    {
        var output = "bash\t5.2.21";
        var packages = VersionParser.ParseDpkgQuery(output);

        packages.Should().HaveCount(1);
        packages[0].Name.Should().Be("bash");
        packages[0].Architecture.Should().BeNull();
    }

    [Fact]
    public void ParseDpkgQuery_Empty_ShouldReturnEmptyList()
    {
        VersionParser.ParseDpkgQuery("").Should().BeEmpty();
        VersionParser.ParseDpkgQuery(null).Should().BeEmpty();
    }

    // --- os-release ---

    [Fact]
    public void ParseOsRelease_StandardUbuntu_ShouldReturnOsInfo()
    {
        var output = """
            NAME="Ubuntu"
            VERSION_ID="24.04"
            VERSION_CODENAME=noble
            ID=ubuntu
            """;
        var info = VersionParser.ParseOsRelease(output);

        info.Should().NotBeNull();
        info!.Name.Should().Be("Ubuntu");
        info.Version.Should().Be("24.04");
        info.Codename.Should().Be("noble");
    }

    [Fact]
    public void ParseOsRelease_MissingFields_ShouldReturnDefaults()
    {
        var output = "ID=alpine";
        var info = VersionParser.ParseOsRelease(output);

        info.Should().NotBeNull();
        info!.Name.Should().Be("Unknown");
        info.Version.Should().Be("Unknown");
    }

    [Fact]
    public void ParseOsRelease_Null_ShouldReturnNull()
    {
        VersionParser.ParseOsRelease(null).Should().BeNull();
    }

    // --- Edge cases ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AllParsers_NullOrEmpty_ShouldReturnNull(string? input)
    {
        VersionParser.ParseDotnetSdk(input).Should().BeNull();
        VersionParser.ParseDotnetRuntime(input).Should().BeNull();
        VersionParser.ParsePython(input).Should().BeNull();
        VersionParser.ParseNode(input).Should().BeNull();
        VersionParser.ParseNpm(input).Should().BeNull();
        VersionParser.ParseGo(input).Should().BeNull();
        VersionParser.ParseRust(input).Should().BeNull();
        VersionParser.ParseJava(input).Should().BeNull();
        VersionParser.ParseGit(input).Should().BeNull();
        VersionParser.ParseDocker(input).Should().BeNull();
        VersionParser.ParseKubectl(input).Should().BeNull();
        VersionParser.ParseCodeServer(input).Should().BeNull();
        VersionParser.ParseOpenSsh(input).Should().BeNull();
        VersionParser.ParseCurl(input).Should().BeNull();
        VersionParser.ParseAngularCli(input).Should().BeNull();
    }
}
