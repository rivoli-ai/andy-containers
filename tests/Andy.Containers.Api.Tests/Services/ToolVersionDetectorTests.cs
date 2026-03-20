using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class ToolVersionDetectorTests
{
    private readonly Mock<IContainerService> _mockContainerService;
    private readonly Mock<IInfrastructureProviderFactory> _mockFactory;
    private readonly Mock<IInfrastructureProvider> _mockProvider;
    private readonly ToolVersionDetector _detector;

    public ToolVersionDetectorTests()
    {
        _mockContainerService = new Mock<IContainerService>();
        _mockFactory = new Mock<IInfrastructureProviderFactory>();
        _mockProvider = new Mock<IInfrastructureProvider>();
        _mockFactory.Setup(f => f.GetProvider(It.IsAny<ProviderType>())).Returns(_mockProvider.Object);
        var logger = new Mock<ILogger<ToolVersionDetector>>();
        _detector = new ToolVersionDetector(_mockContainerService.Object, _mockFactory.Object, logger.Object);
    }

    [Fact]
    public async Task IntrospectContainerAsync_ShouldCallExecWithScript()
    {
        var containerId = Guid.NewGuid();
        _mockContainerService.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = BuildSampleOutput() });

        await _detector.IntrospectContainerAsync(containerId);

        _mockContainerService.Verify(s => s.ExecAsync(containerId, It.Is<string>(cmd => cmd.Contains("#!/bin/sh")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IntrospectContainerAsync_ShouldParseToolsFromOutput()
    {
        var containerId = Guid.NewGuid();
        _mockContainerService.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = BuildSampleOutput() });

        var manifest = await _detector.IntrospectContainerAsync(containerId);

        manifest.Tools.Should().HaveCount(3);
        manifest.Tools.Should().Contain(t => t.Name == "python" && t.Version == "3.12.8");
        manifest.Tools.Should().Contain(t => t.Name == "git" && t.Version == "2.43.0");
        manifest.Tools.Should().Contain(t => t.Name == "curl" && t.Version == "8.5.0");
    }

    [Fact]
    public async Task IntrospectContainerAsync_ShouldParseArchitecture()
    {
        var containerId = Guid.NewGuid();
        _mockContainerService.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = BuildSampleOutput() });

        var manifest = await _detector.IntrospectContainerAsync(containerId);

        manifest.Architecture.Should().Be("amd64");
    }

    [Fact]
    public async Task IntrospectContainerAsync_ShouldNormalizeArm64()
    {
        var containerId = Guid.NewGuid();
        var output = "ARCH\taarch64\nKERNEL\t6.5.0\n";
        _mockContainerService.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = output });

        var manifest = await _detector.IntrospectContainerAsync(containerId);

        manifest.Architecture.Should().Be("arm64");
    }

    [Fact]
    public async Task IntrospectContainerAsync_ShouldParseOsInfo()
    {
        var containerId = Guid.NewGuid();
        _mockContainerService.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = BuildSampleOutput() });

        var manifest = await _detector.IntrospectContainerAsync(containerId);

        manifest.OperatingSystem.Name.Should().Be("Ubuntu");
        manifest.OperatingSystem.Version.Should().Be("24.04");
        manifest.OperatingSystem.Codename.Should().Be("noble");
        manifest.OperatingSystem.KernelVersion.Should().Be("6.5.0");
    }

    [Fact]
    public async Task IntrospectContainerAsync_ShouldParsePackages()
    {
        var containerId = Guid.NewGuid();
        _mockContainerService.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = BuildSampleOutput() });

        var manifest = await _detector.IntrospectContainerAsync(containerId);

        manifest.OsPackages.Should().HaveCount(2);
        manifest.OsPackages.Should().Contain(p => p.Name == "libssl3");
        manifest.OsPackages.Should().Contain(p => p.Name == "curl");
    }

    [Fact]
    public async Task IntrospectContainerAsync_WithDeclaredDeps_ShouldMatchVersions()
    {
        var containerId = Guid.NewGuid();
        _mockContainerService.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = BuildSampleOutput() });

        var declared = new List<DependencySpec>
        {
            new() { Name = "python", VersionConstraint = ">=3.12,<4.0", Type = DependencyType.Runtime },
            new() { Name = "git", VersionConstraint = "2.40.*", Type = DependencyType.Tool }
        };

        var manifest = await _detector.IntrospectContainerAsync(containerId, declared);

        var python = manifest.Tools.Single(t => t.Name == "python");
        python.DeclaredVersion.Should().Be(">=3.12,<4.0");
        python.MatchesDeclared.Should().BeTrue();

        var git = manifest.Tools.Single(t => t.Name == "git");
        git.DeclaredVersion.Should().Be("2.40.*");
        git.MatchesDeclared.Should().BeFalse(); // 2.43.0 doesn't match 2.40.*
    }

    [Fact]
    public async Task IntrospectContainerAsync_UndeclaredTool_ShouldHaveNullDeclaredVersion()
    {
        var containerId = Guid.NewGuid();
        _mockContainerService.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = BuildSampleOutput() });

        var declared = new List<DependencySpec>
        {
            new() { Name = "python", VersionConstraint = "3.12.*" }
        };

        var manifest = await _detector.IntrospectContainerAsync(containerId, declared);

        var curl = manifest.Tools.Single(t => t.Name == "curl");
        curl.DeclaredVersion.Should().BeNull();
        curl.MatchesDeclared.Should().BeTrue(); // undeclared = always matches
    }

    [Fact]
    public async Task IntrospectContainerAsync_NullDeclaredDeps_ShouldSetAllMatchesDeclaredTrue()
    {
        var containerId = Guid.NewGuid();
        _mockContainerService.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = BuildSampleOutput() });

        var manifest = await _detector.IntrospectContainerAsync(containerId);

        manifest.Tools.Should().AllSatisfy(t =>
        {
            t.DeclaredVersion.Should().BeNull();
            t.MatchesDeclared.Should().BeTrue();
        });
    }

    [Fact]
    public async Task IntrospectContainerAsync_ExecFailure_ShouldReturnEmptyManifest()
    {
        var containerId = Guid.NewGuid();
        _mockContainerService.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 1, StdErr = "permission denied" });

        var manifest = await _detector.IntrospectContainerAsync(containerId);

        manifest.Tools.Should().BeEmpty();
        manifest.Architecture.Should().Be("unknown");
    }

    [Fact]
    public async Task IntrospectContainerAsync_EmptyContainer_ShouldReturnEmptyToolsList()
    {
        var containerId = Guid.NewGuid();
        _mockContainerService.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "ARCH\tx86_64\nKERNEL\t6.5.0\n" });

        var manifest = await _detector.IntrospectContainerAsync(containerId);

        manifest.Tools.Should().BeEmpty();
        manifest.Architecture.Should().Be("amd64");
    }

    [Fact]
    public async Task IntrospectContainerAsync_ToolWithBinaryPath_ShouldCapturePath()
    {
        var containerId = Guid.NewGuid();
        var output = "ARCH\tx86_64\nKERNEL\t6.5.0\nTOOL\tgit\tgit version 2.43.0\t/usr/bin/git\n";
        _mockContainerService.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = output });

        var manifest = await _detector.IntrospectContainerAsync(containerId);

        var git = manifest.Tools.Single(t => t.Name == "git");
        git.BinaryPath.Should().Be("/usr/bin/git");
    }

    [Fact]
    public async Task IntrospectContainerAsync_ToolWithUnparseableOutput_ShouldBeSkipped()
    {
        var containerId = Guid.NewGuid();
        var output = "ARCH\tx86_64\nKERNEL\t6.5.0\nTOOL\tgit\tgarbageoutput\t/usr/bin/git\n";
        _mockContainerService.Setup(s => s.ExecAsync(containerId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = output });

        var manifest = await _detector.IntrospectContainerAsync(containerId);

        manifest.Tools.Should().BeEmpty();
    }

    [Fact]
    public async Task IntrospectImageAsync_ShouldCreateTempContainerAndDestroy()
    {
        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "temp-123" });
        _mockProvider.Setup(p => p.ExecAsync("temp-123", It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "ARCH\tx86_64\nKERNEL\t6.5.0\n" });

        await _detector.IntrospectImageAsync("ubuntu:24.04");

        _mockProvider.Verify(p => p.CreateContainerAsync(It.Is<ContainerSpec>(s => s.ImageReference == "ubuntu:24.04"), It.IsAny<CancellationToken>()), Times.Once);
        _mockProvider.Verify(p => p.StartContainerAsync("temp-123", It.IsAny<CancellationToken>()), Times.Once);
        _mockProvider.Verify(p => p.DestroyContainerAsync("temp-123", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IntrospectImageAsync_WhenExecFails_ShouldStillDestroyContainer()
    {
        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "temp-456" });
        _mockProvider.Setup(p => p.ExecAsync("temp-456", It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("exec failed"));

        var act = () => _detector.IntrospectImageAsync("bad:image");

        await act.Should().ThrowAsync<InvalidOperationException>();
        _mockProvider.Verify(p => p.DestroyContainerAsync("temp-456", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IntrospectImageAsync_TempContainer_ShouldHaveIntrospectionLabels()
    {
        ContainerSpec? capturedSpec = null;
        _mockProvider.Setup(p => p.CreateContainerAsync(It.IsAny<ContainerSpec>(), It.IsAny<CancellationToken>()))
            .Callback<ContainerSpec, CancellationToken>((spec, _) => capturedSpec = spec)
            .ReturnsAsync(new ContainerProvisionResult { ExternalId = "temp-789" });
        _mockProvider.Setup(p => p.ExecAsync("temp-789", It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "ARCH\tx86_64\nKERNEL\t6.5.0\n" });

        await _detector.IntrospectImageAsync("ubuntu:24.04");

        capturedSpec.Should().NotBeNull();
        capturedSpec!.Labels.Should().ContainKey("andy.purpose").WhoseValue.Should().Be("introspection");
        capturedSpec.Labels.Should().ContainKey("andy.ephemeral").WhoseValue.Should().Be("true");
    }

    // --- ParseIntrospectionOutput (internal, tested directly) ---

    [Fact]
    public void ParseIntrospectionOutput_DotnetSdk_ShouldParse()
    {
        var output = "ARCH\tx86_64\nKERNEL\t6.5.0\nTOOL\tdotnet-sdk\t8.0.404 [/usr/share/dotnet/sdk]\t/usr/bin/dotnet\n";
        var manifest = ToolVersionDetector.ParseIntrospectionOutput(output, null);

        manifest.Tools.Should().ContainSingle(t => t.Name == "dotnet-sdk" && t.Version == "8.0.404");
        manifest.Tools[0].Type.Should().Be(DependencyType.Sdk);
    }

    [Fact]
    public void ParseIntrospectionOutput_Node_ShouldStripLeadingV()
    {
        var output = "ARCH\tx86_64\nKERNEL\t6.5.0\nTOOL\tnode\tv20.18.1\t/usr/bin/node\n";
        var manifest = ToolVersionDetector.ParseIntrospectionOutput(output, null);

        manifest.Tools.Should().ContainSingle(t => t.Name == "node" && t.Version == "20.18.1");
    }

    [Fact]
    public void ParseIntrospectionOutput_Kubectl_ShouldParseJson()
    {
        var kubectlJson = """{"clientVersion":{"gitVersion":"v1.29.2","major":"1","minor":"29"}}""";
        var output = $"ARCH\tx86_64\nKERNEL\t6.5.0\nTOOL\tkubectl\t{kubectlJson}\t/usr/bin/kubectl\n";
        var manifest = ToolVersionDetector.ParseIntrospectionOutput(output, null);

        manifest.Tools.Should().ContainSingle(t => t.Name == "kubectl" && t.Version == "1.29.2");
    }

    [Fact]
    public void ParseIntrospectionOutput_MissingOsRelease_ShouldUseDefaults()
    {
        var output = "ARCH\tx86_64\nKERNEL\t6.5.0\n";
        var manifest = ToolVersionDetector.ParseIntrospectionOutput(output, null);

        manifest.OperatingSystem.Name.Should().Be("Unknown");
        manifest.OperatingSystem.KernelVersion.Should().Be("6.5.0");
    }

    private static string BuildSampleOutput()
    {
        return string.Join("\n",
            "ARCH\tx86_64",
            "KERNEL\t6.5.0",
            "OS_RELEASE\tNAME=\"Ubuntu\"|VERSION_ID=\"24.04\"|VERSION_CODENAME=noble|ID=ubuntu|",
            "TOOL\tpython\tPython 3.12.8\t/usr/bin/python3",
            "TOOL\tgit\tgit version 2.43.0\t/usr/bin/git",
            "TOOL\tcurl\tcurl 8.5.0 (x86_64-pc-linux-gnu) libcurl/8.5.0\t/usr/bin/curl",
            "PACKAGES\tlibssl3\t3.0.13\tamd64|curl\t8.5.0\tamd64|",
            "");
    }
}
