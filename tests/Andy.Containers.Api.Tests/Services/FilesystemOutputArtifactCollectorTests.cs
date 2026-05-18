// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// rivoli-ai/andy-containers#316. Two test surfaces here:
//   * ParseProbeOutput — pure parser, exhaustively covered (empty,
//     single, nested, hidden, malformed lines, paths-with-tabs,
//     paths-outside-root). No I/O.
//   * CollectAsync — orchestration over IContainerService.ExecAsync.
//     Mock the exec, assert the right command flows in and the
//     output flows out. Failure paths collapse to empty list per the
//     IOutputArtifactCollector contract.
public class FilesystemOutputArtifactCollectorTests
{
    private const string Root = FilesystemOutputArtifactCollector.OutputsRoot;

    // ----- ParseProbeOutput -----

    [Fact]
    public void Parse_EmptyOutput_ReturnsEmpty()
    {
        FilesystemOutputArtifactCollector.ParseProbeOutput("")
            .Should().BeEmpty();
        FilesystemOutputArtifactCollector.ParseProbeOutput(null)
            .Should().BeEmpty();
        FilesystemOutputArtifactCollector.ParseProbeOutput("   \n  \n")
            .Should().BeEmpty();
    }

    [Fact]
    public void Parse_SingleFile_ReturnsOneArtifactWithRelativePath()
    {
        // size=42, sha=64 hex chars, absolute path under root.
        var sha = new string('a', 64);
        var line = $"42\t{sha}\t{Root}/report.pdf";

        var artifacts = FilesystemOutputArtifactCollector.ParseProbeOutput(line);

        artifacts.Should().HaveCount(1);
        artifacts[0].Name.Should().Be("report.pdf");
        artifacts[0].RelativePath.Should().Be("report.pdf");
        artifacts[0].SizeBytes.Should().Be(42);
        artifacts[0].Sha256.Should().Be(sha);
        artifacts[0].ContentType.Should().Be("application/pdf");
    }

    [Fact]
    public void Parse_NestedFile_ReturnsRelativePathWithSubdir()
    {
        var sha = new string('b', 64);
        var line = $"100\t{sha}\t{Root}/sub/dir/data.json";

        var artifacts = FilesystemOutputArtifactCollector.ParseProbeOutput(line);

        artifacts.Should().HaveCount(1);
        artifacts[0].RelativePath.Should().Be("sub/dir/data.json");
        artifacts[0].Name.Should().Be("data.json");
        artifacts[0].ContentType.Should().Be("application/json");
    }

    [Fact]
    public void Parse_MultipleLines_ReturnsAllArtifacts()
    {
        var sha1 = new string('1', 64);
        var sha2 = new string('2', 64);
        var sha3 = new string('3', 64);
        var stdout = string.Join('\n', new[]
        {
            $"10\t{sha1}\t{Root}/a.txt",
            $"20\t{sha2}\t{Root}/sub/b.log",
            $"30\t{sha3}\t{Root}/c.zip",
        });

        var artifacts = FilesystemOutputArtifactCollector.ParseProbeOutput(stdout);

        artifacts.Should().HaveCount(3);
        artifacts.Select(a => a.RelativePath).Should().BeEquivalentTo(
            new[] { "a.txt", "sub/b.log", "c.zip" });
    }

    [Fact]
    public void Parse_HiddenFile_IsIncluded()
    {
        // Hidden files (leading dot) under the outputs root ARE
        // included — the agent might legitimately want to publish a
        // `.metadata` file alongside outputs. The `.andy` parent dir
        // is just the chosen prefix; nothing magical about the dot
        // inside the user's tree. Documented: included.
        var sha = new string('c', 64);
        var line = $"5\t{sha}\t{Root}/.metadata";

        var artifacts = FilesystemOutputArtifactCollector.ParseProbeOutput(line);

        artifacts.Should().HaveCount(1);
        artifacts[0].Name.Should().Be(".metadata");
        artifacts[0].RelativePath.Should().Be(".metadata");
    }

    [Fact]
    public void Parse_MalformedLines_AreSkipped()
    {
        var sha = new string('d', 64);
        var stdout = string.Join('\n', new[]
        {
            "notanumber\tdeadbeef\t/x",                          // bad size
            $"42\tshort-sha\t{Root}/x.txt",                       // sha not 64 chars
            $"42\t{sha}",                                          // missing path field
            $"42\t{sha}\t{Root}/legit.txt",                       // good line
            "",                                                    // empty
            "garbage line with no tabs",                           // no tabs
        });

        var artifacts = FilesystemOutputArtifactCollector.ParseProbeOutput(stdout);

        artifacts.Should().HaveCount(1);
        artifacts[0].RelativePath.Should().Be("legit.txt");
    }

    [Fact]
    public void Parse_PathOutsideRoot_IsSkipped()
    {
        // Symlink-followed paths that escape the outputs root must
        // not be mis-attributed as artifacts. The contract pins paths
        // relative to OutputsRoot; an absolute path elsewhere is
        // skipped silently.
        var sha = new string('e', 64);
        var line = $"42\t{sha}\t/etc/passwd";

        var artifacts = FilesystemOutputArtifactCollector.ParseProbeOutput(line);

        artifacts.Should().BeEmpty();
    }

