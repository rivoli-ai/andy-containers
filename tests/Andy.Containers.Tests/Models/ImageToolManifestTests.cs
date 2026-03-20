using System.Text.Json;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Models;

public class ImageToolManifestTests
{
    [Fact]
    public void NewManifest_ShouldHaveIntrospectedAtSet()
    {
        var before = DateTime.UtcNow;
        var manifest = CreateManifest();
        var after = DateTime.UtcNow;

        manifest.IntrospectedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void NewManifest_ShouldHaveEmptyToolsAndPackages()
    {
        var manifest = CreateManifest();

        manifest.Tools.Should().BeEmpty();
        manifest.OsPackages.Should().BeEmpty();
    }

    [Fact]
    public void InstalledTool_ShouldDefaultMatchesDeclaredToTrue()
    {
        var tool = new InstalledTool { Name = "dotnet-sdk", Version = "8.0.404" };

        tool.MatchesDeclared.Should().BeTrue();
    }

    [Fact]
    public void InstalledTool_NullableProperties_ShouldBeNull()
    {
        var tool = new InstalledTool { Name = "dotnet-sdk", Version = "8.0.404" };

        tool.DeclaredVersion.Should().BeNull();
        tool.InstallPath.Should().BeNull();
        tool.BinaryPath.Should().BeNull();
        tool.SizeBytes.Should().BeNull();
    }

    [Fact]
    public void InstalledPackage_NullableProperties_ShouldBeNull()
    {
        var pkg = new InstalledPackage { Name = "libssl3", Version = "3.0.13" };

        pkg.Architecture.Should().BeNull();
        pkg.Source.Should().BeNull();
    }

    [Theory]
    [InlineData(DependencyType.Compiler)]
    [InlineData(DependencyType.Runtime)]
    [InlineData(DependencyType.Sdk)]
    [InlineData(DependencyType.Tool)]
    public void InstalledTool_ShouldAcceptRelevantDependencyTypes(DependencyType type)
    {
        var tool = new InstalledTool { Name = "test", Version = "1.0.0", Type = type };

        tool.Type.Should().Be(type);
    }

    [Fact]
    public void Manifest_ShouldRoundTripViaJson()
    {
        var manifest = new ImageToolManifest
        {
            ImageContentHash = "sha256:abc123",
            BaseImage = "ubuntu:24.04",
            BaseImageDigest = "sha256:def456",
            Architecture = "amd64",
            OperatingSystem = new OsInfo
            {
                Name = "Ubuntu",
                Version = "24.04",
                Codename = "noble",
                KernelVersion = "6.5.0"
            },
            Tools = new List<InstalledTool>
            {
                new()
                {
                    Name = "dotnet-sdk",
                    Version = "8.0.404",
                    Type = DependencyType.Sdk,
                    DeclaredVersion = "8.0.*",
                    MatchesDeclared = true,
                    InstallPath = "/usr/share/dotnet"
                },
                new()
                {
                    Name = "python",
                    Version = "3.12.8",
                    Type = DependencyType.Runtime
                }
            },
            OsPackages = new List<InstalledPackage>
            {
                new() { Name = "libssl3", Version = "3.0.13", Architecture = "amd64" }
            }
        };

        var json = JsonSerializer.Serialize(manifest);
        var deserialized = JsonSerializer.Deserialize<ImageToolManifest>(json);

        deserialized.Should().NotBeNull();
        deserialized!.ImageContentHash.Should().Be("sha256:abc123");
        deserialized.BaseImage.Should().Be("ubuntu:24.04");
        deserialized.Architecture.Should().Be("amd64");
        deserialized.OperatingSystem.Name.Should().Be("Ubuntu");
        deserialized.OperatingSystem.Version.Should().Be("24.04");
        deserialized.OperatingSystem.Codename.Should().Be("noble");
        deserialized.OperatingSystem.KernelVersion.Should().Be("6.5.0");
        deserialized.Tools.Should().HaveCount(2);
        deserialized.Tools[0].Name.Should().Be("dotnet-sdk");
        deserialized.Tools[0].Version.Should().Be("8.0.404");
        deserialized.Tools[0].Type.Should().Be(DependencyType.Sdk);
        deserialized.Tools[0].DeclaredVersion.Should().Be("8.0.*");
        deserialized.Tools[0].MatchesDeclared.Should().BeTrue();
        deserialized.Tools[0].InstallPath.Should().Be("/usr/share/dotnet");
        deserialized.Tools[1].Name.Should().Be("python");
        deserialized.Tools[1].Version.Should().Be("3.12.8");
        deserialized.OsPackages.Should().HaveCount(1);
        deserialized.OsPackages[0].Name.Should().Be("libssl3");
    }

    [Theory]
    [InlineData(ChangeSeverity.Build)]
    [InlineData(ChangeSeverity.Patch)]
    [InlineData(ChangeSeverity.Minor)]
    [InlineData(ChangeSeverity.Major)]
    public void ChangeSeverity_ShouldAcceptAllValues(ChangeSeverity severity)
    {
        severity.Should().BeDefined();
    }

    private static ImageToolManifest CreateManifest() => new()
    {
        ImageContentHash = "sha256:test",
        BaseImage = "ubuntu:24.04",
        BaseImageDigest = "sha256:base",
        Architecture = "amd64",
        OperatingSystem = new OsInfo
        {
            Name = "Ubuntu",
            Version = "24.04",
            Codename = "noble",
            KernelVersion = "6.5.0"
        }
    };
}
