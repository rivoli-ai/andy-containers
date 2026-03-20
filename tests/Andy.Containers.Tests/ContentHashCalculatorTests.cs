using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests;

public class ContentHashCalculatorTests
{
    [Fact]
    public void ComputeHash_IdenticalManifests_ShouldProduceSameHash()
    {
        var m1 = CreateManifest("sha256:base1", "amd64", new[] { ("python", "3.12.8"), ("git", "2.43.0") });
        var m2 = CreateManifest("sha256:base1", "amd64", new[] { ("python", "3.12.8"), ("git", "2.43.0") });

        ContentHashCalculator.ComputeHash(m1).Should().Be(ContentHashCalculator.ComputeHash(m2));
    }

    [Fact]
    public void ComputeHash_DifferentTools_ShouldProduceDifferentHash()
    {
        var m1 = CreateManifest("sha256:base1", "amd64", new[] { ("python", "3.12.8") });
        var m2 = CreateManifest("sha256:base1", "amd64", new[] { ("python", "3.11.9") });

        ContentHashCalculator.ComputeHash(m1).Should().NotBe(ContentHashCalculator.ComputeHash(m2));
    }

    [Fact]
    public void ComputeHash_ToolOrderDoesNotAffectHash()
    {
        var m1 = CreateManifest("sha256:base1", "amd64", new[] { ("python", "3.12.8"), ("git", "2.43.0") });
        var m2 = CreateManifest("sha256:base1", "amd64", new[] { ("git", "2.43.0"), ("python", "3.12.8") });

        ContentHashCalculator.ComputeHash(m1).Should().Be(ContentHashCalculator.ComputeHash(m2));
    }

    [Fact]
    public void ComputeHash_BaseImageDigestIncludedInHash()
    {
        var m1 = CreateManifest("sha256:base1", "amd64", new[] { ("python", "3.12.8") });
        var m2 = CreateManifest("sha256:base2", "amd64", new[] { ("python", "3.12.8") });

        ContentHashCalculator.ComputeHash(m1).Should().NotBe(ContentHashCalculator.ComputeHash(m2));
    }

    [Fact]
    public void ComputeHash_ArchitectureIncludedInHash()
    {
        var m1 = CreateManifest("sha256:base1", "amd64", new[] { ("python", "3.12.8") });
        var m2 = CreateManifest("sha256:base1", "arm64", new[] { ("python", "3.12.8") });

        ContentHashCalculator.ComputeHash(m1).Should().NotBe(ContentHashCalculator.ComputeHash(m2));
    }

    [Fact]
    public void ComputeHash_OsPackagesExcludedFromHash()
    {
        var m1 = CreateManifest("sha256:base1", "amd64", new[] { ("python", "3.12.8") });
        var m2 = CreateManifest("sha256:base1", "amd64", new[] { ("python", "3.12.8") });
        // Add different OS packages
        m1.OsPackages = new List<InstalledPackage>
        {
            new() { Name = "libssl3", Version = "3.0.13" }
        };
        m2.OsPackages = new List<InstalledPackage>
        {
            new() { Name = "libssl3", Version = "3.0.14" },
            new() { Name = "curl", Version = "8.5.0" }
        };

        ContentHashCalculator.ComputeHash(m1).Should().Be(ContentHashCalculator.ComputeHash(m2));
    }

    [Fact]
    public void ComputeHash_Format_ShouldBeSha256Prefix()
    {
        var manifest = CreateManifest("sha256:base1", "amd64", new[] { ("python", "3.12.8") });
        var hash = ContentHashCalculator.ComputeHash(manifest);

        hash.Should().StartWith("sha256:");
        hash.Should().HaveLength(7 + 64); // "sha256:" + 64 hex chars
    }

    [Fact]
    public void ComputeHash_EmptyTools_ShouldProduceValidHash()
    {
        var manifest = CreateManifest("sha256:base1", "amd64", Array.Empty<(string, string)>());
        var hash = ContentHashCalculator.ComputeHash(manifest);

        hash.Should().StartWith("sha256:");
        hash.Should().HaveLength(7 + 64);
    }

    [Fact]
    public void ComputeHash_IsDeterministic()
    {
        var manifest = CreateManifest("sha256:base1", "amd64", new[] { ("python", "3.12.8") });

        var hash1 = ContentHashCalculator.ComputeHash(manifest);
        var hash2 = ContentHashCalculator.ComputeHash(manifest);
        var hash3 = ContentHashCalculator.ComputeHash(manifest);

        hash1.Should().Be(hash2).And.Be(hash3);
    }

    private static ImageToolManifest CreateManifest(
        string baseImageDigest, string architecture, (string Name, string Version)[] tools)
    {
        return new ImageToolManifest
        {
            ImageContentHash = "",
            BaseImage = "ubuntu:24.04",
            BaseImageDigest = baseImageDigest,
            Architecture = architecture,
            OperatingSystem = new OsInfo
            {
                Name = "Ubuntu",
                Version = "24.04",
                Codename = "noble",
                KernelVersion = "6.5.0"
            },
            Tools = tools.Select(t => new InstalledTool
            {
                Name = t.Name,
                Version = t.Version,
                Type = DependencyType.Runtime
            }).ToList()
        };
    }
}