    [Fact]
    public void Parse_PathWithEmbeddedTab_IsRecovered()
    {
        // POSIX filenames may contain tabs — the parser re-joins
        // trailing tab-separated fields back into the path so a
        // `weird\tname.txt` survives the round-trip.
        var sha = new string('f', 64);
        var line = $"7\t{sha}\t{Root}/weird\tname.txt";

        var artifacts = FilesystemOutputArtifactCollector.ParseProbeOutput(line);

        artifacts.Should().HaveCount(1);
        artifacts[0].RelativePath.Should().Be("weird\tname.txt");
        artifacts[0].Name.Should().Be("weird\tname.txt");
    }

    [Fact]
    public void Parse_TrailingCarriageReturns_AreTolerated()
    {
        // sh/awk on Windows-mounted volumes can emit CRLF. The parser
        // trims trailing \r so a cross-platform pipeline still produces
        // a clean manifest.
        var sha = new string('a', 64);
        var line = $"42\t{sha}\t{Root}/x.txt\r";

        var artifacts = FilesystemOutputArtifactCollector.ParseProbeOutput(line);

        artifacts.Should().HaveCount(1);
        artifacts[0].RelativePath.Should().Be("x.txt");
    }

    // ----- ContentType guessing -----

    [Theory]
    [InlineData("foo.txt", "text/plain")]
    [InlineData("foo.json", "application/json")]
    [InlineData("foo.pdf", "application/pdf")]
    [InlineData("foo.png", "image/png")]
    [InlineData("foo.tar.gz", "application/gzip")]  // by .gz extension
    [InlineData("foo", null)]                        // no extension
    [InlineData("foo.unknownext", null)]
    public void GuessContentType_HandlesCommonExtensions(string name, string? expected)
    {
        FilesystemOutputArtifactCollector.GuessContentType(name).Should().Be(expected);
    }

    // ----- CollectAsync orchestration -----

    [Fact]
    public async Task Collect_NoExternalId_ReturnsEmpty_WithoutExec()
    {
        var containers = new Mock<IContainerService>();
        var collector = new FilesystemOutputArtifactCollector(
            containers.Object, NullLogger<FilesystemOutputArtifactCollector>.Instance);

        var container = new Container
        {
            Id = Guid.NewGuid(),
            Name = "no-external",
            OwnerId = "u",
            ExternalId = null,
        };

        var artifacts = await collector.CollectAsync(container);

        artifacts.Should().BeEmpty();
        containers.Verify(
            c => c.ExecAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Collect_ExecExitsNonZero_ReturnsEmpty()
    {
        var containers = new Mock<IContainerService>();
        containers
            .Setup(c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 1, StdErr = "find: permission denied" });

        var collector = new FilesystemOutputArtifactCollector(
            containers.Object, NullLogger<FilesystemOutputArtifactCollector>.Instance);

        var container = new Container
        {
            Id = Guid.NewGuid(), Name = "c", OwnerId = "u", ExternalId = "ext-1",
        };

        var artifacts = await collector.CollectAsync(container);

        artifacts.Should().BeEmpty();
    }

    [Fact]
    public async Task Collect_ExecThrows_ReturnsEmpty()
    {
        var containers = new Mock<IContainerService>();
        containers
            .Setup(c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("docker daemon unreachable"));

        var collector = new FilesystemOutputArtifactCollector(
            containers.Object, NullLogger<FilesystemOutputArtifactCollector>.Instance);

        var container = new Container
        {
            Id = Guid.NewGuid(), Name = "c", OwnerId = "u", ExternalId = "ext-1",
        };

        var artifacts = await collector.CollectAsync(container);

        artifacts.Should().BeEmpty();
    }

    [Fact]
    public async Task Collect_HappyPath_ReturnsParsedArtifacts()
    {
        var sha1 = new string('1', 64);
        var sha2 = new string('2', 64);
        var stdout = string.Join('\n', new[]
        {
            $"100\t{sha1}\t{Root}/report.pdf",
            $"50\t{sha2}\t{Root}/logs/run.log",
        });

        var containers = new Mock<IContainerService>();
        containers
            .Setup(c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = stdout });

        var collector = new FilesystemOutputArtifactCollector(
            containers.Object, NullLogger<FilesystemOutputArtifactCollector>.Instance);

        var container = new Container
        {
            Id = Guid.NewGuid(), Name = "c", OwnerId = "u", ExternalId = "ext-1",
        };

        var artifacts = await collector.CollectAsync(container);

        artifacts.Should().HaveCount(2);
        artifacts.Should().Contain(a =>
            a.RelativePath == "report.pdf" && a.Sha256 == sha1 && a.SizeBytes == 100);
        artifacts.Should().Contain(a =>
            a.RelativePath == "logs/run.log" && a.Sha256 == sha2 && a.SizeBytes == 50);
    }

    [Fact]
    public async Task Collect_PropagatesCallerCancellation()
    {
        var containers = new Mock<IContainerService>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        containers
            .Setup(c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var collector = new FilesystemOutputArtifactCollector(
            containers.Object, NullLogger<FilesystemOutputArtifactCollector>.Instance);

        var container = new Container
        {
            Id = Guid.NewGuid(), Name = "c", OwnerId = "u", ExternalId = "ext-1",
        };

        var act = () => collector.CollectAsync(container, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
