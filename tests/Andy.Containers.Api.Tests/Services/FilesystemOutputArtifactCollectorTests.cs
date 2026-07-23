// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Audit;
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
    [InlineData("checkpoint.patch", "text/x-diff")]
    [InlineData("changes.diff", "text/x-diff")]
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
    public async Task CollectRun_ProbeIncludesOnlyCurrentRunDeliverableBundle()
    {
        string? command = null;
        var containers = new Mock<IContainerService>();
        containers
            .Setup(c => c.ExecAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, string, TimeSpan, CancellationToken>(
                (_, cmd, _, _) => command = cmd)
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = "" });
        var collector = new FilesystemOutputArtifactCollector(
            containers.Object,
            NullLogger<FilesystemOutputArtifactCollector>.Instance);
        var container = new Container
        {
            Id = Guid.NewGuid(), Name = "c", OwnerId = "u", ExternalId = "ext-1",
        };
        var runId = Guid.NewGuid();

        await collector.CollectRunAsync(container, runId);

        command.Should().Contain(
            $"{Root}/deliverables/{runId}/*");
        command.Should().Contain("! -path");
        command.Should().Contain(
            $"{Root}/deliverables/*",
            "older attempts must not be attributed to this run");
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

    // ----- rivoli-ai/andy-containers#320 byte-upload behaviour -----
    //
    // The probe pipeline (find | xargs sha256sum) is the FIRST exec; the
    // per-file base64 read is the SECOND-and-onward exec. We model both
    // by setup-ordering: ReturnsAsync chains responses, so the first
    // ExecAsync call lands on the probe response and subsequent calls
    // (one per artifact) land on the base64 response.

    private static Mock<IContainerService> MockExecPipeline(
        string probeStdOut, params string[] base64StdOuts)
    {
        var containers = new Mock<IContainerService>();
        var queue = new Queue<string>();
        queue.Enqueue(probeStdOut);
        foreach (var b in base64StdOuts) queue.Enqueue(b);

        containers
            .Setup(c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ExecResult { ExitCode = 0, StdOut = queue.Dequeue() });
        return containers;
    }

    [Fact]
    public async Task Collect_WithAndyDocsClient_PopulatesDocsRefOnEachArtifact()
    {
        // Two probe hits → two base64 reads → two andy-docs uploads;
        // every emitted artifact has its DocsRef stamped from the
        // client's response.
        var sha1 = new string('1', 64);
        var sha2 = new string('2', 64);
        var probe = string.Join('\n', new[]
        {
            $"3\t{sha1}\t{Root}/a.txt",
            $"3\t{sha2}\t{Root}/b.txt",
        });
        var b64 = Convert.ToBase64String(new byte[] { 0x61, 0x62, 0x63 }); // "abc"

        var containers = MockExecPipeline(probe, b64, b64);

        var fixedDocId = Guid.NewGuid();
        var fixedLinkId = Guid.NewGuid();
        var docs = new Mock<IAndyDocsClient>();
        docs.Setup(d => d.UploadAsync(It.IsAny<UploadRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocsRef(fixedDocId, fixedLinkId));

        var collector = new FilesystemOutputArtifactCollector(
            containers.Object,
            NullLogger<FilesystemOutputArtifactCollector>.Instance,
            docs.Object);
        var container = new Container
        {
            Id = Guid.NewGuid(), Name = "c", OwnerId = "u", ExternalId = "ext-1",
        };

        var artifacts = await collector.CollectAsync(container);

        artifacts.Should().HaveCount(2);
        artifacts.Should().OnlyContain(a =>
            a.DocsRef != null
            && a.DocsRef.DocumentId == fixedDocId
            && a.DocsRef.LinkId == fixedLinkId);
        docs.Verify(d => d.UploadAsync(It.IsAny<UploadRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task Collect_WithAndyDocsClient_PassesCorrectUploadRequestFields()
    {
        // Capture the UploadRequest and assert it carries the
        // expected MimeType / Name / Digest / Links shape (Run target,
        // concrete run id, Output role).
        var sha = new string('a', 64);
        var probe = $"3\t{sha}\t{Root}/report.pdf";
        var b64 = Convert.ToBase64String(new byte[] { 1, 2, 3 });

        var containers = MockExecPipeline(probe, b64);

        UploadRequest? captured = null;
        var docs = new Mock<IAndyDocsClient>();
        docs.Setup(d => d.UploadAsync(It.IsAny<UploadRequest>(), It.IsAny<CancellationToken>()))
            .Callback<UploadRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new DocsRef(Guid.NewGuid(), Guid.NewGuid()));

        var containerId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var collector = new FilesystemOutputArtifactCollector(
            containers.Object,
            NullLogger<FilesystemOutputArtifactCollector>.Instance,
            docs.Object);
        var container = new Container
        {
            Id = containerId, Name = "c", OwnerId = "u", ExternalId = "ext-1",
        };

        var artifacts = await collector.CollectRunAsync(container, runId);

        artifacts.Should().HaveCount(1);
        captured.Should().NotBeNull();
        captured!.Name.Should().Be("report.pdf");
        captured.MimeType.Should().Be("application/pdf");
        captured.Digest.Should().Be(sha);
        captured.Content.ToArray().Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
        captured.Links.Should().HaveCount(1);
        captured.Links[0].TargetType.Should().Be("Run");
        captured.Links[0].TargetId.Should().Be(runId.ToString());
        captured.Links[0].Role.Should().Be("Output");
    }

    [Fact]
    public async Task Collect_WithAndyDocsClientThrowing_FallsBackToMetadataOnly()
    {
        // Per-artifact try/catch: one bad upload must not blow away the
        // rest of the collection. We model that with a client that
        // throws on every call — every emitted artifact still has its
        // metadata, but DocsRef is null.
        var sha1 = new string('1', 64);
        var sha2 = new string('2', 64);
        var probe = string.Join('\n', new[]
        {
            $"3\t{sha1}\t{Root}/a.txt",
            $"3\t{sha2}\t{Root}/b.txt",
        });
        var b64 = Convert.ToBase64String(new byte[] { 1, 2, 3 });

        var containers = MockExecPipeline(probe, b64, b64);

        var docs = new Mock<IAndyDocsClient>();
        docs.Setup(d => d.UploadAsync(It.IsAny<UploadRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("andy-docs unreachable"));

        var collector = new FilesystemOutputArtifactCollector(
            containers.Object,
            NullLogger<FilesystemOutputArtifactCollector>.Instance,
            docs.Object);
        var container = new Container
        {
            Id = Guid.NewGuid(), Name = "c", OwnerId = "u", ExternalId = "ext-1",
        };

        var artifacts = await collector.CollectAsync(container);

        artifacts.Should().HaveCount(2);
        artifacts.Should().OnlyContain(a => a.DocsRef == null);
        artifacts.Select(a => a.RelativePath).Should().BeEquivalentTo(new[] { "a.txt", "b.txt" });
    }

    [Fact]
    public async Task Collect_WithAndyDocsClientReturningNull_LeavesDocsRefNull()
    {
        // Per the IAndyDocsClient contract, transient failure ==
        // UploadAsync returns null (not throws). The collector must
        // still emit the artifact, just metadata-only.
        var sha = new string('a', 64);
        var probe = $"3\t{sha}\t{Root}/a.txt";
        var b64 = Convert.ToBase64String(new byte[] { 1, 2, 3 });

        var containers = MockExecPipeline(probe, b64);

        var docs = new Mock<IAndyDocsClient>();
        docs.Setup(d => d.UploadAsync(It.IsAny<UploadRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocsRef?)null);

        var collector = new FilesystemOutputArtifactCollector(
            containers.Object,
            NullLogger<FilesystemOutputArtifactCollector>.Instance,
            docs.Object);
        var container = new Container
        {
            Id = Guid.NewGuid(), Name = "c", OwnerId = "u", ExternalId = "ext-1",
        };

        var artifacts = await collector.CollectAsync(container);

        artifacts.Should().HaveCount(1);
        artifacts[0].DocsRef.Should().BeNull();
        artifacts[0].RelativePath.Should().Be("a.txt");
    }

    [Fact]
    public async Task Collect_WithoutAndyDocsClient_PreservesPreCenturyBehaviour()
    {
        // Null IAndyDocsClient → pure metadata-only mode. No DocsRef
        // populated, no extra exec round-trips beyond the probe.
        var sha = new string('a', 64);
        var probe = $"3\t{sha}\t{Root}/a.txt";

        var containers = new Mock<IContainerService>();
        containers
            .Setup(c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0, StdOut = probe });

        var collector = new FilesystemOutputArtifactCollector(
            containers.Object,
            NullLogger<FilesystemOutputArtifactCollector>.Instance,
            andyDocs: null);
        var container = new Container
        {
            Id = Guid.NewGuid(), Name = "c", OwnerId = "u", ExternalId = "ext-1",
        };

        var artifacts = await collector.CollectAsync(container);

        artifacts.Should().HaveCount(1);
        artifacts[0].DocsRef.Should().BeNull();
        // Only the probe call, no base64 read.
        containers.Verify(
            c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Collect_BytesReadFailure_FallsBackToMetadataOnlyButContinues()
    {
        // The probe succeeds and returns two artifacts. The base64 read
        // of the FIRST file fails (exec returns non-zero); the SECOND
        // succeeds. The first artifact is metadata-only, the second has
        // its DocsRef populated — proving the per-file isolation.
        var sha1 = new string('1', 64);
        var sha2 = new string('2', 64);
        var probe = string.Join('\n', new[]
        {
            $"3\t{sha1}\t{Root}/a.txt",
            $"3\t{sha2}\t{Root}/b.txt",
        });
        var b64Good = Convert.ToBase64String(new byte[] { 1, 2, 3 });

        var containers = new Mock<IContainerService>();
        var calls = 0;
        containers
            .Setup(c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                calls++;
                return calls switch
                {
                    1 => new ExecResult { ExitCode = 0, StdOut = probe },
                    2 => new ExecResult { ExitCode = 1, StdErr = "cat: no such file" },
                    3 => new ExecResult { ExitCode = 0, StdOut = b64Good },
                    _ => new ExecResult { ExitCode = 0, StdOut = "" },
                };
            });

        var docs = new Mock<IAndyDocsClient>();
        docs.Setup(d => d.UploadAsync(It.IsAny<UploadRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocsRef(Guid.NewGuid(), Guid.NewGuid()));

        var collector = new FilesystemOutputArtifactCollector(
            containers.Object,
            NullLogger<FilesystemOutputArtifactCollector>.Instance,
            docs.Object);
        var container = new Container
        {
            Id = Guid.NewGuid(), Name = "c", OwnerId = "u", ExternalId = "ext-1",
        };

        var artifacts = await collector.CollectAsync(container);

        artifacts.Should().HaveCount(2);
        artifacts.Single(a => a.RelativePath == "a.txt").DocsRef.Should().BeNull(
            "first file's bytes read failed → metadata-only");
        artifacts.Single(a => a.RelativePath == "b.txt").DocsRef.Should().NotBeNull(
            "second file's read + upload succeeded");
        // Upload was attempted only once (the second file).
        docs.Verify(d => d.UploadAsync(It.IsAny<UploadRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
