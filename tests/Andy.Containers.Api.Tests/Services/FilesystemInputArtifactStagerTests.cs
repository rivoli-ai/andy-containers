// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Configurator;
using Andy.Containers.Infrastructure.Audit;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// EX.7 (rivoli-ai/andy-containers#328). Stager orchestration over
// IAndyDocsClient.DownloadAsync (fetch) + IContainerService.ExecAsync
// (write). The "integration" path here drives the full chain end-to-end
// with the test exec fakes: a docref is downloaded, base64-staged into the
// container via the exec channel, and we assert both the command shape
// (mkdir + base64 -d into the inputs root) and the failure mappings
// (missing / oversized / fetch-failed / write-failed → typed
// InputStagingException → run-start failure).
public class FilesystemInputArtifactStagerTests
{
    private const string Root = FilesystemInputArtifactStager.InputsRoot;

    private static Container ContainerFixture() => new()
    {
        Id = Guid.NewGuid(), Name = "c", OwnerId = "u", ExternalId = "ext-1",
    };

    // ----- no-op / unchanged behaviour -----

    [Fact]
    public async Task Stage_EmptyInputs_IsNoOp_NoExecNoDownload()
    {
        var containers = new Mock<IContainerService>();
        var docs = new Mock<IAndyDocsClient>();
        var stager = new FilesystemInputArtifactStager(
            containers.Object, NullLogger<FilesystemInputArtifactStager>.Instance, docs.Object);

        await stager.StageAsync(ContainerFixture(), Array.Empty<HeadlessInput>());

        containers.Verify(c => c.ExecAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
        docs.Verify(d => d.DownloadAsync(
            It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Stage_InputsButNoDocsClient_ThrowsDocsClientUnavailable()
    {
        var containers = new Mock<IContainerService>();
        var stager = new FilesystemInputArtifactStager(
            containers.Object, NullLogger<FilesystemInputArtifactStager>.Instance, andyDocs: null);

        var inputs = new[] { new HeadlessInput { DocsRef = Guid.NewGuid(), DestRelativePath = "a.txt" } };

        var act = () => stager.StageAsync(ContainerFixture(), inputs);

        var ex = (await act.Should().ThrowAsync<InputStagingException>()).Which;
        ex.Failure.Should().Be(InputStagingFailure.DocsClientUnavailable);
    }

    // ----- happy path (integration over the exec fakes) -----

    [Fact]
    public async Task Stage_OneInput_DownloadsAndWritesIntoInputsRoot()
    {
        var docId = Guid.NewGuid();
        var payload = Encoding.UTF8.GetBytes("hello prior task");
        var expectedB64 = Convert.ToBase64String(payload);

        var docs = new Mock<IAndyDocsClient>();
        docs.Setup(d => d.DownloadAsync(docId, It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DocumentDownloadResult.Ok(payload, "text/plain"));

        string? capturedCommand = null;
        var containers = new Mock<IContainerService>();
        containers.Setup(c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, TimeSpan, CancellationToken>((_, cmd, _, _) => capturedCommand = cmd)
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        var stager = new FilesystemInputArtifactStager(
            containers.Object, NullLogger<FilesystemInputArtifactStager>.Instance, docs.Object);

        var inputs = new[]
        {
            new HeadlessInput { DocsRef = docId, DestRelativePath = "prior/report.json" },
        };

        await stager.StageAsync(ContainerFixture(), inputs);

        // Downloaded exactly once, with the configured size cap.
        docs.Verify(d => d.DownloadAsync(
            docId, FilesystemInputArtifactStager.MaxInputSizeBytes, It.IsAny<CancellationToken>()),
            Times.Once);

        // The write command mkdir -p's the parent and decodes base64 into
        // the target path under the inputs root, carrying the exact bytes.
        capturedCommand.Should().NotBeNull();
        capturedCommand!.Should().Contain("mkdir -p");
        capturedCommand.Should().Contain($"{Root}/prior/report.json");
        capturedCommand.Should().Contain("base64 -d");
        capturedCommand.Should().Contain(expectedB64,
            "the decoded-in-container payload must match the downloaded bytes");
    }

    [Fact]
    public async Task Stage_MultipleInputs_WritesEach()
    {
        var d1 = Guid.NewGuid();
        var d2 = Guid.NewGuid();
        var docs = new Mock<IAndyDocsClient>();
        docs.Setup(d => d.DownloadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DocumentDownloadResult.Ok(new byte[] { 1, 2, 3 }, "application/octet-stream"));

        var containers = new Mock<IContainerService>();
        containers.Setup(c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        var stager = new FilesystemInputArtifactStager(
            containers.Object, NullLogger<FilesystemInputArtifactStager>.Instance, docs.Object);

        var inputs = new[]
        {
            new HeadlessInput { DocsRef = d1, DestRelativePath = "a.bin" },
            new HeadlessInput { DocsRef = d2, DestRelativePath = "sub/b.bin" },
        };

        await stager.StageAsync(ContainerFixture(), inputs);

        docs.Verify(d => d.DownloadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        containers.Verify(c => c.ExecAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // ----- failure mapping -----

    [Fact]
    public async Task Stage_DocumentNotFound_ThrowsNotFound()
    {
        var docId = Guid.NewGuid();
        var docs = new Mock<IAndyDocsClient>();
        docs.Setup(d => d.DownloadAsync(docId, It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DocumentDownloadResult.Fail(DocumentDownloadFailure.NotFound));

        var containers = new Mock<IContainerService>();
        var stager = new FilesystemInputArtifactStager(
            containers.Object, NullLogger<FilesystemInputArtifactStager>.Instance, docs.Object);

        var inputs = new[] { new HeadlessInput { DocsRef = docId, DestRelativePath = "a.txt" } };

        var ex = (await FluentActions.Awaiting(() => stager.StageAsync(ContainerFixture(), inputs))
            .Should().ThrowAsync<InputStagingException>()).Which;

        ex.Failure.Should().Be(InputStagingFailure.NotFound);
        ex.DocsRef.Should().Be(docId);
        // A failed fetch must not attempt to write into the container.
        containers.Verify(c => c.ExecAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Stage_DocumentTooLarge_ThrowsTooLarge()
    {
        var docId = Guid.NewGuid();
        var docs = new Mock<IAndyDocsClient>();
        docs.Setup(d => d.DownloadAsync(docId, It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DocumentDownloadResult.Fail(DocumentDownloadFailure.TooLarge));

        var containers = new Mock<IContainerService>();
        var stager = new FilesystemInputArtifactStager(
            containers.Object, NullLogger<FilesystemInputArtifactStager>.Instance, docs.Object);

        var inputs = new[] { new HeadlessInput { DocsRef = docId, DestRelativePath = "big.bin" } };

        var ex = (await FluentActions.Awaiting(() => stager.StageAsync(ContainerFixture(), inputs))
            .Should().ThrowAsync<InputStagingException>()).Which;

        ex.Failure.Should().Be(InputStagingFailure.TooLarge);
    }

    [Fact]
    public async Task Stage_FetchFailed_ThrowsFetchFailed()
    {
        var docId = Guid.NewGuid();
        var docs = new Mock<IAndyDocsClient>();
        docs.Setup(d => d.DownloadAsync(docId, It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DocumentDownloadResult.Fail(DocumentDownloadFailure.FetchFailed));

        var containers = new Mock<IContainerService>();
        var stager = new FilesystemInputArtifactStager(
            containers.Object, NullLogger<FilesystemInputArtifactStager>.Instance, docs.Object);

        var inputs = new[] { new HeadlessInput { DocsRef = docId, DestRelativePath = "a.txt" } };

        var ex = (await FluentActions.Awaiting(() => stager.StageAsync(ContainerFixture(), inputs))
            .Should().ThrowAsync<InputStagingException>()).Which;

        ex.Failure.Should().Be(InputStagingFailure.FetchFailed);
    }

    [Fact]
    public async Task Stage_ContainerWriteNonZeroExit_ThrowsWriteFailed()
    {
        var docId = Guid.NewGuid();
        var docs = new Mock<IAndyDocsClient>();
        docs.Setup(d => d.DownloadAsync(docId, It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DocumentDownloadResult.Ok(new byte[] { 1, 2, 3 }, "text/plain"));

        var containers = new Mock<IContainerService>();
        containers.Setup(c => c.ExecAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = 1, StdErr = "mkdir: permission denied" });

        var stager = new FilesystemInputArtifactStager(
            containers.Object, NullLogger<FilesystemInputArtifactStager>.Instance, docs.Object);

        var inputs = new[] { new HeadlessInput { DocsRef = docId, DestRelativePath = "a.txt" } };

        var ex = (await FluentActions.Awaiting(() => stager.StageAsync(ContainerFixture(), inputs))
            .Should().ThrowAsync<InputStagingException>()).Which;

        ex.Failure.Should().Be(InputStagingFailure.WriteFailed);
    }

    [Fact]
    public async Task Stage_TraversalDestThatBypassedBuilder_ThrowsWriteFailed()
    {
        // Defence in depth: a hand-constructed HeadlessInput that escaped
        // the builder's validation is re-checked by the stager and must
        // not write outside the inputs root.
        var docId = Guid.NewGuid();
        var docs = new Mock<IAndyDocsClient>();
        var containers = new Mock<IContainerService>();
        var stager = new FilesystemInputArtifactStager(
            containers.Object, NullLogger<FilesystemInputArtifactStager>.Instance, docs.Object);

        var inputs = new[] { new HeadlessInput { DocsRef = docId, DestRelativePath = "../../etc/passwd" } };

        var ex = (await FluentActions.Awaiting(() => stager.StageAsync(ContainerFixture(), inputs))
            .Should().ThrowAsync<InputStagingException>()).Which;

        ex.Failure.Should().Be(InputStagingFailure.WriteFailed);
        // Never downloaded, never wrote.
        docs.Verify(d => d.DownloadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
